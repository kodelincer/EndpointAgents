using EndpointAgents.Shared.Actions;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace EndpointAgents.Platform.Windows.Actions.UserSide;

/// <summary>The user's sound devices through the Windows Core Audio API (no programs run).</summary>
[SupportedOSPlatform("windows")]
internal static class SoundDevices
{
    public sealed record Device(string Id, string Name, bool IsDefault, bool Muted,
        int VolumePercent);

    public const int Playback = 0;  // eRender
    public const int Recording = 1; // eCapture
    private const int RoleCommunications = 2;
    private const int DeviceStateActive = 1;

    public static IReadOnlyList<Device> Read(int flow)
    {
        var enumerator = Enumerator();
        try
        {
            var defaultId = DefaultId(enumerator, flow);
            Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(flow, DeviceStateActive,
                out var collection));
            Marshal.ThrowExceptionForHR(collection.GetCount(out var count));
            var devices = new List<Device>();
            for (uint index = 0; index < count && index < 20; index++)
            {
                if (collection.Item(index, out var device) != 0) continue;
                devices.Add(Describe(device, defaultId));
                Marshal.ReleaseComObject(device);
            }
            Marshal.ReleaseComObject(collection);
            return devices;
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    /// <summary>Unmutes the default device and raises a volume under 10% to 50%; returns what changed.</summary>
    public static string? Unmute(int flow)
    {
        var enumerator = Enumerator();
        try
        {
            if (enumerator.GetDefaultAudioEndpoint(flow, 0, out var device) != 0) return null;
            try
            {
                var volume = Volume(device);
                var changes = new List<string>();
                var context = Guid.Empty;
                if (volume.GetMute(out var muted) == 0 && muted)
                {
                    Marshal.ThrowExceptionForHR(volume.SetMute(false, ref context));
                    changes.Add("unmuted");
                }
                if (volume.GetMasterVolumeLevelScalar(out var level) == 0 && level < 0.10f)
                {
                    Marshal.ThrowExceptionForHR(volume.SetMasterVolumeLevelScalar(0.5f, ref context));
                    changes.Add("volume raised to 50%");
                }
                Marshal.ReleaseComObject(volume);
                return changes.Count == 0 ? null : string.Join(" and ", changes);
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    /// <summary>Makes a device the default for every role (console, multimedia, communications).</summary>
    public static void SetDefault(string id)
    {
        var policy = (IPolicyConfig)new PolicyConfigClass();
        try
        {
            for (var role = 0; role <= RoleCommunications; role++)
                Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(id, role));
        }
        finally
        {
            Marshal.ReleaseComObject(policy);
        }
    }

    public static (Device? Device, int Flow) Find(string id)
    {
        foreach (var flow in new[] { Playback, Recording })
            if (Read(flow).FirstOrDefault(item => item.Id == id) is { } device)
                return (device, flow);
        return (null, Playback);
    }

    private static IMMDeviceEnumerator Enumerator() =>
        (IMMDeviceEnumerator)new MMDeviceEnumeratorClass();

    private static string? DefaultId(IMMDeviceEnumerator enumerator, int flow)
    {
        if (enumerator.GetDefaultAudioEndpoint(flow, 0, out var device) != 0) return null;
        try { return device.GetId(out var id) == 0 ? id : null; }
        finally { Marshal.ReleaseComObject(device); }
    }

    private static Device Describe(IMMDevice device, string? defaultId)
    {
        device.GetId(out var id);
        var name = id;
        if (device.OpenPropertyStore(0, out var store) == 0)
        {
            var key = FriendlyName;
            if (store.GetValue(ref key, out var value) == 0)
            {
                if (value.Type == 31 && value.Pointer != IntPtr.Zero)
                    name = Marshal.PtrToStringUni(value.Pointer) ?? id;
                PropVariantClear(ref value);
            }
            Marshal.ReleaseComObject(store);
        }
        var muted = false;
        var percent = 0;
        try
        {
            var volume = Volume(device);
            volume.GetMute(out muted);
            if (volume.GetMasterVolumeLevelScalar(out var level) == 0)
                percent = (int)Math.Round(level * 100);
            Marshal.ReleaseComObject(volume);
        }
        catch (COMException) { }
        return new Device(id, name, id == defaultId, muted, percent);
    }

    private static IAudioEndpointVolume Volume(IMMDevice device)
    {
        var iid = typeof(IAudioEndpointVolume).GUID;
        Marshal.ThrowExceptionForHR(device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero,
            out var instance));
        return (IAudioEndpointVolume)instance;
    }

    private static readonly PropertyKey FriendlyName =
        new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid format, int id)
    {
        public Guid Format = format;
        public int Id = id;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorClass;

    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    private class PolicyConfigClass;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int context, IntPtr parameters,
            [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float level, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float level);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    /// <summary>Windows' own default-device interface (used by the Sound settings page since Windows 7).</summary>
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(string id, IntPtr format);
        [PreserveSig] int GetDeviceFormat(string id, int defaultFormat, IntPtr format);
        [PreserveSig] int ResetDeviceFormat(string id);
        [PreserveSig] int SetDeviceFormat(string id, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod(string id, int defaultPeriod, IntPtr defaultValue, IntPtr minimum);
        [PreserveSig] int SetProcessingPeriod(string id, IntPtr period);
        [PreserveSig] int GetShareMode(string id, IntPtr mode);
        [PreserveSig] int SetShareMode(string id, IntPtr mode);
        [PreserveSig] int GetPropertyValue(string id, int store, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue(string id, int store, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        [PreserveSig] int SetEndpointVisibility(string id, int visible);
    }
}

[SupportedOSPlatform("windows")]
public sealed class AudioStatusAction : IEndpointAction
{
    public string Id => "audio.status";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var playback = SoundDevices.Read(SoundDevices.Playback);
        var recording = SoundDevices.Read(SoundDevices.Recording);
        var speaker = playback.FirstOrDefault(item => item.IsDefault);
        var microphone = recording.FirstOrDefault(item => item.IsDefault);
        string Describe(SoundDevices.Device? device, string kind) => device is null
            ? $"No {kind} is connected."
            : $"{kind[..1].ToUpperInvariant()}{kind[1..]}: {device.Name}{(device.Muted ? " (muted)" : device.VolumePercent < 10 ? $" (volume {device.VolumePercent}%)" : "")}.";
        return Task.FromResult(EndpointActionResult.Ok(
            $"{Describe(speaker, "speaker")} {Describe(microphone, "microphone")}",
            new { Playback = playback, Recording = recording }));
    }
}

[SupportedOSPlatform("windows")]
public sealed class AudioUnmuteAction : IEndpointAction
{
    public string Id => "audio.unmute";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var side = invocation.Argument("device") ?? "both";
        var changes = new List<string>();
        if (side is "both" or "playback" && SoundDevices.Unmute(SoundDevices.Playback) is { } speaker)
            changes.Add($"speaker {speaker}");
        if (side is "both" or "recording" && SoundDevices.Unmute(SoundDevices.Recording) is { } microphone)
            changes.Add($"microphone {microphone}");
        return Task.FromResult(changes.Count == 0
            ? EndpointActionResult.NotNeeded("Sound was not muted or turned down.")
            : EndpointActionResult.Done($"Changed: {string.Join("; ", changes)}."));
    }
}

[SupportedOSPlatform("windows")]
public sealed class AudioSetDefaultAction : IEndpointAction
{
    public string Id => "audio.set_default";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken) =>
        Task.FromResult(SetDefault(invocation.Argument("device")!, saveUndo: true));

    internal static EndpointActionResult SetDefault(string id, bool saveUndo)
    {
        var (device, flow) = SoundDevices.Find(id);
        if (device is null)
            return EndpointActionResult.Failed("That sound device is not connected. Use audio.status.");
        if (device.IsDefault)
            return EndpointActionResult.NotNeeded($"{device.Name} is already the default.");
        var previous = SoundDevices.Read(flow).FirstOrDefault(item => item.IsDefault);
        SoundDevices.SetDefault(device.Id);
        return EndpointActionResult.Done($"{device.Name} is now the default.", null,
            saveUndo && previous is not null
            && previous.Id.Length <= EndpointActionValidator.MaxLocalItemLength
                ? new Dictionary<string, string> { ["device"] = previous.Id } : null);
    }
}

[SupportedOSPlatform("windows")]
public sealed class AudioRestoreDefaultAction : IEndpointAction
{
    public string Id => "audio.restore_default";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken) =>
        Task.FromResult(AudioSetDefaultAction.SetDefault(invocation.Argument("device")!, saveUndo: false));
}
