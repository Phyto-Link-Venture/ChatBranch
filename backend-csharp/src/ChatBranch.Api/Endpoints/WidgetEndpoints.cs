using System.Security.Cryptography;
using System.Text;
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
        // Title, greeting and placeholder are the operator's own words, sent
        // only when set: blank ones are filled in by the script from its own
        // dictionary, in the visitor's language (see `language`).
        app.MapGet("/api/widget/{token}/config", async (string token, AppDbContext db) =>
        {
            var account = await AccountAsync(db, token);
            if (account == null) return Results.NotFound(new { detail = "Unknown widget." });
            return Results.Ok(new
            {
                title = Cfg(account, "title", ""),
                greeting = Cfg(account, "greeting", ""),
                accent = Cfg(account, "accent", "#3B82F6"),
                placeholder = Cfg(account, "placeholder", ""),
                // auto = the visitor's browser language; else en | ms | zh
                language = WidgetAdminEndpoints.WidgetLanguage(account),
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
            // …and only on THIS widget (a session never crosses to another widget / company)
            Thread? thread = session.Length > 0
                ? await db.Threads.FirstOrDefaultAsync(t => t.Channel == Channels.Website && t.Key == session
                                                            && t.AccountId == account.Id)
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
                    thread.ContactId = (await UpsertContactAsync(db, sc, account, name, email))?.Id;
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
                t => t.Channel == Channels.Website && t.Key == session && t.AccountId == account.Id);
            if (thread == null)
                return Results.Json(new { detail = "This conversation has expired — reload the page." },
                                    statusCode: 404);

            var msg = store.AppendMessage(thread, MessageDirection.In, MessageSource.Customer,
                text, status: "received", accountId: account.Id);

            // Same post-inbound path as every other channel: flows resume, the
            // conversation routes to a team, automations run, the AI may reply.
            try { engine.OnInboundMessage(thread, text); } catch { }
            await TeamsEndpoints.AutoAssignAsync(db, thread);
            var firstInbound = db.Messages.Count(m => m.ThreadId == thread.Id
                                                      && m.Direction == MessageDirection.In) <= 1;
            if (firstInbound) automation.Run("conversation_created", thread, text, true);
            automation.Run("message_created", thread, text, firstInbound);
            await NotificationService.NotifyInboundMessageAsync(db, thread, text);
            inbound.MaybeAutoReply(thread, text);

            return Results.Ok(new { message = WidgetMessage(msg) });
        }).AllowAnonymous().RequireCors("widget");

        // ---- visitor polls for replies ---------------------------------------
        app.MapGet("/api/widget/{token}/messages", async (string token, string session, int? after,
            AppDbContext db, ThreadStore store) =>
        {
            var account = await AccountAsync(db, token);
            if (account == null) return Results.NotFound(new { detail = "Unknown widget." });
            var thread = await db.Threads.FirstOrDefaultAsync(
                t => t.Channel == Channels.Website && t.Key == (session ?? "").Trim() && t.AccountId == account.Id);
            if (thread == null) return Results.Ok(new { messages = Array.Empty<object>() });

            var msgs = store.GetMessages(thread, 50, null)
                .Where(m => !m.Private && (after is not int a || m.Id > a))
                .Select(WidgetMessage).ToList();
            return Results.Ok(new { messages = msgs });
        }).AllowAnonymous().RequireCors("widget");

        // ---- the embeddable script -------------------------------------------
        // Served from the API so a site owner adds ONE tag and nothing else, and
        // so the script and the endpoints it calls share an origin.
        // Also under /api/: a single-origin deployment proxies only /api/ to the
        // API, so the embed hands out /api/widget.js; /widget.js stays for the
        // snippets already pasted into sites (and API-origin deployments).
        foreach (var path in new[] { "/api/widget.js", "/widget.js" })
            app.MapGet(path, (HttpContext ctx) =>
            {
                ctx.Response.Headers.CacheControl = "public, max-age=300";
                // UTF-8 declared: the dictionary carries Malay and Chinese, and a
                // host page in another encoding must not garble them.
                return Results.Text(WidgetScript, "application/javascript", Encoding.UTF8);
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

    private static async Task<Contact?> UpsertContactAsync(AppDbContext db, ScopingService sc, ChannelAccount account,
        string name, string email)
    {
        try
        {
            // the widget's OWN company's contact with that email (else an organization-wide one) —
            // a visitor typing an address never reaches another company's contact
            Contact? contact = null;
            if (email.Length > 0)
                contact = ChatBranch.Data.Contacts.ContactCompanies.MatchEmail(db, sc, email, account.EntityId);
            if (contact != null && !ChatBranch.Data.Contacts.ContactCompanies.MayLink(db, sc, contact.Id,
                    sc.CompanyOf(account.EntityId)))
                contact = null;
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
                // filed under the widget's entity, so it belongs to that company
                ChatBranch.Data.Contacts.ContactCompanies.LinkToEntity(db, contact.Id, account.EntityId);
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
        // stored UTC; a row read back from SQLite has Kind=Unspecified and would
        // serialize without the Z (the widget then read it as local time)
        created_at = DateTime.SpecifyKind(m.CreatedAt, DateTimeKind.Utc),
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
    ///
    /// The widget speaks the visitor's language: its own words (launcher,
    /// close, send, the default title / greeting / placeholder, the error
    /// lines) come from the <c>I18N</c> dictionary below — English, Bahasa
    /// Melayu and 简体中文 — picked by the widget's configured <c>language</c>,
    /// else the visitor's <c>navigator.languages</c> (Indonesian reads Malay),
    /// else English. Keep the three in step with the dashboard's locales.
    /// </summary>
    private const string WidgetScript = """
(function () {
  var s = document.currentScript;
  var api = (s && s.getAttribute('data-api')) || new URL(s.src).origin + '/api';
  var token = s && s.getAttribute('data-token');
  if (!token) return console.error('[chatbranch] data-token is required');
  var KEY = 'chatbranch-widget-' + token;

  // The widget's own words. Picked by the widget's configured language, else
  // the visitor's browser, else English; the operator's own title, greeting
  // and placeholder (when set) are shown as written.
  var I18N = {
    en: {
      open: 'Open chat', close: 'Close', title: 'Chat with us', greeting: 'Hi! How can we help?',
      placeholder: 'Type your message…', send: 'Send', message: 'Message',
      sendFailed: "Couldn't send your message — check your connection and try again.",
      expired: 'This conversation has ended. Send your message again to start a new one.',
      offline: "We can't reach the chat right now. Please try again in a moment."
    },
    ms: {
      open: 'Buka sembang', close: 'Tutup', title: 'Sembang dengan kami', greeting: 'Hai! Apa yang boleh kami bantu?',
      placeholder: 'Taip mesej anda…', send: 'Hantar', message: 'Mesej',
      sendFailed: 'Mesej anda tidak dapat dihantar — semak sambungan anda dan cuba lagi.',
      expired: 'Perbualan ini telah tamat. Hantar mesej anda sekali lagi untuk memulakan perbualan baharu.',
      offline: 'Sembang tidak dapat dihubungi buat masa ini. Sila cuba sebentar lagi.'
    },
    zh: {
      open: '打开聊天', close: '关闭', title: '与我们聊天', greeting: '您好！有什么可以帮您？',
      placeholder: '输入消息…', send: '发送', message: '消息',
      sendFailed: '消息发送失败——请检查网络连接后重试。',
      expired: '此对话已结束。请重新发送消息以开始新的对话。',
      offline: '暂时无法连接聊天，请稍后再试。'
    }
  };

  function pickLang(pref) {
    if (pref && I18N[pref]) return pref;
    var tags = (navigator.languages && navigator.languages.length) ? navigator.languages : [navigator.language || ''];
    for (var i = 0; i < tags.length; i++) {
      var base = String(tags[i] || '').toLowerCase().split('-')[0];
      if (base === 'id') return 'ms'; // Indonesian readers get Malay rather than English
      if (I18N[base]) return base;
    }
    return 'en';
  }

  function load() { try { return localStorage.getItem(KEY) || ''; } catch (e) { return ''; } }
  function save(v) { try { if (v) localStorage.setItem(KEY, v); else localStorage.removeItem(KEY); } catch (e) {} }

  var host = document.createElement('div');
  host.style.cssText = 'position:fixed;right:20px;bottom:20px;z-index:2147483000;display:flex;flex-direction:column;align-items:flex-end;gap:12px';
  document.body.appendChild(host);
  var root = host.attachShadow({ mode: 'open' });

  var cfg = { title: '', greeting: '', accent: '#3B82F6', placeholder: '', language: 'auto' };
  var lang = pickLang(), L = I18N[lang];
  var session = load();
  var lastId = 0, open = false, poll = null;

  root.innerHTML =
    '<style>' +
    ':host,*{box-sizing:border-box;font-family:system-ui,-apple-system,"Segoe UI",Roboto,"PingFang SC","Microsoft YaHei",sans-serif}' +
    '.bubble{width:56px;height:56px;border-radius:50%;border:0;cursor:pointer;color:#fff;' +
      'box-shadow:0 6px 20px rgba(0,0,0,.25);font-size:24px;line-height:1}' +
    '.panel{display:none;flex-direction:column;width:340px;max-width:calc(100vw - 40px);height:460px;' +
      'max-height:calc(100vh - 100px);background:#fff;color:#111;' +
      'border-radius:14px;overflow:hidden;box-shadow:0 12px 40px rgba(0,0,0,.28)}' +
    '.panel.open{display:flex}' +
    '.hd{padding:12px 14px;color:#fff;font-weight:700;font-size:14px;display:flex;' +
      'align-items:center;justify-content:space-between;gap:8px}' +
    '.hd .ttl{min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}' +
    '.hd button{background:transparent;border:0;color:#fff;font-size:18px;cursor:pointer}' +
    '.log{flex:1;overflow-y:auto;padding:12px;display:flex;flex-direction:column;gap:8px;background:#f7f7f8}' +
    '.msg{max-width:78%;padding:8px 11px;border-radius:12px;font-size:13px;line-height:1.45;' +
      'white-space:pre-wrap;word-wrap:break-word}' +
    '.in{align-self:flex-end;color:#fff}' +
    '.out{align-self:flex-start;background:#fff;border:1px solid #e6e6e8}' +
    '.err{display:none;padding:7px 12px;font-size:12px;line-height:1.4;color:#9f1d14;background:#fdf0ee;' +
      'border-top:1px solid #f6d5d0}' +
    '.err.on{display:block}' +
    '.ft{display:flex;gap:6px;padding:10px;border-top:1px solid #eee;background:#fff}' +
    '.ft input{flex:1;min-width:0;padding:9px 11px;border:1px solid #ddd;border-radius:9px;font-size:13px;outline:none}' +
    '.ft button{border:0;border-radius:9px;padding:0 14px;color:#fff;cursor:pointer;font-weight:600;white-space:nowrap}' +
    '</style>' +
    '<div class="panel" role="dialog"><div class="hd"><span class="ttl"></span><button class="x" type="button">&times;</button></div>' +
    '<div class="log" aria-live="polite"></div>' +
    '<div class="err" role="status"></div>' +
    '<form class="ft"><input class="in-txt" autocomplete="off" /><button type="submit"></button></form></div>' +
    '<button class="bubble" type="button">&#128172;</button>';

  var panel = root.querySelector('.panel'), log = root.querySelector('.log');
  var bubble = root.querySelector('.bubble'), form = root.querySelector('.ft');
  var input = root.querySelector('.in-txt'), sendBtn = root.querySelector('.ft button');
  var closeBtn = root.querySelector('.x'), errEl = root.querySelector('.err');

  // Paint the look and every built-in word in the chosen language.
  function paint() {
    lang = pickLang(cfg.language);
    L = I18N[lang];
    panel.setAttribute('lang', lang);
    bubble.style.background = cfg.accent;
    bubble.setAttribute('aria-label', L.open);
    bubble.title = L.open;
    root.querySelector('.hd').style.background = cfg.accent;
    sendBtn.style.background = cfg.accent;
    sendBtn.textContent = L.send;
    closeBtn.setAttribute('aria-label', L.close);
    closeBtn.title = L.close;
    root.querySelector('.ttl').textContent = cfg.title || L.title;
    panel.setAttribute('aria-label', cfg.title || L.title);
    input.placeholder = cfg.placeholder || L.placeholder;
    input.setAttribute('aria-label', L.message);
  }

  function showErr(text) {
    errEl.textContent = text || '';
    errEl.classList.toggle('on', !!text);
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

  // JSON in, JSON out; a non-2xx answer rejects with its status.
  function req(path, opts) {
    return fetch(api + '/widget/' + token + path, opts).then(function (r) {
      return r.json().catch(function () { return {}; }).then(function (d) {
        if (!r.ok) { var e = new Error('HTTP ' + r.status); e.status = r.status; throw e; }
        return d;
      });
    });
  }

  function start() {
    return req('/session', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ session: session })
    }).then(function (d) {
      if (!d || !d.session) { showErr(L.offline); return; }
      session = d.session;
      save(session);
      showErr('');
      (d.messages || []).forEach(add);
      if (!d.messages || !d.messages.length) addLocal('out', cfg.greeting || L.greeting);
    }).catch(function () { showErr(L.offline); });
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
  closeBtn.onclick = function () { open = false; panel.classList.remove('open'); };

  form.onsubmit = function (e) {
    e.preventDefault();
    var text = input.value.trim();
    if (!text) return;
    input.value = '';
    showErr('');
    (session ? Promise.resolve() : start()).then(function () {
      if (!session) { var x = new Error('no session'); x.status = 0; throw x; }
      return req('/messages', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ session: session, text: text })
      });
    }).then(function (d) {
      if (d && d.message) add(d.message);
      setTimeout(pump, 1200);
    }).catch(function (err) {
      input.value = text; // nothing typed is lost
      if (err && err.status === 404) {
        // the conversation is gone server-side: the next send starts a new one
        session = ''; save(''); lastId = 0; log.innerHTML = '';
        showErr(L.expired);
      } else {
        showErr(L.sendFailed);
      }
    });
  };

  paint();
  req('/config').then(function (d) { if (d) cfg = Object.assign(cfg, d); paint(); }).catch(paint);
})();
""";
}
