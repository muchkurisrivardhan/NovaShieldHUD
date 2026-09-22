using System.Runtime.InteropServices;

namespace Beacon.Interop;

/// <summary>
/// Exact system volume, through the Core Audio API.
///
/// The media keys only nudge the volume in fixed steps, which cannot express "set volume to
/// 75%" - the step size is a driver detail, so counting key taps lands somewhere different on
/// every machine. IAudioEndpointVolume sets a real scalar, and needs no extra package: a few
/// COM declarations reach it directly.
///
/// Every entry point returns a bool or null rather than throwing. A PC with no audio endpoint
/// at all is unusual but perfectly legal, and it must not take a command down with it.
/// </summary>
internal static class AudioEndpoint
{
    public static int? GetVolumePercent()
    {
        try
        {
            var volume = Open();
            if (volume is null) return null;

            return volume.GetMasterVolumeLevelScalar(out var scalar) == 0
                ? (int)Math.Round(scalar * 100)
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Sets the master volume. <paramref name="percent"/> is clamped to 0-100.</summary>
    public static bool TrySetVolumePercent(int percent)
    {
        try
        {
            var volume = Open();
            if (volume is null) return false;

            var scalar = Math.Clamp(percent, 0, 100) / 100f;
            var context = Guid.Empty;

            // Setting a level does not clear mute, which would leave "set volume to 60"
            // looking like it did nothing at all.
            if (percent > 0) volume.SetMute(false, ref context);

            return volume.SetMasterVolumeLevelScalar(scalar, ref context) == 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool? GetMute()
    {
        try
        {
            var volume = Open();
            if (volume is null) return null;
            return volume.GetMute(out var muted) == 0 ? muted : null;
        }
        catch
        {
            return null;
        }
    }

    public static bool TrySetMute(bool mute)
    {
        try
        {
            var volume = Open();
            if (volume is null) return false;

            var context = Guid.Empty;
            return volume.SetMute(mute, ref context) == 0;
        }
        catch
        {
            return false;
        }
    }

    private static IAudioEndpointVolume? Open()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();

        // eRender / eMultimedia: the default speakers, the thing the volume key moves.
        if (enumerator.GetDefaultAudioEndpoint(0, 1, out var device) != 0 || device is null)
            return null;

        var iid = typeof(IAudioEndpointVolume).GUID;
        if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var ptr) != 0 || ptr == IntPtr.Zero)
            return null;

        return (IAudioEndpointVolume)Marshal.GetObjectForIUnknown(ptr);
    }

    private const int CLSCTX_ALL = 23;

    // ------------------------------------------------------------------- COM

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice? device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, out IntPtr instance);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    // Declaration order is the vtable order; every method must be present and in sequence,
    // even the ones we never call, or the slots shift and the wrong function runs.
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        [PreserveSig] int GetVolumeStepInfo(out uint step, out uint stepCount);
        [PreserveSig] int VolumeStepUp(ref Guid eventContext);
        [PreserveSig] int VolumeStepDown(ref Guid eventContext);
        [PreserveSig] int QueryHardwareSupport(out uint mask);
        [PreserveSig] int GetVolumeRange(out float minDb, out float maxDb, out float incrementDb);
    }
}
