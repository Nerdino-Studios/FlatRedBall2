using System;
using System.IO;
using System.Text.Json;
using FlatRedBall2.Automation;
using Shouldly;
using Xunit;

using FrbKeyboard = FlatRedBall2.Input.Keyboard;
using GumKeys = Gum.Forms.Input.Keys;
using XnaKeys = Microsoft.Xna.Framework.Input.Keys;

namespace FlatRedBall2.Tests.Automation;

// --- The IInputReceiverKeyboard FRB2 hands to Gum while automation is active ---

public class AutomationGumKeyboardTests
{
    [Fact]
    public void GetStringTyped_AfterActivity_ReturnsQueuedTextThenIsEmptyNextFrame()
    {
        var gum = new AutomationGumKeyboard(new FrbKeyboard());

        gum.QueueText("ab");
        gum.Activity(0);
        gum.GetStringTyped().ShouldBe("ab");

        // Activity is the frame boundary, so text is delivered for exactly one frame — a second
        // delivery would type everything twice.
        gum.Activity(1);
        gum.GetStringTyped().ShouldBeEmpty();
    }

    [Fact]
    public void GetStringTyped_TwoQueuesBeforeOneActivity_ConcatenatesInOrder()
    {
        var gum = new AutomationGumKeyboard(new FrbKeyboard());

        gum.QueueText("ab");
        gum.QueueText("cd");
        gum.Activity(0);

        gum.GetStringTyped().ShouldBe("abcd");
    }

    [Fact]
    public void KeysTyped_KeyHeldFarPastRepeatDelay_ReportsThePressOnceAndNeverRepeats()
    {
        var frb = new FrbKeyboard();
        var gum = new AutomationGumKeyboard(frb);

        frb.InjectKey(XnaKeys.Back, down: true);
        frb.Update();
        gum.Activity(0);
        gum.KeysTyped.ShouldContain(GumKeys.Back);

        // Gum's own keyboard starts repeating after RepeatDelay (500ms). Automation must not:
        // repeat is wall-clock driven, so it would make a replay depend on how many frames the
        // driver happened to step. 120 frames is well past that delay at any frame rate.
        for (var frame = 1; frame <= 120; frame++)
        {
            frb.Update();
            gum.Activity(frame);
            gum.KeysTyped.ShouldBeEmpty();
        }
    }

    [Fact]
    public void GumKeysAndXnaKeys_ShareNamesAndNumericValues()
    {
        // AutomationGumKeyboard casts straight between the two enums. Gum documents that as a
        // permanent commitment (Gum.Forms.Input.Keys "mirrors Microsoft.Xna.Framework.Input.Keys
        // exactly"), so pin it — a silent divergence upstream would misroute every key.
        foreach (var xna in Enum.GetValues<XnaKeys>())
            Enum.GetName((GumKeys)(int)xna).ShouldBe(Enum.GetName(xna));
    }
}

// --- The {"cmd":"input","type":"text"} protocol command ---

public class AutomationModeTextCommandTests
{
    private static (AutomationMode mode, StringWriter output) Make()
    {
        var output = new StringWriter();
        return (new AutomationMode(new FlatRedBallService(), output), output);
    }

    [Fact]
    public void TextCommand_Valid_ProducesNoResponse()
    {
        var (mode, output) = Make();

        mode.ProcessLine("{\"cmd\":\"input\",\"type\":\"text\",\"text\":\"abc\"}");
        mode.TryAdvanceFrame(0);

        // Same contract as the other input types: silence means accepted.
        output.ToString().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("{\"cmd\":\"input\",\"type\":\"text\"}")]
    [InlineData("{\"cmd\":\"input\",\"type\":\"text\",\"text\":42}")]
    public void TextCommand_MissingOrNonStringText_RespondsWithError(string line)
    {
        var (mode, output) = Make();

        mode.ProcessLine(line);
        mode.TryAdvanceFrame(0);

        output.ToString().ShouldContain("\"ok\":false");
    }

    [Theory]
    [InlineData("\\n")]
    [InlineData("\\t")]
    [InlineData("\\u0000")]
    public void TextCommand_ControlCharacter_RespondsWithErrorAndDeliversNothing(string escaped)
    {
        var (mode, output) = Make();

        // Gum silently drops control characters. Automation rejects them: a client sending "\n"
        // expecting Enter would otherwise face a text box that never changes, with nothing on the
        // wire to say why. Rejection is all-or-nothing — no partial "a" sneaking through.
        mode.ProcessLine("{\"cmd\":\"input\",\"type\":\"text\",\"text\":\"a" + escaped + "b\"}");
        mode.TryAdvanceFrame(0);
        mode.GumKeyboard.Activity(0);

        // Parsed rather than substring-matched: the raw line spells the code point "U+000A",
        // because System.Text.Json's default encoder escapes '+'.
        using var response = JsonDocument.Parse(output.ToString());
        response.RootElement.GetProperty("ok").GetBoolean().ShouldBeFalse();
        response.RootElement.GetProperty("error").GetString().ShouldContain("U+00");
        mode.GumKeyboard.GetStringTyped().ShouldBeEmpty();
    }

    [Fact]
    public void TextCommand_QueuedBeforeStep_IsDeliveredOnThatSteppedFrame()
    {
        var mode = new AutomationMode(new FlatRedBallService(), new StringWriter());

        mode.ProcessLine("{\"cmd\":\"input\",\"type\":\"text\",\"text\":\"hi\"}");
        mode.ProcessLine("{\"cmd\":\"step\"}");

        mode.TryAdvanceFrame(0).ShouldBeTrue();
        mode.GumKeyboard.Activity(0);

        mode.GumKeyboard.GetStringTyped().ShouldBe("hi");
    }
}

// --- Installing the keyboard and cursor into Gum, and the engine tick that does it ---

// Installs into Gum's static input slots, so it lives with the other Gum-static tests.
[Collection(HeadlessGumFormsCollection.Name)]
public class AutomationGumInputInstallTests
{
    [Fact]
    public void EngineUpdate_WithAutomationActive_InstallsTheAutomationKeyboardAndCursorIntoGumForms()
    {
        var previousKeyboard = Gum.Forms.FormsUtilities.Keyboard;
        var previousCursor = Gum.Forms.FormsUtilities.Cursor;
        var engine = new FlatRedBallService();
        try
        {
            engine.Start<Screen>();
            // Generous count so the tick loop never runs out of steps while waiting on the
            // reader thread to deliver the command.
            engine.StartAutomationMode(
                seed: 0,
                input: new StringReader("{\"cmd\":\"step\",\"count\":10000}\n"),
                output: new StringWriter());

            for (var i = 0; i < 500 && Gum.Forms.FormsUtilities.Keyboard is not AutomationGumKeyboard; i++)
            {
                engine.Update(new Microsoft.Xna.Framework.GameTime());
                System.Threading.Thread.Sleep(2);
            }

            // StartAutomationMode deliberately does not install, so only the update tick can
            // make this pass.
            Gum.Forms.FormsUtilities.Keyboard.ShouldBeOfType<AutomationGumKeyboard>();
            Gum.Forms.FormsUtilities.Cursor.ShouldBeOfType<AutomationGumCursor>();
        }
        finally
        {
            engine.Shutdown();
            // Gum has no public way back to "no keyboard", so a run that started without one keeps ours.
            if (previousKeyboard != null)
            {
                Gum.Forms.FormsUtilities.SetKeyboard(previousKeyboard);
            }

            Gum.Forms.FormsUtilities.SetCursor(previousCursor);
        }
    }

    [Fact]
    public void EngineUpdate_WithAutomationActiveAndNoStepPending_DoesNotThrowHeadless()
    {
        var engine = new FlatRedBallService();
        try
        {
            engine.Start<Screen>();
            engine.StartAutomationMode(seed: 0, input: new StringReader(""), output: new StringWriter());

            // An ungranted tick asks the Game to suppress its draw. A headless engine has no
            // Game and no draw to suppress, so this is a no-op rather than a crash.
            Should.NotThrow(() => engine.Update(new Microsoft.Xna.Framework.GameTime()));
        }
        finally
        {
            engine.Shutdown();
        }
    }

    [Fact]
    public void EnsureGumInputInstalled_AfterSomethingElseReplacesKeyboardAndCursor_ReinstallsBoth()
    {
        var previousKeyboard = Gum.Forms.FormsUtilities.Keyboard;
        var previousCursor = Gum.Forms.FormsUtilities.Cursor;
        try
        {
            var mode = new AutomationMode(new FlatRedBallService(), new StringWriter());

            mode.EnsureGumInputInstalled();
            Gum.Forms.FormsUtilities.Keyboard.ShouldBeSameAs(mode.GumKeyboard);
            Gum.Forms.FormsUtilities.Cursor.ShouldBeSameAs(mode.GumCursor);

            // Stands in for FormsUtilities.InitializeDefaults, which overwrites both fields
            // depending on when the game calls Initialize.
            var other = new AutomationMode(new FlatRedBallService(), new StringWriter());
            Gum.Forms.FormsUtilities.SetKeyboard(other.GumKeyboard);
            Gum.Forms.FormsUtilities.SetCursor(other.GumCursor);
            mode.EnsureGumInputInstalled();

            Gum.Forms.FormsUtilities.Keyboard.ShouldBeSameAs(mode.GumKeyboard);
            Gum.Forms.FormsUtilities.Cursor.ShouldBeSameAs(mode.GumCursor);
        }
        finally
        {
            // Gum has no public way back to "no keyboard", so a run that started without one keeps ours.
            if (previousKeyboard != null)
            {
                Gum.Forms.FormsUtilities.SetKeyboard(previousKeyboard);
            }

            Gum.Forms.FormsUtilities.SetCursor(previousCursor);
        }
    }
}
