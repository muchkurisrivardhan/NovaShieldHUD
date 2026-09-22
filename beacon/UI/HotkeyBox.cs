using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Beacon.Interop;

namespace Beacon.UI;

/// <summary>
/// A button that records a global chord when clicked.
///
/// Code-only rather than a XAML UserControl because it is purely behavioural - it borrows
/// whatever Button style the hosting window defines, so it looks native to each page it
/// appears on without carrying its own template.
/// </summary>
public sealed class HotkeyBox : Button
{
    public static readonly DependencyProperty ModifiersProperty =
        DependencyProperty.Register(
            nameof(Modifiers), typeof(uint), typeof(HotkeyBox),
            new FrameworkPropertyMetadata(0u,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnChordChanged));

    public static readonly DependencyProperty VirtualKeyProperty =
        DependencyProperty.Register(
            nameof(VirtualKey), typeof(uint), typeof(HotkeyBox),
            new FrameworkPropertyMetadata(0u,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnChordChanged));

    public uint Modifiers
    {
        get => (uint)GetValue(ModifiersProperty);
        set => SetValue(ModifiersProperty, value);
    }

    public uint VirtualKey
    {
        get => (uint)GetValue(VirtualKeyProperty);
        set => SetValue(VirtualKeyProperty, value);
    }

    /// <summary>Raised once a complete chord has been recorded or the binding cleared.</summary>
    public event Action? ChordChanged;

    private bool _capturing;

    public HotkeyBox()
    {
        Focusable = true;
        ToolTip = "Click, then press the shortcut. Esc cancels, Delete clears it.";
        UpdateText();

        Click += (_, _) => BeginCapture();
        LostFocus += (_, _) => EndCapture();
    }

    private static void OnChordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HotkeyBox box && !box._capturing) box.UpdateText();
    }

    private void BeginCapture()
    {
        if (_capturing) return;

        _capturing = true;
        Content = "Press a shortcut…";
        Focus();
        Keyboard.Focus(this);
    }

    private void EndCapture()
    {
        if (!_capturing) return;

        _capturing = false;
        UpdateText();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!_capturing)
        {
            base.OnPreviewKeyDown(e);
            return;
        }

        e.Handled = true;

        // Alt and F10 arrive as Key.System; the real key is in SystemKey.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            EndCapture();
            return;
        }

        if (key is Key.Delete or Key.Back)
        {
            Modifiers = 0;
            VirtualKey = 0;
            EndCapture();
            ChordChanged?.Invoke();
            return;
        }

        // Modifiers alone are not a chord; keep waiting for the real key.
        if (IsModifier(key)) return;

        var modifiers = CurrentModifiers();

        // Without a modifier the chord would swallow that key across the whole desktop.
        if (modifiers == 0)
        {
            Content = "Add Ctrl, Alt, Shift or Win…";
            return;
        }

        Modifiers = modifiers;
        VirtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);

        EndCapture();
        ChordChanged?.Invoke();
    }

    private static uint CurrentModifiers()
    {
        uint modifiers = 0;
        var state = Keyboard.Modifiers;

        if (state.HasFlag(ModifierKeys.Control)) modifiers |= NativeMethods.MOD_CONTROL;
        if (state.HasFlag(ModifierKeys.Alt)) modifiers |= NativeMethods.MOD_ALT;
        if (state.HasFlag(ModifierKeys.Shift)) modifiers |= NativeMethods.MOD_SHIFT;

        // Keyboard.Modifiers never reports Windows, so query the keys directly.
        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin))
            modifiers |= NativeMethods.MOD_WIN;

        return modifiers;
    }

    private static bool IsModifier(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin
            or Key.System;

    private void UpdateText() =>
        Content = Modifiers == 0 || VirtualKey == 0
            ? "Not set"
            : HotkeyManager.Describe(Modifiers, VirtualKey);
}
