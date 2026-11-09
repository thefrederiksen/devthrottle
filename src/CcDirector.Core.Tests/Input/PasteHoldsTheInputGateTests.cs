using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Tests.Wingman;
using Xunit;

namespace CcDirector.Core.Tests.Input;

/// <summary>
/// A PASTE HOLDS THE SESSION'S INPUT GATE (issue #4816).
///
/// A paste is written in chunks with a real wait between them - eight bytes every fifteen milliseconds
/// unbracketed. Until this change the tunnel's paste called <c>SendInput</c> per chunk and held nothing
/// across the gaps, which was survivable only because the paste was blocking the Director's one command
/// delivery loop, so nothing else could arrive. With the loop freed, a prompt, a <c>/clear</c> or a
/// <c>/compact</c> arriving mid-paste would open a GUARDED SECTION - and from that moment
/// <c>Session.SendInput</c> holds the paste's remaining chunks and writes them after the send, so the
/// person's text would arrive split around somebody else's.
/// </summary>
public class PasteHoldsTheInputGateTests
{
    [Fact]
    public async Task TheGateIsHeldBeforeThePasteYields_WhichTheOldPathNeverDid()
    {
        // WHY THIS IS READ AT EXACTLY THIS MOMENT. PasteIntoTerminalAsync takes the gate before its first
        // incomplete await - BeginInputAsync returns an already-completed task when no guarded section is
        // open - so the count is raised by the time the call hands its task back, with no clock and no race
        // in the reading. Measured on the old path, which wrote each chunk through SendInput and held
        // nothing between them, this reads 0 every time: 289,650 samples across a paste, not one of them 1.
        var manager = new SessionManager(new AgentOptions { ClaudePath = TestShell.Path });
        try
        {
            var session = manager.CreateEmbeddedSession(Path.GetTempPath(), null, new BufferOnlyBackend());

            var pasting = session.PasteIntoTerminalAsync(
                new string('x', 96), null, SubmissionProvenance.Typed("test", "device"));

            Assert.Equal(1, session.InputsInFlight);

            // AND THE GATE THAT ACTUALLY STOPS A SEND. SendInProgress reads _submitGate - "held for the
            // whole of one terminal send, from the first keystroke to the end of its proof" - and an
            // ordinary send waits on it. The first version of this change took only the in-flight mark,
            // which nothing but the guarded prompt and the doorbell even read, and a review measured a
            // SendTextAsync completing in 47ms inside a 2.2-second paste. This reads false on that version.
            Assert.True(session.SendInProgress,
                "an ordinary send waits on _submitGate, so a paste that does not hold it is typed into");

            var answer = await pasting;

            Assert.Equal(InputAnswerKind.Written, answer.Kind);
            Assert.Equal(0, session.InputsInFlight);        // and both let go again at the end
            Assert.False(session.SendInProgress);
        }
        finally { manager.Dispose(); }
    }
}
