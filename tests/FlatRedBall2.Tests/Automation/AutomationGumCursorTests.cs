using System;
using FlatRedBall2.Automation;
using FlatRedBall2.Input;
using Shouldly;
using Xunit;

namespace FlatRedBall2.Tests.Automation;

// --- The Gum ICursor FRB2 hands to Gum Forms while automation is active ---

public class AutomationGumCursorTests
{
    private static TimeSpan Sec(double s) => TimeSpan.FromSeconds(s);

    [Fact]
    public void Activity_AfterInjectedMove_ReportsNewPositionAndChangeSinceLastFrame()
    {
        var frb = new Cursor();
        var gum = new AutomationGumCursor(frb);

        frb.Inject(100, 40, primary: false, secondary: false);
        frb.Update(Sec(0));
        gum.Activity(0);
        frb.Inject(130, 30, primary: false, secondary: false);
        frb.Update(Sec(0.016));
        gum.Activity(0.016);

        gum.X.ShouldBe(130);
        gum.Y.ShouldBe(30);
        // Gum raises RollOver/Dragging off these deltas, so they must track the FRB2 cursor frame to frame.
        gum.XChange.ShouldBe(30);
        gum.YChange.ShouldBe(-10);
    }

    [Fact]
    public void CustomCursor_Set_IsStoredWithoutAWindow()
    {
        var gum = new AutomationGumCursor(new Cursor());

        // FormsUtilities.Update assigns this whenever the hovered control asks for a cursor kind.
        // Gum's own cursor forwards to Mouse.SetCursor; automation has no window to forward to.
        gum.CustomCursor = global::Gum.Wireframe.Cursors.SizeWE;

        gum.CustomCursor.ShouldBe(global::Gum.Wireframe.Cursors.SizeWE);
    }

    [Fact]
    public void PrimaryPushDownClick_AcrossPressHoldRelease_FollowTheInjectedFrb2Edges()
    {
        var frb = new Cursor();
        var gum = new AutomationGumCursor(frb);

        frb.Inject(0, 0, primary: true, secondary: false);
        frb.Update(Sec(0));
        gum.Activity(0);
        gum.PrimaryPush.ShouldBeTrue();
        gum.PrimaryDown.ShouldBeTrue();
        gum.PrimaryClick.ShouldBeFalse();

        // Held: no new push, so Gum keeps WindowPushed instead of re-pushing.
        frb.Update(Sec(0.016));
        gum.Activity(0.016);
        gum.PrimaryPush.ShouldBeFalse();
        gum.PrimaryDown.ShouldBeTrue();

        frb.Inject(0, 0, primary: false, secondary: false);
        frb.Update(Sec(0.032));
        gum.Activity(0.032);
        gum.PrimaryClick.ShouldBeTrue();
        gum.PrimaryDown.ShouldBeFalse();
        gum.LastPrimaryClickTime.ShouldBe(0.032);
    }
}
