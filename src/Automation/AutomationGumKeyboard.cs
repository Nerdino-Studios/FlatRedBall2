using System;
using System.Collections.Generic;
using System.Text;
using FlatRedBall2.Input;
using Gum.Wireframe;

using GumKeys = Gum.Forms.Input.Keys;
using XnaKeys = Microsoft.Xna.Framework.Input.Keys;

namespace FlatRedBall2.Automation;

/// <summary>
/// The keyboard FRB2 hands to Gum Forms while automation mode is active, so injected keys and
/// injected text reach the focused control through Gum's ordinary input path.
/// </summary>
/// <remarks>
/// Gum's own <c>MonoGameGum.Input.Keyboard</c> cannot serve automation: the characters it reports
/// from <c>GetStringTyped</c> come exclusively from the OS <c>GameWindow.TextInput</c> event, via
/// a private buffer, and that path has no injection seam. This type replaces it wholesale through
/// <c>FormsUtilities.SetKeyboard</c> and sources everything from <see cref="InputManager"/>
/// instead — the same injected state the <c>input type:key</c> command already writes. See
/// design/gum-text-input-seam.md.
/// <para>
/// Key state is read live from the FRB2 keyboard; typed text is buffered and released one frame
/// at a time by <see cref="Activity"/>, mirroring how Gum's keyboard promotes its window buffer.
/// </para>
/// <para>
/// <b>No key repeat.</b> Gum's keyboard re-reports a held key every <c>RepeatRate</c> once
/// <c>RepeatDelay</c> has elapsed, so holding Backspace deletes repeatedly. Automation reports a
/// key as typed only on the frame it goes down, however long it is held. Repeat is wall-clock
/// driven, which would make a recorded NDJSON session replay differently depending on how many
/// frames the driver happened to step — determinism matters more here than input fidelity. Send
/// one down/up pair per intended keystroke.
/// </para>
/// </remarks>
internal sealed class AutomationGumKeyboard : IInputReceiverKeyboard
{
    // Scanned once per frame to build the KeysTyped snapshot. Cached because Enum.GetValues
    // allocates, and this would otherwise run every frame for the life of the session.
    private static readonly XnaKeys[] AllKeys = Enum.GetValues<XnaKeys>();

    private readonly IKeyboard _keyboard;
    private readonly StringBuilder _pendingText = new();
    private readonly List<GumKeys> _keysTyped = new();
    private string _textThisFrame = string.Empty;

    internal AutomationGumKeyboard(IKeyboard keyboard) => _keyboard = keyboard;

    /// <summary>
    /// Queues text for delivery on the next frame. Several calls before one <see cref="Activity"/>
    /// concatenate, matching how a burst of real window text events coalesces into one frame.
    /// </summary>
    /// <remarks>
    /// Game-thread only — automation commands are drained on the game thread, so unlike Gum's
    /// keyboard (fed by a window event on another thread) this needs no lock.
    /// </remarks>
    internal void QueueText(string text) => _pendingText.Append(text);

    /// <inheritdoc/>
    public void Activity(double gameTime)
    {
        _textThisFrame = _pendingText.ToString();
        _pendingText.Clear();

        // Snapshot rather than compute per read: KeysTyped is read once per focused control per
        // frame, and a snapshot cannot change underneath an in-progress enumeration.
        _keysTyped.Clear();
        foreach (var key in AllKeys)
        {
            if (_keyboard.WasKeyPressed(key))
                _keysTyped.Add((GumKeys)(int)key);
        }
    }

    /// <inheritdoc/>
    public string GetStringTyped() => _textThisFrame;

    /// <inheritdoc/>
    public IEnumerable<GumKeys> KeysTyped => _keysTyped;

    /// <inheritdoc/>
    public bool IsShiftDown => KeyDown(GumKeys.LeftShift) || KeyDown(GumKeys.RightShift);

    /// <inheritdoc/>
    public bool IsCtrlDown => KeyDown(GumKeys.LeftControl) || KeyDown(GumKeys.RightControl);

    /// <inheritdoc/>
    public bool IsAltDown => KeyDown(GumKeys.LeftAlt) || KeyDown(GumKeys.RightAlt);

    /// <inheritdoc/>
    public bool KeyDown(GumKeys key) => _keyboard.IsKeyDown((XnaKeys)(int)key);

    /// <inheritdoc/>
    public bool KeyPushed(GumKeys key) => _keyboard.WasKeyPressed((XnaKeys)(int)key);

    /// <inheritdoc/>
    public bool KeyReleased(GumKeys key) => _keyboard.WasKeyJustReleased((XnaKeys)(int)key);

    /// <summary>
    /// Whether the key was typed this frame. Equivalent to <see cref="KeyPushed"/> — automation
    /// never key-repeats; see the remarks on <see cref="AutomationGumKeyboard"/>.
    /// </summary>
    public bool KeyTyped(GumKeys key) => KeyPushed(key);
}
