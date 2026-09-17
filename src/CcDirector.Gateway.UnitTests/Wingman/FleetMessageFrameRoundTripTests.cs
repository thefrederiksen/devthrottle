using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// <see cref="FleetMessaging.TryParseFrame"/>, read back against the BUILDER rather than against a copy of its
/// format (the Wingman tab, version 3, item 1, slice 4).
///
/// WHY THE ROUND TRIP IS THE TEST. The Now view names who last asked a working session something, and the only
/// honest way to name a sending session is to recognise a string this very file wrote. A parser pinned to a
/// hand-written copy of the frame would keep passing the day the frame changed, and the Now view would quietly
/// stop naming anyone - or worse, show the delivery wrapper to the owner as though the sender had typed it. So
/// every case here builds a real frame with <see cref="FleetMessaging.BuildFramedMessage"/> and requires the
/// parser to get the sender and the text back out.
///
/// PARKED SUITE. Gateway.UnitTests runs under -Parked.
/// </summary>
public sealed class FleetMessageFrameRoundTripTests
{
    private const string Sender = "11111111-1111-1111-1111-111111111111";
    private const string Body = "Allow the merge, tag straight after it, and retry the changelog message.";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_named_sender_survives_the_round_trip_with_and_without_the_reply_hint(bool replyHint)
    {
        var framed = FleetMessaging.BuildFramedMessage(Sender, "Dev Reports - Architect", "SOREN_NORTH", Body, replyHint);

        Assert.True(FleetMessaging.TryParseFrame(framed, out var frame));
        Assert.Equal("Dev Reports - Architect", frame.SenderName);
        Assert.Equal(Body, frame.Text);
    }

    /// <summary>A sender the server could not name is still a fleet message - it just names nobody. A short
    /// identifier is not a name, and putting one where a name goes is the guess this whole parser avoids.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_unnamed_sender_is_recognised_as_a_fleet_message_that_names_nobody(bool replyHint)
    {
        var framed = FleetMessaging.BuildFramedMessage(Sender, null, "SOREN_NORTH", Body, replyHint);

        Assert.True(FleetMessaging.TryParseFrame(framed, out var frame));
        Assert.Null(frame.SenderName);
        Assert.Equal(Body, frame.Text);
    }

    /// <summary>The third header form: no identifier at all, so no reply hint either.</summary>
    [Fact]
    public void A_sender_with_no_identifier_at_all_is_still_recognised()
    {
        var framed = FleetMessaging.BuildFramedMessage(null, null, "SOREN_NORTH", Body);

        Assert.True(FleetMessaging.TryParseFrame(framed, out var frame));
        Assert.Null(frame.SenderName);
        Assert.Equal(Body, frame.Text);
    }

    /// <summary>A name with brackets of its own does not lose its tail: the machine's bracket is the LAST one.
    /// </summary>
    [Fact]
    public void A_sender_name_containing_brackets_comes_back_whole()
    {
        var framed = FleetMessaging.BuildFramedMessage(Sender, "Dev Reports (phase 2) - Architect", "SOREN_NORTH", Body);

        Assert.True(FleetMessaging.TryParseFrame(framed, out var frame));
        Assert.Equal("Dev Reports (phase 2) - Architect", frame.SenderName);
    }

    /// <summary>The builder collapses a multi-line message to one line, so that is what comes back - the parser
    /// undoes the FRAME, never the builder's own normalising.</summary>
    [Fact]
    public void A_multi_line_message_comes_back_as_the_one_line_the_builder_made_of_it()
    {
        var framed = FleetMessaging.BuildFramedMessage(Sender, "Architect", "SOREN_NORTH", "First line.\n\nSecond line.");

        Assert.True(FleetMessaging.TryParseFrame(framed, out var frame));
        Assert.Equal("First line. Second line.", frame.Text);
    }

    /// <summary>A body that ENDS in something shaped like the reply hint but is not one keeps every word. The hint
    /// is only removed when the whole tail is one.</summary>
    [Fact]
    public void A_body_that_merely_mentions_the_reply_command_keeps_it()
    {
        var mentions = "Run cc-devthrottle message send abc123 \"done\" when the gate is green.";
        var framed = FleetMessaging.BuildFramedMessage(Sender, "Architect", "SOREN_NORTH", mentions, includeReplyHint: false);

        Assert.True(FleetMessaging.TryParseFrame(framed, out var frame));
        Assert.Equal(mentions, frame.Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Allow the merge.")]
    [InlineData("Message from a person who types like this")]
    [InlineData("Message [message from")]                 // the header opens and never closes
    public void Anything_this_gateway_did_not_frame_is_not_claimed_to_be_a_fleet_message(string? text)
    {
        Assert.False(FleetMessaging.TryParseFrame(text, out var frame));
        Assert.Null(frame.SenderName);
    }
}
