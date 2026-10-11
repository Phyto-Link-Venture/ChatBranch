using System.Globalization;
using ChatBranch.Domain;

namespace ChatBranch.Api.Security;

/// <summary>
/// The words of the sign-in emails, in English, Bahasa Melayu and 简体中文 (the
/// dashboard's languages). Every value is plain text: the HTML part is rendered from
/// it by <c>EmailHtml</c>, which escapes everything, and names are reduced to one line
/// first so nobody's name can fake an extra line. No email ever contains a password.
/// </summary>
public static class WelcomeEmail
{
    public sealed record Message(string Subject, string Text, string ButtonLabel, string ButtonUrl);

    /// <summary>What goes into one welcome / reset email.</summary>
    public sealed record Facts(
        string Locale, string Purpose, string Name, string Username, string Inviter, string Organization,
        string LoginUrl, string LinkUrl, DateTime ExpiresAtUtc, int Hours, TimeZoneInfo Zone);

    private sealed record Words(
        string InviteSubject, string ResetSubject, string Hello,
        string InviteIntro, string ResetIntro, string UsernameLine, string LoginLine,
        string InviteAsk, string ResetAsk, string InviteExpiry, string ResetExpiry,
        string InviteIgnore, string ResetIgnore, string InviteButton, string ResetButton,
        string TestSubject, string TestBody, string Signature, string YourOrganization);

    private static readonly Dictionary<string, Words> Dictionary = new()
    {
        ["en"] = new Words(
            InviteSubject: "{inviter} added you to {org} on ChatBranch",
            ResetSubject: "Choose a new password for ChatBranch",
            Hello: "Hello {name},",
            InviteIntro: "{inviter} added you to {org} on ChatBranch.",
            ResetIntro: "{inviter} sent you a link to choose a new password for your ChatBranch account at {org}.",
            UsernameLine: "Your username: {username}",
            LoginLine: "Sign in at: {login}",
            InviteAsk: "To get started, choose your own password:",
            ResetAsk: "Choose your new password here:",
            InviteExpiry: "This link works once and expires on {expires} ({hours} hours from now). After that, ask {inviter} to send you a new one.",
            ResetExpiry: "This link works once and expires on {expires}. Your current password keeps working until you choose a new one.",
            InviteIgnore: "If you didn't expect this email, you can ignore it — nobody can use your account without choosing a password through this link.",
            ResetIgnore: "If you didn't ask for this, you can ignore this email — your password stays as it is.",
            InviteButton: "Choose my password",
            ResetButton: "Choose a new password",
            TestSubject: "ChatBranch test email",
            TestBody: "This is a test from Settings → System email. Welcome emails and password links will be sent from this address.",
            Signature: "— ChatBranch",
            YourOrganization: "your organization"),
        ["ms"] = new Words(
            InviteSubject: "{inviter} telah menambah anda ke {org} di ChatBranch",
            ResetSubject: "Pilih kata laluan baharu untuk ChatBranch",
            Hello: "Hai {name},",
            InviteIntro: "{inviter} telah menambah anda ke {org} di ChatBranch.",
            ResetIntro: "{inviter} telah menghantar pautan untuk anda memilih kata laluan baharu bagi akaun ChatBranch anda di {org}.",
            UsernameLine: "Nama pengguna anda: {username}",
            LoginLine: "Log masuk di: {login}",
            InviteAsk: "Untuk bermula, pilih kata laluan anda sendiri:",
            ResetAsk: "Pilih kata laluan baharu anda di sini:",
            InviteExpiry: "Pautan ini hanya boleh digunakan sekali dan tamat tempoh pada {expires} ({hours} jam dari sekarang). Selepas itu, minta {inviter} menghantar pautan baharu.",
            ResetExpiry: "Pautan ini hanya boleh digunakan sekali dan tamat tempoh pada {expires}. Kata laluan semasa anda masih berfungsi sehingga anda memilih yang baharu.",
            InviteIgnore: "Jika anda tidak menjangkakan e-mel ini, abaikan sahaja — tiada sesiapa boleh menggunakan akaun anda tanpa memilih kata laluan melalui pautan ini.",
            ResetIgnore: "Jika anda tidak meminta ini, abaikan e-mel ini — kata laluan anda kekal seperti sedia ada.",
            InviteButton: "Pilih kata laluan saya",
            ResetButton: "Pilih kata laluan baharu",
            TestSubject: "E-mel ujian ChatBranch",
            TestBody: "Ini ujian daripada Tetapan → E-mel sistem. E-mel alu-aluan dan pautan kata laluan akan dihantar dari alamat ini.",
            Signature: "— ChatBranch",
            YourOrganization: "organisasi anda"),
        ["zh"] = new Words(
            InviteSubject: "{inviter} 已将您添加到 ChatBranch 上的 {org}",
            ResetSubject: "为 ChatBranch 设置新密码",
            Hello: "{name}，您好：",
            InviteIntro: "{inviter} 已将您添加到 ChatBranch 上的 {org}。",
            ResetIntro: "{inviter} 向您发送了一个链接，用于为您在 {org} 的 ChatBranch 账户设置新密码。",
            UsernameLine: "您的用户名：{username}",
            LoginLine: "登录地址：{login}",
            InviteAsk: "请先设置您自己的密码：",
            ResetAsk: "请在此设置新密码：",
            InviteExpiry: "此链接仅可使用一次，将于 {expires}（{hours} 小时后）失效。失效后，请联系 {inviter} 重新发送。",
            ResetExpiry: "此链接仅可使用一次，将于 {expires} 失效。在您设置新密码之前，当前密码仍然有效。",
            InviteIgnore: "如果您没有预料到这封邮件，请忽略它——不通过此链接设置密码，任何人都无法使用您的账户。",
            ResetIgnore: "如果这不是您本人的请求，请忽略此邮件——您的密码不会改变。",
            InviteButton: "设置我的密码",
            ResetButton: "设置新密码",
            TestSubject: "ChatBranch 测试邮件",
            TestBody: "这是来自“设置 → 系统邮件”的测试。欢迎邮件和密码链接将从此地址发送。",
            Signature: "— ChatBranch",
            YourOrganization: "您的组织"),
    };

    /// <summary>"en" | "ms" | "zh" — anything else is English.</summary>
    public static string Normalize(string? locale) =>
        (locale ?? "").Trim().ToLowerInvariant() is "ms" or "zh" ? locale!.Trim().ToLowerInvariant() : "en";

    public static Message Build(Facts f)
    {
        var w = Dictionary[Normalize(f.Locale)];
        var reset = f.Purpose == PasswordTokenPurposes.Reset;
        var values = new Dictionary<string, string>
        {
            ["inviter"] = f.Inviter, ["org"] = f.Organization.Length > 0 ? f.Organization : w.YourOrganization, ["name"] = f.Name, ["username"] = f.Username,
            ["login"] = f.LoginUrl, ["expires"] = FormatExpiry(f.ExpiresAtUtc, f.Zone, f.Locale),
            ["hours"] = f.Hours.ToString(CultureInfo.InvariantCulture),
        };
        // one pass: a value that happens to contain "{org}" is never filled in again
        string Fill(string template) => Placeholder.Replace(template,
            m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
        var lines = new List<string>
        {
            Fill(w.Hello),
            "",
            Fill(reset ? w.ResetIntro : w.InviteIntro),
            "",
            Fill(w.UsernameLine),
            Fill(w.LoginLine),
            "",
            Fill(reset ? w.ResetAsk : w.InviteAsk),
            "",
            f.LinkUrl,
            "",
            Fill(reset ? w.ResetExpiry : w.InviteExpiry),
            "",
            Fill(reset ? w.ResetIgnore : w.InviteIgnore),
            "",
            w.Signature,
        };
        return new Message(
            SystemEmail.OneLine(Fill(reset ? w.ResetSubject : w.InviteSubject), 200),
            string.Join("\n", lines) + "\n",
            reset ? w.ResetButton : w.InviteButton,
            f.LinkUrl);
    }

    /// <summary>The settings page's test email.</summary>
    public static Message Test(string locale, string name, string source)
    {
        var w = Dictionary[Normalize(locale)];
        var text = string.Join("\n", new[]
        {
            Placeholder.Replace(w.Hello, _ => name), "", w.TestBody, "", w.Signature,
        }) + "\n";
        return new Message(w.TestSubject, text, "", "");
    }

    /// <summary>The expiry in the organization's time zone, written the way the reader's language writes dates.</summary>
    public static string FormatExpiry(DateTime utc, TimeZoneInfo zone, string? locale)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
        var offset = zone.GetUtcOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var utcLabel = offset == TimeSpan.Zero ? "UTC"
            : $"UTC{sign}{Math.Abs(offset.Hours):00}:{Math.Abs(offset.Minutes):00}";
        var time = local.ToString("HH':'mm", CultureInfo.InvariantCulture);
        return Normalize(locale) switch
        {
            "zh" => $"{local.Year}年{local.Month}月{local.Day}日 {time}（{utcLabel}）",
            "ms" => $"{local.Day} {MalayMonths[local.Month - 1]} {local.Year}, {time} ({utcLabel})",
            _ => $"{local.Day} {local.ToString("MMMM", CultureInfo.InvariantCulture)} {local.Year}, {time} ({utcLabel})",
        };
    }

    private static readonly System.Text.RegularExpressions.Regex Placeholder = new(@"\{(\w+)\}");

    private static readonly string[] MalayMonths =
    {
        "Januari", "Februari", "Mac", "April", "Mei", "Jun", "Julai", "Ogos", "September", "Oktober", "November", "Disember",
    };
}
