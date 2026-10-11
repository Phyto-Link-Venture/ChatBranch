using ChatBranch.Data;
using ChatBranch.Data.Ai;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>A customer's photo reaches the chat reply: attached to their latest
/// message for a model that reads images, or described honestly for one that doesn't.</summary>
public class ChatImageTests
{
    private sealed class SeeingLlm : ILlmClient
    {
        private readonly bool _vision;
        public SeeingLlm(bool vision) => _vision = vision;
        public IReadOnlyList<LlmMessage>? First;
        public bool IsConfigured => true;
        public bool SupportsVision => _vision;
        public Task<LlmResult> CompleteAsync(string system, IReadOnlyList<(string role, string content)> messages,
            CancellationToken ct = default) => Task.FromResult(new LlmResult("ok", 0, 0, "test", "v"));
        public Task<LlmChatResult> ChatAsync(string system, IReadOnlyList<LlmMessage> messages,
            IReadOnlyList<LlmTool>? tools = null, string? model = null, double temperature = 0.5, CancellationToken ct = default)
        {
            First ??= messages.ToList();
            return Task.FromResult(ToolScriptLlm.Says("That looks like a cracked screen — we can fix it."));
        }
    }

    private static readonly byte[] Jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 2, 3 };

    private static IReadOnlyList<LlmMessage> Reply(AiTaskRig rig, bool vision)
    {
        var llm = new SeeingLlm(vision);
        var assistant = new AssistantService(rig.Db, rig.Sc, new PersonaService(rig.Sc), new KnowledgeService(rig.Db, rig.Sc),
            llm, media: rig.Media);
        var thread = rig.Db.Threads.AsNoTracking().Single();
        Task.Run(() => assistant.GenerateReplyAsync(thread, applyOutcome: false)).GetAwaiter().GetResult();
        return llm.First!;
    }

    [Fact]
    public void A_vision_model_gets_the_customers_photo_on_their_latest_message()
    {
        using var rig = new AiTaskRig();
        rig.Inbound_("[image]", file: (".jpg", "image", Jpeg));
        rig.Inbound_("what's wrong with my phone?");

        var seen = Reply(rig, vision: true);
        var user = seen.Last(m => m.Role == "user");
        var img = Assert.Single(user.Images!);
        Assert.StartsWith("data:image/jpeg;base64,", img);
        Assert.Contains(seen, m => m.Role == "system" && m.Content!.Contains("never say you cannot open"));
    }

    [Fact]
    public void Without_vision_the_model_is_told_it_cannot_see_it_and_sends_no_image()
    {
        using var rig = new AiTaskRig();
        rig.Inbound_("[image]", file: (".jpg", "image", Jpeg));

        var seen = Reply(rig, vision: false);
        Assert.All(seen, m => Assert.Null(m.Images));
        Assert.Contains(seen, m => m.Role == "system" && m.Content!.Contains("cannot see"));
    }

    [Fact]
    public void Only_this_turns_images_go_and_a_too_big_file_is_not_sent()
    {
        using var rig = new AiTaskRig();
        rig.Inbound_("[image]", file: (".jpg", "image", Jpeg));
        var thread = rig.Db.Threads.Single();
        rig.Add(new Message { ThreadId = thread.Id, Direction = MessageDirection.Out, Text = "Thanks!",
            CreatedAt = DateTime.UtcNow });
        rig.Inbound_("and another thing");
        Assert.All(Reply(rig, vision: true), m => Assert.Null(m.Images)); // the photo was answered already

        using var big = new AiTaskRig();
        var huge = new byte[AssistantService.MaxImageBytes + 1];
        Jpeg.CopyTo(huge, 0);
        big.Inbound_("[image]", file: (".jpg", "image", huge));
        var seen = Reply(big, vision: true);
        Assert.All(seen, m => Assert.Null(m.Images));
        Assert.Contains(seen, m => m.Role == "system" && m.Content!.Contains("too large"));
    }
}
