using System.Security.Cryptography;
using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Flows;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Thread = ChatBranch.Domain.Thread;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// The website chat widget: the one inbound channel a customer can start from
/// with no phone number and no account.
///
/// Everything under <c>/api/widget/</c> is ANONYMOUS by necessity — the caller
/// is a stranger's browser on someone else's site. The guards that replace
/// authentication:
/// <list type="bullet">
/// <item>The <b>widget token</b> in the URL identifies the ACCOUNT, not the
/// visitor. It is public by design (it ships in a script tag) and grants
/// nothing but "start or continue a conversation on this site".</item>
/// <item>The <b>session id</b> is minted server-side per visitor and is the
/// only way to read a conversation back. Guessing one is guessing 256 bits.</item>
/// <item>Message text is length-capped, and a visitor cannot list threads,
/// read another session, or reach any other endpoint.</item>
/// </list>
///
/// A widget conversation is an ordinary <see cref="Thread"/> with
/// channel="website", so the unified inbox, teams, automations, AI replies and
/// analytics all work on it with no special-casing.
/// </summary>
public static class WidgetEndpoints
{
    private const int MaxMessageChars = 4000;

    public static void MapWidgetEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- config: what the widget should look like ------------------------
        app.MapGet("/api/widget/{token}/config", async (string token, AppDbContext db) =>
        {
            var account = await AccountAsync(db, token);
            if (account == null) return Results.NotFound(new { detail = "Unknown widget." });
            return Results.Ok(new
            {
                title = Cfg(account, "title", account.Name),
                greeting = Cfg(account, "greeting", "Hi! How can we help?"),
                accent = Cfg(account, "accent", "#3B82F6"),
                placeholder = Cfg(account, "placeholder", "Type your message…"),
                // The visitor is asked for a name/email only when configured to.
                ask_identity = Cfg(account, "ask_identity", "true") == "true",
            });
        }).AllowAnonymous().RequireCors("widget");

        // ---- start (or resume) a visitor session -----------------------------
        app.MapPost("/api/widget/{token}/session", async (string token, JsonElement body,
            AppDbContext db, ThreadStore store, ScopingService sc) =>
        {
            var account = await AccountAsync(db, token);
            if (account == null) return Results.NotFound(new { detail = "Unknown widget." });

            // An existing session id is honoured only if it really exists —
            // otherwise a visitor could invent one and claim someone's thread.
            var session = (Str(body, "session") ?? "").Trim();
            Thread? thread = session.Length > 0
                ? await db.Threads.FirstOrDefaultAsync(t => t.Channel == Channels.Website && t.Key == session)
                : null;

            if (thread == null)
            {
                session = NewSessionId();
                var name = Trim(Str(body, "name") ?? "", 80);
                var email = Trim(Str(body, "email") ?? "", 190);
                thread = store.GetOrCreateThread(Channels.Website, session,
                    name.Length > 0 ? name : "Website visitor", account.Id);
                thread.EntityId = account.EntityId;
                // the widget inbox's AI reply default (Channels → Who answers it)
                ChatBranch.Data.Channels.InboxSettings.ApplyToNewThread(db, thread);
                // A visitor who gave an email becomes a real contact so the
                // conversation is not orphaned the moment they close the tab.
                if (email.Length > 0 || name.Length > 0)
                    thread.ContactId = (await UpsertContactAsync(db, name, email))?.Id;
                thread.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }

            return Results.Ok(new
            {
                session = thread.Key,
                messages = store.GetMessages(thread, 50, null)
                    .Where(m => !m.Private)
                    .Select(WidgetMessage).ToList(),
            });
        }).AllowAnonymous().RequireCors("widget");

        // ---- visitor sends a message -----------------------------------------
        app.MapPost("/api/widget/{token}/messages", async (string token, JsonElement body,
            AppDbContext db, ThreadStore store, InboundProcessor inbound, FlowEngine engine,
            AutomationService automation) =>
        {
            var account = await AccountAsync(db, token);
            if (account == null) return Results.NotFound(new { detail = "Unknown widget." });

            var session = (Str(body, "session") ?? "").Trim();
            var text = Trim(Str(body, "text") ?? "", MaxMessageChars);
            if (session.Length == 0 || text.Length == 0)
                return Results.Json(new { detail = "session and text are required." }, statusCode: 400);

            var thread = await db.Threads.FirstOrDefaultAsync(
                t => t.Channel == Channels.Website && t.Key == session);
            if (thread == null)
                return Results.Json(new { detail = "This conversation has expired — reload the page." },
                                    statusCode: 404);

            var msg = store.AppendMessage(thread, MessageDirection.In, MessageSource.Customer,
                text, status: "received", accountId: account.Id);

            // Same post-inbound path as every other channel: flows resume, the
            // conversation routes to a team, automations run, the AI may reply.
            try { engine.OnInboundMessage(Channels.Website, session, text); } catch { }
            await TeamsEndpoints.AutoAssignAsync(db, thread);
            var firstInbound = db.Messages.Count(m => m.ThreadId == thread.Id
                                                      && m.Direction == MessageDirection.In) <= 1;
            if (firstInbound) automation.Run("conversation_created", thread, text, true);
            automation.Run("message_created", thread, text, firstInbound);
            await NotificationService.NotifyInboundMessageAsync(db, thread, text);
            inbound.MaybeAutoReply(thread);

            return Results.Ok(new { message = WidgetMessage(msg) });
        }).AllowAnonymous().RequireCors("widget");

        // ---- visitor polls for replies ---------------------------------------
        app.MapGet("/api/widget/{token}/messages", async (string token, string session, int? after,
            AppDbContext db, ThreadStore store) =>
        {
            var account = await AccountAsync(db, token);
            if (account == null) return Results.NotFound(new { detail = "Unknown widget." });
            var thread = await db.Threads.FirstOrDefaultAsync(
                t => t.Channel == Channels.Website && t.Key == (session ?? "").Trim());
            if (thread == null) return Results.Ok(new { messages = Array.Empty<object>() });

            var msgs = store.GetMessages(thread, 50, null)
                .Where(m => !m.Private && (after is not int a || m.Id > a))
                .Select(WidgetMessage).ToList();
            return Results.Ok(new { messages = msgs });
        }).AllowAnonymous().RequireCors("widget");

        // ---- the embeddable script -------------------------------------------
        // Served from the API so a site owner adds ONE tag and nothing else, and
        // so the script and the endpoints it calls share an origin.
        app.MapGet("/widget.js", (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "public, max-age=300";
            return Results.Text(WidgetScript, "application/javascript");
        }).AllowAnonymous().RequireCors("widget");
    }

    // ---- helpers ------------------------------------------------------------

    /// <summary>The account a widget token belongs to (its ExternalId).</summary>
    private static Task<ChannelAccount?> AccountAsync(AppDbContext db, string token) =>
        db.ChannelAccounts.FirstOrDefaultAsync(
            a => a.Channel == Channels.Website && a.ExternalId == token);

    /// <summary>256 bits — a session id is the only thing protecting a transcript.</summary>
    private static string NewSessionId() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>A public widget token. Safe to embed; identifies the account only.</summary>
    public static string NewWidgetToken() =>
        "wgt_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();

    private static async Task<Contact?> UpsertContactAsync(AppDbContext db, string name, string email)
    {
        try
        {
            Contact? contact = null;
            if (email.Length > 0)
                contact = await db.Contacts.FirstOrDefaultAsync(c => c.Email.ToLower() == email.ToLower());
            if (contact == null)
            {
                contact = new Contact
                {
                    Name = name.Length > 0 ? name : (email.Length > 0 ? email : "Website visitor"),
                    Email = email,
                    Phone = "",
                    Status = "pending",
                    Method = "website",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                };
                db.Contacts.Add(contact);
                await db.SaveChangesAsync();
            }
            return contact;
        }
        catch
        {
            return null;   // a contact is a nicety; never block the conversation
        }
    }

    /// <summary>What a visitor may see of a message — no internal metadata.</summary>
    private static object WidgetMessage(Message m) => new
    {
        id = m.Id,
        direction = m.Direction,          // "in" = the visitor, "out" = us
        text = m.Text,
        created_at = m.CreatedAt,
    };

    private static string Cfg(ChannelAccount a, string key, string dflt)
    {
        var v = a.Config.TryGetValue(key, out var raw) ? raw?.ToString() ?? "" : "";
        return v.Length > 0 ? v : dflt;
    }

    private static string? Str(JsonElement b, string key) =>
        b.ValueKind == JsonValueKind.Object && b.TryGetProperty(key, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Null or JsonValueKind.Undefined => "",
                _ => v.ToString(),
            }
            : null;

    private static string Trim(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max]);

    // ---- the widget itself --------------------------------------------------

    /// <summary>
    /// A self-contained bubble + panel. Rendered into a SHADOW ROOT so the host
    /// page's CSS cannot leak in and ours cannot leak out — the usual reason an
    /// embedded widget looks broken on a stranger's site.
    /// </summary>
    private const string WidgetScript = """
(function () {
  var s = document.currentScript;
  var api = (s && s.getAttribute('data-api')) || new URL(s.src).origin + '/api';
  var token = s && s.getAttribute('data-token');
  if (!token) return console.error('[chatbranch] data-token is required');
  var KEY = 'chatbranch-widget-' + token;

  var host = document.createElement('div');
  host.style.cssText = 'position:fixed;right:20px;bottom:20px;z-index:2147483000';
  document.body.appendChild(host);
  var root = host.attachShadow({ mode: 'open' });

  var cfg = { title: 'Chat', greeting: '', accent: '#3B82F6', placeholder: 'Type your message…' };
  var session = localStorage.getItem(KEY) || '';
  var lastId = 0, open = false, poll = null;

  root.innerHTML =
    '<style>' +
    ':host,*{box-sizing:border-box;font-family:system-ui,-apple-system,"Segoe UI",Roboto,sans-serif}' +
    '.bubble{width:56px;height:56px;border-radius:50%;border:0;cursor:pointer;color:#fff;' +
      'box-shadow:0 6px 20px rgba(0,0,0,.25);font-size:24px;line-height:1}' +
    '.panel{display:none;flex-direction:column;width:340px;height:460px;background:#fff;color:#111;' +
      'border-radius:14px;overflow:hidden;box-shadow:0 12px 40px rgba(0,0,0,.28)}' +
    '.panel.open{display:flex}' +
    '.hd{padding:12px 14px;color:#fff;font-weight:700;font-size:14px;display:flex;' +
      'align-items:center;justify-content:space-between}' +
    '.hd button{background:transparent;border:0;color:#fff;font-size:18px;cursor:pointer}' +
    '.log{flex:1;overflow-y:auto;padding:12px;display:flex;flex-direction:column;gap:8px;background:#f7f7f8}' +
    '.msg{max-width:78%;padding:8px 11px;border-radius:12px;font-size:13px;line-height:1.45;' +
      'white-space:pre-wrap;word-wrap:break-word}' +
    '.in{align-self:flex-end;color:#fff}' +
    '.out{align-self:flex-start;background:#fff;border:1px solid #e6e6e8}' +
    '.ft{display:flex;gap:6px;padding:10px;border-top:1px solid #eee;background:#fff}' +
    '.ft input{flex:1;padding:9px 11px;border:1px solid #ddd;border-radius:9px;font-size:13px;outline:none}' +
    '.ft button{border:0;border-radius:9px;padding:0 14px;color:#fff;cursor:pointer;font-weight:600}' +
    '</style>' +
    '<div class="panel"><div class="hd"><span class="ttl"></span><button class="x" aria-label="Close">&times;</button></div>' +
    '<div class="log"></div>' +
    '<form class="ft"><input class="in-txt" autocomplete="off" /><button type="submit">Send</button></form></div>' +
    '<button class="bubble" aria-label="Open chat">&#128172;</button>';

  var panel = root.querySelector('.panel'), log = root.querySelector('.log');
  var bubble = root.querySelector('.bubble'), form = root.querySelector('.ft');
  var input = root.querySelector('.in-txt');

  function paint() {
    bubble.style.background = cfg.accent;
    root.querySelector('.hd').style.background = cfg.accent;
    root.querySelector('.ft button').style.background = cfg.accent;
    root.querySelector('.ttl').textContent = cfg.title;
    input.placeholder = cfg.placeholder;
  }

  // Render a message that came from the server (advances the poll cursor).
  function add(m) {
    if (m.id <= lastId) return;
    lastId = Math.max(lastId, m.id);
    bubbleEl(m.direction, m.text);
  }

  // Render something local (the greeting) WITHOUT touching the poll cursor —
  // a fake id here ends up in ?after= and the server rejects it as not an int.
  function addLocal(direction, text) { bubbleEl(direction, text); }

  function bubbleEl(direction, text) {
    var m = { direction: direction, text: text };
    var el = document.createElement('div');
    // "in" is the VISITOR's own message — right-aligned and accent-coloured.
    el.className = 'msg ' + (m.direction === 'in' ? 'in' : 'out');
    if (m.direction === 'in') el.style.background = cfg.accent;
    el.textContent = m.text;
    log.appendChild(el);
    log.scrollTop = log.scrollHeight;
  }

  function req(path, opts) {
    return fetch(api + '/widget/' + token + path, opts).then(function (r) { return r.json(); });
  }

  function start() {
    return req('/session', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ session: session })
    }).then(function (d) {
      if (!d || !d.session) return;
      session = d.session;
      localStorage.setItem(KEY, session);
      (d.messages || []).forEach(add);
      if ((!d.messages || !d.messages.length) && cfg.greeting) addLocal('out', cfg.greeting);
    });
  }

  function pump() {
    if (!session) return;
    req('/messages?session=' + encodeURIComponent(session) + '&after=' + lastId)
      .then(function (d) { (d.messages || []).forEach(add); })
      .catch(function () {});
  }

  bubble.onclick = function () {
    open = !open;
    panel.classList.toggle('open', open);
    if (open) {
      if (!session) start().then(pump); else pump();
      // Polling, not websockets: it survives every proxy and needs no config.
      poll = poll || setInterval(pump, 4000);
      input.focus();
    }
  };
  root.querySelector('.x').onclick = function () { open = false; panel.classList.remove('open'); };

  form.onsubmit = function (e) {
    e.preventDefault();
    var text = input.value.trim();
    if (!text || !session) return;
    input.value = '';
    req('/messages', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ session: session, text: text })
    }).then(function (d) { if (d && d.message) add(d.message); setTimeout(pump, 1200); })
      .catch(function () {});
  };

  req('/config').then(function (d) { if (d && d.title) cfg = Object.assign(cfg, d); paint(); }).catch(paint);
})();
""";
}
