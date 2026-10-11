using System.Security.Claims;
using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Api.Security;
using ChatBranch.Data;
using ChatBranch.Data.Audit;
using ChatBranch.Data.Channels;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// Settings → Organization → System email (<c>app_keys.manage</c> — credentials,
/// superadmin by default; organization-level, so nobody bound to entities):
/// <list type="bullet">
/// <item><c>GET /api/settings/system-email</c> — the stored settings (password masked), what
/// sign-in emails use right now (this mailbox, the default mail settings, or nothing), the
/// login address the links are built on and whether invites can go out.</item>
/// <item><c>PUT /api/settings/system-email</c> — save; a password containing "••" (the masked
/// value sent back) keeps the stored one; everything empty removes the configuration.</item>
/// <item><c>POST /api/settings/system-email/test</c> — a test email to the CALLER's own address
/// (Settings → My account), through what sign-in emails use right now; errors never carry a
/// secret.</item>
/// </list>
/// Not MCP tools (credentials — <c>McpExclusions</c>).
/// </summary>
public static class SystemEmailEndpoints
{
    public static void MapSystemEmailEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/settings/system-email", async (ClaimsPrincipal me, AppDbContext db, ScopingService sc,
            SecurityOptions security) => Results.Ok(await PayloadAsync(db, sc, me, security)))
            .RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("app_keys.manage").OrgWideOnly();

        app.MapPut("/api/settings/system-email", async (JsonElement body, ClaimsPrincipal me, AppDbContext db,
            ScopingService sc, SecurityOptions security) =>
        {
            if (body.ValueKind != JsonValueKind.Object) return Bad("Send a JSON object.", null);
            var current = SystemEmail.Load(sc);

            string Text(string key, string fallback) =>
                body.TryGetProperty(key, out var v) ? v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "" : fallback;

            var fromName = SystemEmail.OneLine(Text("from_name", current.FromName), 120);
            var fromEmail = Text("from_email", current.FromEmail).Trim();
            var host = Text("host", current.Host).Trim();
            var username = Text("username", current.Username).Trim();
            var password = Text("password", current.Password);
            if (password.Contains("••")) password = current.Password; // the masked value sent back = unchanged

            var port = current.Port;
            if (body.TryGetProperty("port", out var pv))
            {
                if (pv.ValueKind == JsonValueKind.Number && pv.TryGetInt32(out var n)) port = n;
                else if (pv.ValueKind == JsonValueKind.String && int.TryParse(pv.GetString(), out var ns)) port = ns;
                else if (pv.ValueKind is JsonValueKind.Null || (pv.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(pv.GetString())))
                    port = 587;
                else return Bad("port must be a number.", "port");
            }
            if (port is < 1 or > 65535) return Bad("port must be between 1 and 65535.", "port");
            var securityMode = Text("security", current.Security).Trim().ToLowerInvariant();
            if (securityMode.Length == 0) securityMode = SystemEmail.DefaultSecurity(port);
            if (!SystemEmail.SecurityModes.Contains(securityMode))
                return Bad($"security must be one of: {string.Join(", ", SystemEmail.SecurityModes)}.", "security");

            if (host.Length > 253 || host.Any(char.IsWhiteSpace) || host.Contains('/'))
                return Bad("That doesn't look like a server name (for example smtp.example.com).", "host");
            if (fromEmail.Length > 0 && !SystemEmail.IsEmail(fromEmail))
                return Bad("The sender address doesn't look like an email address.", "from_email");
            if (host.Length > 0 && fromEmail.Length == 0)
                return Bad("Enter the sender address the emails come from.", "from_email");
            if (fromEmail.Length > 0 && host.Length == 0)
                return Bad("Enter the SMTP server.", "host");
            if (username.Length > 254 || password.Length > 1024)
                return Bad("The login is too long.", "username");

            var next = new SystemEmail.Config
            {
                FromName = fromName, FromEmail = fromEmail, Host = host, Port = port, Security = securityMode,
                Username = username, Password = password,
            };
            var changed = new List<string>();
            if (next.FromName != current.FromName) changed.Add("from_name");
            if (next.FromEmail != current.FromEmail) changed.Add("from_email");
            if (next.Host != current.Host) changed.Add("host");
            if (next.Port != current.Port) changed.Add("port");
            if (next.Security != current.Security) changed.Add("security");
            if (next.Username != current.Username) changed.Add("username");
            if (next.Password != current.Password) changed.Add("password");
            if (changed.Count > 0)
            {
                SystemEmail.Save(sc, next);
                // which fields changed — never their values
                AuditTrail.Write(db, "settings.update", "system-email",
                    next.Empty ? "system email removed" : $"fields: {string.Join(", ", changed)}");
            }
            return Results.Ok(await PayloadAsync(db, sc, me, security));
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("app_keys.manage").OrgWideOnly();

        app.MapPost("/api/settings/system-email/test", async (ClaimsPrincipal me, AppDbContext db, ScopingService sc,
            IEmailTransport transport, HttpContext ctx) =>
        {
            var self = await OwnUserAsync(db, me);
            var to = (self?.Email ?? "").Trim();
            if (self == null || to.Length == 0)
                return Results.Json(new
                {
                    detail = "Add your own email address in Settings → My account first — the test is sent to you.",
                    code = "no_own_email",
                }, statusCode: 400);
            var (server, source) = SystemEmail.Resolve(sc);
            if (server == null)
                return Results.Json(new { detail = SystemEmail.Explain("email_not_configured"), code = "email_not_configured" },
                    statusCode: 409);
            var message = WelcomeEmail.Test(InviteMailer.LocaleOf(sc, self.Profile?.Preferences),
                SystemEmail.OneLine(MyAccountEndpoints.DisplayName(self), 120), source);
            var result = await SystemEmail.SendAsync(transport, server, to, MyAccountEndpoints.DisplayName(self),
                message.Subject, message.Text, ct: ctx.RequestAborted);
            var detail = MailKitEmailTransport.Scrub(result.Detail, server);
            AuditTrail.Write(db, "settings.system_email_test", "system-email",
                result.Ok ? $"test email sent to {SystemEmail.MaskEmail(to)} via the {SourceLabel(source)}"
                          : $"test email to {SystemEmail.MaskEmail(to)} failed: {detail}");
            if (!result.Ok)
                return Results.Json(new
                {
                    detail = detail.Length > 0 ? detail : "The mail server refused the test email.",
                    code = "send_failed", source,
                }, statusCode: 502);
            return Results.Ok(new { ok = true, to, source });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("app_keys.manage").OrgWideOnly();
    }

    private static string SourceLabel(string source) =>
        source == SystemEmail.SourceSystem ? "system email" : "default mail settings";

    private static async Task<object> PayloadAsync(AppDbContext db, ScopingService sc, ClaimsPrincipal me,
        SecurityOptions security)
    {
        var c = SystemEmail.Load(sc);
        EmailServerSettings? fallback = null;
        try { fallback = EmailConfig.GlobalSettings(sc); } catch { /* reported as not configured */ }
        var readiness = InviteMailer.Check(sc, security);
        var self = await OwnUserAsync(db, me);
        return new
        {
            from_name = c.FromName,
            from_email = c.FromEmail,
            host = c.Host,
            port = c.Port,
            security = c.Security,
            username = c.Username,
            password_masked = SettingsEndpoints.MaskSecret(c.Password),
            has_password = c.Password.Length > 0,
            configured = c.Configured,
            security_modes = SystemEmail.SecurityModes,
            // what sign-in emails use right now: this mailbox, else the default mail settings, else nothing
            source = readiness.Source,
            default_mail = new { configured = fallback?.SmtpConfigured == true, from = fallback?.From ?? "" },
            invites = readiness.Payload(Math.Max(1, security.InviteLinkHours)),
            // the test goes to the caller's own address
            my_email = (self?.Email ?? "").Trim(),
        };
    }

    private static async Task<AppUser?> OwnUserAsync(AppDbContext db, ClaimsPrincipal me)
    {
        if (!int.TryParse(me.FindFirstValue("profile_id"), out var pid))
        {
            var uid = await Caller.UserIdAsync(db, me);
            return uid is int id ? await db.Users.Include(u => u.Profile).FirstOrDefaultAsync(u => u.Id == id) : null;
        }
        return await db.Users.Include(u => u.Profile).FirstOrDefaultAsync(u => u.Profile != null && u.Profile.Id == pid);
    }

    private static IResult Bad(string detail, string? field) =>
        Results.Json(new { detail, field }, statusCode: 400);
}
