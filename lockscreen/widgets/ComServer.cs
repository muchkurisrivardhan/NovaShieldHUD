using System.Runtime.InteropServices;

namespace Vantage.Lockscreen.Widgets;

/// <summary>
/// A minimal out-of-process COM server: registers one class object for the widget provider and
/// pumps the STA message queue so the widget host can create and drive it. The host starts this
/// process (with -RegisterProcessAsComServer) when a widget is pinned and ends it when idle, so
/// the server itself never decides to exit.
/// </summary>
static class ComServer
{
    [DllImport("ole32.dll")] static extern int CoRegisterClassObject(ref Guid rclsid, [MarshalAs(UnmanagedType.IUnknown)] object pUnk, uint dwClsContext, uint flags, out uint cookie);
    [DllImport("ole32.dll")] static extern int CoRevokeClassObject(uint cookie);
    [DllImport("ole32.dll")] static extern int CoResumeClassObjects();

    const uint CLSCTX_LOCAL_SERVER = 0x4;
    const uint REGCLS_MULTIPLEUSE = 1, REGCLS_SUSPENDED = 4;

    [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG msg);
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    public static void Run(Guid clsid)
    {
        var factory = new ClassFactory(() => new WidgetProvider());
        Marshal.ThrowExceptionForHR(CoRegisterClassObject(ref clsid, factory, CLSCTX_LOCAL_SERVER, REGCLS_MULTIPLEUSE | REGCLS_SUSPENDED, out var cookie));
        CoResumeClassObjects();
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref msg); DispatchMessage(ref msg); }
        CoRevokeClassObject(cookie);
    }
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("00000001-0000-0000-C000-000000000046")]
interface IClassFactory
{
    [PreserveSig] int CreateInstance(IntPtr outer, ref Guid iid, out IntPtr ppv);
    [PreserveSig] int LockServer(bool fLock);
}

sealed class ClassFactory(Func<object> create) : IClassFactory
{
    const int CLASS_E_NOAGGREGATION = unchecked((int)0x80040110);

    public int CreateInstance(IntPtr outer, ref Guid iid, out IntPtr ppv)
    {
        ppv = IntPtr.Zero;
        if (outer != IntPtr.Zero) return CLASS_E_NOAGGREGATION;
        var unk = Marshal.GetIUnknownForObject(create());
        try { return Marshal.QueryInterface(unk, in iid, out ppv); }
        finally { Marshal.Release(unk); }
    }

    public int LockServer(bool fLock) => 0;
}
