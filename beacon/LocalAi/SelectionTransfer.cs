using System.Runtime.InteropServices;
using System.Windows;
using Beacon.Interop;

namespace Beacon.LocalAi;

/// <summary>
/// Reads the text selected in whatever app the user is in, and puts new text in its place,
/// by the one route every app understands: Ctrl+C and Ctrl+V. The clipboard is borrowed for
/// that, and whatever was on it before is put back afterwards.
/// </summary>
public static class SelectionTransfer
{
    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);

    const byte VK_CONTROL = 0x11, VK_SHIFT = 0x10, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    /// <summary>
    /// The hotkey's own keys are still held when it fires; a Ctrl+C sent now would reach the
    /// app as Ctrl+Alt+C. Wait (briefly) for the user to let go.
    /// </summary>
    public static async Task WaitForModifiersReleasedAsync()
    {
        for (var i = 0; i < 100; i++)   // up to 1.5 s
        {
            if (!Down(VK_CONTROL) && !Down(VK_SHIFT) && !Down(VK_MENU) && !Down(VK_LWIN) && !Down(VK_RWIN)) return;
            await Task.Delay(15);
        }
        static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
    }

    /// <summary>What was on the clipboard, every format that could be read, to put back later.</summary>
    public sealed class Snapshot
    {
        internal readonly List<(string Format, object Data)> Items = [];
    }

    public static Snapshot Save()
    {
        var snapshot = new Snapshot();
        try
        {
            if (Clipboard.GetDataObject() is { } data)
                foreach (var format in data.GetFormats(false))
                {
                    try { if (data.GetData(format, false) is { } value) snapshot.Items.Add((format, value)); }
                    catch (Exception e) when (e is COMException or OutOfMemoryException or ExternalException) { }
                }
        }
        catch (ExternalException) { }
        return snapshot;
    }

    public static void Restore(Snapshot snapshot)
    {
        Retry(() =>
        {
            if (snapshot.Items.Count == 0) { Clipboard.Clear(); return; }
            var data = new DataObject();
            foreach (var (format, value) in snapshot.Items)
            {
                try { data.SetData(format, value); }
                catch (Exception e) when (e is COMException or ArgumentException) { }
            }
            Clipboard.SetDataObject(data, copy: true);
        });
    }

    /// <summary>Sends Ctrl+C and returns the text it copied, or "" when nothing was selected.</summary>
    public static async Task<string> CopySelectionAsync()
    {
        var before = GetClipboardSequenceNumber();
        Chord(0x43);   // C
        for (var i = 0; i < 40; i++)   // up to 600 ms: some apps copy slowly
        {
            await Task.Delay(15);
            if (GetClipboardSequenceNumber() != before) break;
        }
        if (GetClipboardSequenceNumber() == before) return "";
        await Task.Delay(30);   // let the copying app finish writing every format
        string text = "";
        Retry(() => text = Clipboard.ContainsText() ? Clipboard.GetText() : "");
        return text;
    }

    /// <summary>
    /// Like <see cref="CopySelectionAsync"/>, but also reports a file selection (Explorer, the
    /// desktop) and whether the Ctrl+C actually copied anything just now. The caller uses
    /// <c>Changed</c> to tell a real empty selection from a stale clipboard, and falls back to
    /// <c>Files</c> when the selection was a file rather than text.
    /// </summary>
    public static async Task<(bool Changed, string Text, IReadOnlyList<string> Files)> CopySelectionRichAsync()
    {
        var before = GetClipboardSequenceNumber();
        Chord(0x43);   // C
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(15);
            if (GetClipboardSequenceNumber() != before) break;
        }
        if (GetClipboardSequenceNumber() == before) return (false, "", []);
        await Task.Delay(30);
        string text = "";
        var files = new List<string>();
        Retry(() =>
        {
            text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
            if (Clipboard.ContainsFileDropList())
                foreach (string? f in Clipboard.GetFileDropList())
                    if (!string.IsNullOrEmpty(f)) files.Add(f);
        });
        return (true, text, files);
    }

    /// <summary>Brings <paramref name="target"/> back to the front and pastes <paramref name="text"/> over its selection.</summary>
    public static async Task PasteAsync(IntPtr target, string text)
    {
        Retry(() => Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text), copy: true));
        if (target != IntPtr.Zero) NativeMethods.SetForegroundWindow(target);
        await Task.Delay(120);
        Chord(0x56);   // V
        await Task.Delay(350);   // the paste reads the clipboard after the keystroke: wait before restoring it
    }

    static void Chord(byte key)
    {
        NativeMethods.keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        NativeMethods.keybd_event(key, 0, 0, UIntPtr.Zero);
        NativeMethods.keybd_event(key, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
        NativeMethods.keybd_event(VK_CONTROL, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    /// <summary>Another app may hold the clipboard open for a moment (CLIPBRD_E_CANT_OPEN).</summary>
    static void Retry(Action action)
    {
        for (var i = 0; ; i++)
        {
            try { action(); return; }
            catch (ExternalException) when (i < 8) { Thread.Sleep(25); }
            catch (ExternalException) { return; }
        }
    }
}
