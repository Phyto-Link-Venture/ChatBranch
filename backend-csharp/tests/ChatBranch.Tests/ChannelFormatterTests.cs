using ChatBranch.Data.Ai;
using ChatBranch.Domain;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>Markdown → the customer's channel (<see cref="ChannelFormatter"/>) and the
/// built-in "no false booking claim" check (<see cref="BookingClaims"/>), alone and
/// through the real reply loop.</summary>
public class ChannelFormatterTests
{
    // ---- the formatter ------------------------------------------------------------------

    [Theory]
    [InlineData("whatsapp", "Your booking is **Monday 1:30 PM**", "Your booking is *Monday 1:30 PM*")]
    [InlineData("sms", "Your booking is **Monday 1:30 PM**", "Your booking is Monday 1:30 PM")]
    [InlineData("whatsapp", "## Details\n- **Date:** Mon", "*Details*\n- *Date:* Mon")]
    [InlineData("telegram", "## Details\n- **Date:** Mon", "Details\n- Date: Mon")]
    [InlineData("whatsapp", "Pay here [invoice](https://pay.example.com/i/1)", "Pay here invoice: https://pay.example.com/i/1")]
    [InlineData("facebook", "Pay here [invoice](https://pay.example.com/i/1)", "Pay here invoice: https://pay.example.com/i/1")]
    [InlineData("whatsapp", "Was ~~RM 50~~ now RM 40", "Was ~RM 50~ now RM 40")]
    [InlineData("line", "Was ~~RM 50~~ now RM 40", "Was RM 50 now RM 40")]
    [InlineData("whatsapp", "Use code `SAVE10` and *hurry*", "Use code `SAVE10` and *hurry*")]
    [InlineData("instagram", "Use code `SAVE10` and *hurry*", "Use code SAVE10 and hurry")]
    [InlineData("whatsapp", "__Note__: 1. first\n2. second", "*Note*: 1. first\n2. second")]
    [InlineData("viber", "# **Menu**", "Menu")]
    [InlineData("whatsapp", "# **Menu**", "*Menu*")]
    public void Markdown_becomes_what_the_channel_shows(string channel, string input, string expected) =>
        Assert.Equal(expected, ChannelFormatter.ForChannel(input, channel));

    [Theory]
    [InlineData("whatsapp")]
    [InlineData("sms")]
    [InlineData("telegram")]
    [InlineData("email")]
    [InlineData("website")]
    public void Masked_ics_and_maths_are_never_touched(string channel)
    {
        Assert.Equal("IC ******-**-5677", ChannelFormatter.ForChannel("IC ******-**-5677", channel));
        Assert.Equal("Total = 2*3*4 = 24", ChannelFormatter.ForChannel("Total = 2*3*4 = 24", channel));
        Assert.Equal("A * B", ChannelFormatter.ForChannel("A * B", channel));
    }

    [Theory]
    [InlineData("email")]
    [InlineData("website")]
    [InlineData(null)]
    public void Email_and_the_widget_are_left_as_written(string? channel)
    {
        const string md = "## Details\n- **Date:** Mon [link](https://x.example)";
        Assert.Equal(md, ChannelFormatter.ForChannel(md, channel));
    }

    [Theory]
    [InlineData("whatsapp")]
    [InlineData("sms")]
    [InlineData("telegram")]
    public void Formatting_twice_changes_nothing(string channel)
    {
        const string md = "## Details\n- **Date:** Mon, ~~old~~ new, [pay](https://p.example) `X1` IC ******-**-5677";
        var once = ChannelFormatter.ForChannel(md, channel);
        Assert.Equal(once, ChannelFormatter.ForChannel(once, channel));
    }

    // ---- the booking-claim guard ----------------------------------------------------------

    [Theory]
    [InlineData("Your appointment has been cancelled.", false, false, "cancelled")]
    [InlineData("Done — I've cancelled your booking.", false, false, "cancelled")]   // "cancelled your"
    [InlineData("I have successfully canceled the appointment", false, false, "cancelled")]
    [InlineData("Your appointment has been cancelled.", false, true, null)]
    [InlineData("Your booking is confirmed for Mon 1:30 PM.", false, false, "made")]
    [InlineData("Your booking is confirmed for Mon 1:30 PM.", true, false, null)]
    [InlineData("Your slot has been reserved.", false, false, "made")]
    [InlineData("Your booking is on Monday 1:30 PM.", false, false, null)]           // quoting, reply #17
    [InlineData("Shall I book Monday 1:30 PM for you?", false, false, null)]
    public void A_claim_needs_the_tool_that_did_it(string draft, bool booked, bool cancelled, string? expected) =>
        Assert.Equal(expected, BookingClaims.Unbacked(draft, booked, cancelled));

    [Fact]
    public void Status_is_read_from_the_tool_json()
    {
        Assert.True(BookingClaims.HasStatus("{\"ok\":true,\"status\":\"reserved\"}", "reserved"));
        Assert.True(BookingClaims.HasStatus("{\"ok\":true,\"status\":\"cancel_pending\"}", "cancel_pending"));
        Assert.False(BookingClaims.HasStatus("{\"ok\":false,\"status\":\"taken\"}", "reserved"));
        Assert.False(BookingClaims.HasStatus("not json", "reserved"));
        Assert.False(BookingClaims.HasStatus(null, "reserved"));
    }

    // ---- through the real reply loop -------------------------------------------------------

    [Fact]
    public void A_whatsapp_reply_goes_out_in_whatsapp_markup()
    {
        using var r = new BookingRig();
        r.Contact();
        r.Llm.Script = (_, _) => ToolScriptLlm.Says("Your booking is **Monday 1:30 PM** — see you!");

        r.Say("60123000001", "When is my appointment?");

        var ai = Assert.Single(r.Messages("60123000001"), m => m.Source == MessageSource.Ai);
        Assert.Equal("Your booking is *Monday 1:30 PM* — see you!", ai.Text);
        Assert.DoesNotContain("**", ai.Text);
    }

    [Fact]
    public void A_cancellation_no_tool_made_is_never_sent_and_goes_to_a_person()
    {
        using var r = new BookingRig();
        r.Contact();
        r.Skill();
        r.Llm.Script = (_, _) => ToolScriptLlm.Says("Your appointment has been cancelled.");

        r.Say("60123000001", "Please cancel my appointment");

        Assert.DoesNotContain(r.Messages("60123000001"), m => m.Source == MessageSource.Ai && !m.Private);
        var draft = Assert.Single(r.Drafts());
        Assert.NotEqual(AiDraftStatus.AutoSent, draft.Status);
        var ctx = System.Text.Json.JsonSerializer.Serialize(draft.Context);
        Assert.Contains("builtin:no_false_booking_claim", ctx);
        Assert.Contains("no calendar tool did it", ctx);
    }

    [Fact]
    public void A_real_booking_confirms_and_quoting_one_passes()
    {
        using var r = new BookingRig();
        r.Contact();
        r.Skill();
        // books through the tool, then says so with a claim verb — backed, so it sends
        r.Llm.Script = (msgs, tools) =>
        {
            var reply = ToolScriptLlm.BookFirstSlot(msgs, tools);
            return reply.Text.StartsWith("You're booked", StringComparison.Ordinal)
                ? ToolScriptLlm.Says("Your booking is confirmed — " + reply.Text)
                : reply;
        };
        r.Say("60123000001", "Can I book a haircut this week?");
        Assert.Contains(r.Messages("60123000001"), m => m.Source == MessageSource.Ai
            && m.Text.StartsWith("Your booking is confirmed", StringComparison.Ordinal));

        // quoting an existing booking has no claim verb
        r.Llm.Script = (_, _) => ToolScriptLlm.Says("Your booking is on Monday 1:30 PM.");
        r.Say("60123000001", "When is it again?");
        Assert.Contains(r.Messages("60123000001"), m => m.Source == MessageSource.Ai
            && m.Text == "Your booking is on Monday 1:30 PM.");
    }

    [Fact]
    public void Without_a_calendar_skill_the_claim_check_stays_out_of_the_way()
    {
        using var r = new BookingRig();
        r.Contact();
        r.Llm.Script = (_, _) => ToolScriptLlm.Says("Your order has been cancelled.");

        r.Say("60123000001", "Cancel my order please");

        Assert.Contains(r.Messages("60123000001"), m => m.Source == MessageSource.Ai
            && m.Text == "Your order has been cancelled.");
    }
}
