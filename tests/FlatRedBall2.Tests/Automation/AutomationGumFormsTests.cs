using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using FlatRedBall2.Automation;
using FlatRedBall2.Tests;
using Gum.Forms;
using Gum.Forms.Controls;
using Gum.Wireframe;
using Microsoft.Xna.Framework;
using Shouldly;
using Xunit;

namespace FlatRedBall2.Tests.Automation;

/// <summary>
/// Injected cursor and text commands reaching real Gum controls through Gum's own input dispatch:
/// no control state is set, no handler is invoked, no synthetic Click. Everything a control sees
/// arrives via <c>FormsUtilities.Update</c> reading the cursor and keyboard automation installed.
/// </summary>
[Collection(HeadlessGumFormsCollection.Name)]
public class AutomationGumFormsTests
{
    private const double FrameSeconds = 1 / 60.0;

    private readonly HeadlessGumFormsFixture _gum;

    public AutomationGumFormsTests(HeadlessGumFormsFixture gum) => _gum = gum;

    /// <summary>
    /// One automation session over a headless engine, stepped one frame at a time in the same order
    /// <see cref="FlatRedBallService.Update"/> uses: drain commands up to the step, poll FRB2 input,
    /// point Gum Forms at automation's cursor and keyboard, run Gum. The engine skips the last call
    /// with no graphics device, so the session makes it directly.
    /// </summary>
    private sealed class Session : IDisposable
    {
        private readonly FlatRedBallService _engine = new();
        private readonly AutomationMode _mode;
        private readonly List<GraphicalUiElement> _roots;
        private long _frame;
        private readonly StringWriter _output = new();

        public Session(HeadlessGumFormsFixture gum)
        {
            _mode = new AutomationMode(_engine, _output);
            Root = gum.CreateRoot();
            _roots = new List<GraphicalUiElement> { Root };
        }

        public GraphicalUiElement Root { get; }

        public void Send(string json) => _mode.ProcessLine(json);

        public void Cursor(int x, int y, bool primary = false) =>
            Send($"{{\"cmd\":\"input\",\"type\":\"cursor\",\"x\":{x},\"y\":{y},\"primary\":{(primary ? "true" : "false")}}}");

        public void Text(string text) =>
            Send($"{{\"cmd\":\"input\",\"type\":\"text\",\"text\":{JsonSerializer.Serialize(text)}}}");

        public void Step(int count = 1)
        {
            for (var i = 0; i < count; i++)
            {
                Send("{\"cmd\":\"step\"}");
                _mode.TryAdvanceFrame(_frame).ShouldBeTrue();
                var now = TimeSpan.FromSeconds(_frame * FrameSeconds);
                _engine.Input.Update(now);
                _mode.EnsureGumInputInstalled();
                FormsUtilities.Update(null!, new GameTime(now, TimeSpan.FromSeconds(FrameSeconds)), _roots);
                _frame++;
            }

            // Input commands answer only on failure, so a malformed command would otherwise show
            // up here as a control that mysteriously never reacts.
            _output.ToString().ShouldBeEmpty();
        }

        public void Dispose()
        {
            // A focused TextBox re-queues its click-off watcher every frame; a focused control
            // would take the next test's text. Both are Gum statics shared across the collection.
            InteractiveGue.CurrentInputReceiver = null;
            InteractiveGue.ClearNextClickActions();
            InteractiveGue.ClearNextPushActions();
        }
    }

    private static T Place<T>(Session session, T control, float x, float y, float width, float height)
        where T : FrameworkElement
    {
        control.X = x;
        control.Y = y;
        control.Width = width;
        control.Height = height;
        session.Root.Children.Add(control.Visual);
        session.Root.UpdateLayout();
        return control;
    }

    [Theory]
    [InlineData(true,  1)] // enabled: the recommended move → down → up sequence is one click
    [InlineData(false, 0)] // disabled: Gum rejects the push, so nothing to click on release
    public void CursorCommand_MoveThenPressHoldRelease_ClicksEnabledButtonOnceAndDisabledNever(bool enabled, int expectedClicks)
    {
        using var session = new Session(_gum);
        var button = Place(session, new Button { IsEnabled = enabled }, x: 10, y: 10, width: 100, height: 50);
        var clicks = 0;
        button.Click += (_, _) => clicks++;

        session.Cursor(60, 35);
        session.Step();
        session.Cursor(60, 35, primary: true);
        // Held for several stepped frames: sticky injected state must read as one push, not one per frame.
        session.Step(4);
        session.Cursor(60, 35);
        session.Step(2);

        clicks.ShouldBe(expectedClicks);
    }

    [Fact]
    public void CursorThenTextCommands_ClickOneTextBoxAndType_FocusesItAndTextLandsOnlyThere()
    {
        using var session = new Session(_gum);
        var clicked = Place(session, new TextBox(), x: 200, y: 10, width: 150, height: 40);
        var other   = Place(session, new TextBox(), x: 200, y: 100, width: 150, height: 40);

        session.Cursor(250, 30);
        session.Step();
        session.Cursor(250, 30, primary: true);
        session.Step();
        session.Cursor(250, 30);
        session.Step();

        // Focus came from Gum's own push handling on the TextBox, not from setting IsFocused.
        clicked.IsFocused.ShouldBeTrue();
        other.IsFocused.ShouldBeFalse();

        session.Text("MATCH-7F2A");
        session.Step();

        clicked.Text.ShouldBe("MATCH-7F2A");
        (other.Text ?? "").ShouldBeEmpty();
    }
}
