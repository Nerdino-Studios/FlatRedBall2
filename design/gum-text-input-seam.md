# Automation text input: the Gum seam

## Problem

Automation mode can inject keys (`{"cmd":"input","type":"key",...}`), but injected keys never
become *characters* in a Gum `TextBox`. An automation client cannot type a MatchId into a text
field, which is the one interaction a Playwright-style driver needs most.

## How text actually reaches a TextBox

```
FlatRedBallService.Update
  → GumService.Update                              (src/FlatRedBallService.cs:1505)
    → Gum.Forms.FormsUtilities.Update(game, gameTime, roots)
      → keyboard.Activity(gameTimeSeconds)         // the static `keyboard` field
      → GueInteractiveExtensionMethods.DoUiActivityRecursively(roots, cursor, keyboard, t)
        → InteractiveGue.CurrentInputReceiver.DoKeyboardAction(keyboard)
          → TextBoxBase:
                foreach (var k in keyboard.KeysTyped)        HandleKeyDown(k, shift, alt, ctrl);
                foreach (var c in keyboard.GetStringTyped()) HandleCharEntered(c);
```

Focus gating is entirely Gum's: only `InteractiveGue.CurrentInputReceiver` gets
`DoKeyboardAction`. `TextBoxBase.IsEnabled = false` sets `IsFocused = false`, which clears
`CurrentInputReceiver`. So "unfocused or disabled controls receive no text" falls out of the
existing engine — it needs tests, not new code.

## Why keys are reachable but characters are not

`MonoGameGum.Input.Keyboard.GetStringTyped()` early-outs:

```csharp
if (windowTextInputBuffer != null) return processedStringFromWindow;
```

`windowTextInputBuffer` is fed *only* by the private `HandleWindowTextInput` handler attached to
`GameWindow.TextInput`. In any real windowed game that subscription succeeds, so the key→char
fallback below it is dead code. And:

- `GetStringTyped` is an interface implementation, so `virtual final` in IL — not overridable.
- `GameWindow.OnTextInput` is `protected internal` in MonoGame — unreachable from outside that
  assembly.

Keys, by contrast, are reachable today: `Keyboard.KeyboardStateProcessor` is a public settable
property and every `KeyboardStateProcessor` member is `virtual`. That is enough for Backspace and
arrows but not for a single printable character, so it is not a solution on its own.

## The seam

`Gum.Forms.FormsUtilities` already stores its keyboard as the platform-neutral interface:

```csharp
static IInputReceiverKeyboard keyboard;
public static IInputReceiverKeyboard Keyboard => keyboard;
```

and already exposes the symmetric setter for the cursor:

```csharp
public static void SetCursor(ICursor cursor)
{
    FormsUtilities.cursor = cursor;
    FrameworkElement.MainCursor = cursor;
}
```

The change is the matching `SetKeyboard`. FRB2 then supplies its own `IInputReceiverKeyboard`
and owns both halves — keys and characters — with no reflection, no control-specific hook, and
no change to how Gum routes focus.

This is a general-purpose seam (virtual keyboards, IME, accessibility, remote input), not a test
hook, which is what makes it reasonable to land upstream.

## Consequences to keep in mind

- `MonoGameGum.GumService.Keyboard` is `(FormsUtilities.Keyboard as Keyboard)!` — it returns
  `null` once a non-`MonoGameGum.Input.Keyboard` is installed. FRB2 does not use it; Gum samples
  do. Swap the keyboard only while automation is active.
- `FormsUtilities.Uninitialize` nulls the field, so a re-`Initialize` restores Gum's own keyboard.
- `FrameworkElement.KeyboardsForUiControl` (tab/arrow UI navigation) is a separate list that
  `SetKeyboard` deliberately does not touch, matching `SetCursor`'s scope.

## Frame ordering

```
1438  AutomationMode.TryAdvanceFrame   // drains input/query/set inline, stops at `step`
1467  Input.Update
1505  GumService.Update                // keyboard.Activity → DoKeyboardAction
1521  AutomationMode.FlushStepResponse
```

A `text` command queued before a `step` is buffered during the drain and consumed by Gum inside
that same stepped frame — identical in shape to the existing `input` types, and deterministic on
replay.

## Local development loop

The fork is `github.com/Nerdino-Studios/Gum` at `../Gum`, branch `automation-text-input`. It is
consumed as `ProjectReference`s, not packages — see `Gum.Source.props` for the reference sets,
the `GumSourcePath` override, and why the KernSmith split falls where it does. A Gum edit shows
up in the next FRB2 build with no repack and no version bump.

If Gum's *packaging* ever needs verifying (which files ship, TFM slices, analyzers), pack to a
folder feed instead — but bump the version every time, because NuGet caches by exact version and
silently restores the stale package otherwise.
