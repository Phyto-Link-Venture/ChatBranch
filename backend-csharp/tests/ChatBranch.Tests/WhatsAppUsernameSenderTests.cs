using System.Net;
using System.Text.Json;
using ChatBranch.Data.Channels;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// WhatsApp usernames: a user who adopted one may arrive with NO phone number —
/// <c>contacts[].wa_id</c> and <c>messages[].from</c> omitted, identified only by the
/// business-scoped user id (<c>contacts[].user_id</c> / <c>messages[].from_user_id</c>).
/// ChatBranch keys WhatsApp conversations on the phone number, so such a message used to
/// vanish without a trace (the parser returned nothing). It is now recorded where the
/// receiving company can see it — never as a conversation with an empty key, never
/// keyed on the BSUID (see <see cref="WhatsAppUsernameSenders"/> for why).
/// </summary>
public class WhatsAppUsernameSenderTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public WhatsAppUsernameSenderTests(ApiFactory f) => _f = f;

    private static string Payload(string pnid, string bsuid, string username, string wamid, string text,
        string? from = null)
    {
        var contact = new Dictionary<string, object?>
        {
            ["profile"] = new Dictionary<string, object?> { ["name"] = "Uname User", ["username"] = username },
            ["user_id"] = bsuid,
        };
        var message = new Dictionary<string, object?>
        {
            ["from_user_id"] = bsuid, ["id"] = wamid,
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
            ["type"] = "text", ["text"] = new { body = text },
        };
        if (from != null)
        {
            contact["wa_id"] = from;
            message["from"] = from;
        }
        var value = new
        {
            messaging_product = "whatsapp",
            metadata = new { phone_number_id = pnid },
            contacts = new[] { contact },
            messages = new[] { message },
        };
        return JsonSerializer.Serialize(new { entry = new[] { new { changes = new[] { new { field = "messages", value } } } } });
    }

    [Fact]
    public void The_parser_reads_the_bsuid_and_username_of_a_phoneless_sender()
    {
        var root = JsonSerializer.Deserialize<JsonElement>(Payload("PN-X", "BSUID-1", "shop.fan", "wamid.X1", "hi"));
        Assert.Empty(MetaWebhook.CloudMessages(root));          // nothing to key a conversation on
        var one = Assert.Single(MetaWebhook.CloudUnkeyedMessages(root));
        Assert.Equal("PN-X", one.PhoneNumberId);
        Assert.Equal("BSUID-1", one.UserId);
        Assert.Equal("shop.fan", one.Username);
        Assert.Equal("wamid.X1", one.Wamid);
        Assert.Equal("text", one.Type);
    }

    [Fact]
    public async Task A_message_with_no_phone_number_is_recorded_for_its_company_not_lost()
    {
        var pnid = $"PN-UNAME-{Guid.NewGuid():N}"[..20];
        var (company, account) = CloudWebhookKit.Company(_f, "Uname Co", pnid);
        var wamid = $"wamid.UNAME.{Guid.NewGuid():N}";
        var res = await _f.CreateClient().PostAsync("/api/whatsapp/webhook",
            CloudWebhookKit.Signed(Payload(pnid, "BSUID-REC-1", "shop.fan", wamid, "SECRET-WORDS where is my order")));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        CloudWebhookKit.WithDb(_f, db =>
        {
            Assert.False(db.Threads.Any(t => t.Channel == "whatsapp" && t.Key.Trim() == ""));
            Assert.False(db.Threads.Any(t => t.Channel == "whatsapp" && t.Key.Contains("BSUID-REC-1")));
            Assert.False(db.Messages.Any(m => m.Text.Contains("SECRET-WORDS")));
            var audit = Assert.Single(db.AuditLogs.AsNoTracking()
                .Where(a => a.Action == WhatsAppUsernameSenders.Audit && a.Detail.Contains(wamid)).ToList());
            // visible to the receiving number's company only, without the customer's words
            Assert.Equal(company, audit.EntityId);
            Assert.Contains($"#{account}", audit.Target);
            Assert.DoesNotContain("SECRET-WORDS", audit.Detail);
            Assert.Contains("shop.fan", audit.Detail);
            return 0;
        });
    }

    [Fact]
    public async Task A_username_holder_whose_number_is_present_still_gets_a_normal_conversation()
    {
        var pnid = $"PN-UNAME-{Guid.NewGuid():N}"[..20];
        CloudWebhookKit.Company(_f, "Uname Co 2", pnid);
        var from = "60199" + Random.Shared.Next(1000000, 9999999);
        var res = await _f.CreateClient().PostAsync("/api/whatsapp/webhook",
            CloudWebhookKit.Signed(Payload(pnid, "BSUID-REC-2", "shop.fan2", $"wamid.{Guid.NewGuid():N}", "hello", from)));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var t = Assert.Single(CloudWebhookKit.Threads(_f, from));
        Assert.Equal("hello", Assert.Single(CloudWebhookKit.Messages(_f, t.Id)).Text);
    }
}
