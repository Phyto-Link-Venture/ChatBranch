using System.Net.Http.Json;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChatBranch.Tests;

/// <summary>
/// "Every inbound message shows up in Chats": a message from a sender who is NOT a
/// contact opens (or continues) an UNLINKED conversation (ContactId null) that the
/// inbox lists under <c>linked=0</c> ("Not in contacts") — and the AI, which only
/// answers linked conversations, stays quiet. One check, shared by every
/// channel's own test class (each brings its own fixture and webhook helpers).
/// <paramref name="aiStaysQuiet"/> is false only for the website widget, whose
/// anonymous visitors the AI has always been allowed to answer (AiGate's one
/// exception to the linked-contact rule — unchanged).
/// </summary>
internal static class UnknownSenderAsserts
{
    public static async Task AssertListedAsNotInContactsAsync(ApiFactory factory, HttpClient admin,
        string channel, string key, string? text = null, bool aiStaysQuiet = true)
    {
        // stored: an unlinked thread holding the customer's message, nothing from the AI
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var thread = await db.Threads.AsNoTracking().SingleOrDefaultAsync(t => t.Channel == channel && t.Key == key);
            Assert.True(thread != null, $"no {channel} conversation for {key}");
            Assert.Null(thread!.ContactId);
            var msgs = await db.Messages.AsNoTracking().Where(m => m.ThreadId == thread.Id).ToListAsync();
            Assert.Contains(msgs, m => m.Direction == MessageDirection.In
                                       && (text == null || m.Text.Contains(text)));
            if (aiStaysQuiet) Assert.DoesNotContain(msgs, m => m.Source == MessageSource.Ai);
        }

        // listed: under "Not in contacts" (linked=0), never under "Contacts" (linked=1)
        var q = Uri.EscapeDataString(key);
        var unlinked = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/inbox/threads?linked=0&channel={channel}&q={q}&limit=200");
        var row = Assert.Single(unlinked.GetProperty("threads").EnumerateArray(),
            r => r.GetProperty("key").GetString() == key);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("contact_id").ValueKind);
        Assert.True(row.GetProperty("message_count").GetInt32() >= 1);
        var linked = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/inbox/threads?linked=1&channel={channel}&q={q}&limit=200");
        Assert.DoesNotContain(linked.GetProperty("threads").EnumerateArray(),
            r => r.GetProperty("key").GetString() == key);
    }
}
