using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using MerlinSip.Models;

namespace MerlinSip.Services;

public sealed class DeviceDiscoveryService
{
    private static readonly MediaDeviceInfo DefaultAudioInput = new("default-capture", "Default microphone");
    private static readonly MediaDeviceInfo DefaultAudioOutput = new("default-render", "Default speaker");
    private static readonly MediaDeviceInfo DefaultVideoSource = new("default-video", "Default camera");

    public IReadOnlyList<MediaDeviceInfo> GetAudioInputs()
    {
        var devices = new List<MediaDeviceInfo>();
        var count = WinMm.waveInGetNumDevs();

        for (var deviceId = 0; deviceId < count; deviceId++)
        {
            if (WinMm.waveInGetDevCaps((IntPtr)deviceId, out var caps, Marshal.SizeOf<WaveInCaps>()) == 0 &&
                !string.IsNullOrWhiteSpace(caps.ProductName))
            {
                devices.Add(new MediaDeviceInfo(deviceId.ToString(), caps.ProductName));
            }
        }

        return devices.Count > 0 ? devices : [DefaultAudioInput];
    }

    public IReadOnlyList<MediaDeviceInfo> GetAudioOutputs()
    {
        var devices = new List<MediaDeviceInfo>();
        var count = WinMm.waveOutGetNumDevs();

        for (var deviceId = 0; deviceId < count; deviceId++)
        {
            if (WinMm.waveOutGetDevCaps((IntPtr)deviceId, out var caps, Marshal.SizeOf<WaveOutCaps>()) == 0 &&
                !string.IsNullOrWhiteSpace(caps.ProductName))
            {
                devices.Add(new MediaDeviceInfo(deviceId.ToString(), caps.ProductName));
            }
        }

        return devices.Count > 0 ? devices : [DefaultAudioOutput];
    }

    public IReadOnlyList<MediaDeviceInfo> GetVideoSources()
    {
        var devices = new List<MediaDeviceInfo>();
        object? deviceEnum = null;
        IEnumMoniker? enumMoniker = null;

        try
        {
            var type = Type.GetTypeFromCLSID(ComGuids.SystemDeviceEnum);
            if (type is null)
            {
                return [DefaultVideoSource];
            }

            deviceEnum = Activator.CreateInstance(type);
            if (deviceEnum is not ICreateDevEnum createDevEnum)
            {
                return [DefaultVideoSource];
            }

            var result = createDevEnum.CreateClassEnumerator(ComGuids.VideoInputDeviceCategory, out enumMoniker, 0);
            if (result != 0 || enumMoniker is null)
            {
                return [DefaultVideoSource];
            }

            var monikers = new IMoniker[1];
            while (enumMoniker.Next(1, monikers, IntPtr.Zero) == 0)
            {
                var moniker = monikers[0];
                try
                {
                    var propertyBagId = typeof(IPropertyBag).GUID;
                    moniker.BindToStorage(null!, null!, ref propertyBagId, out var propertyBagObject);
                    if (propertyBagObject is IPropertyBag propertyBag)
                    {
                        propertyBag.Read("FriendlyName", out var name, IntPtr.Zero);
                        var id = GetMonikerDisplayName(moniker);
                        if (name is string friendlyName && !string.IsNullOrWhiteSpace(friendlyName))
                        {
                            devices.Add(new MediaDeviceInfo(id, friendlyName));
                        }

                        Marshal.ReleaseComObject(propertyBag);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(moniker);
                }
            }
        }
        catch
        {
            return [DefaultVideoSource];
        }
        finally
        {
            if (enumMoniker is not null)
            {
                Marshal.ReleaseComObject(enumMoniker);
            }

            if (deviceEnum is not null)
            {
                Marshal.ReleaseComObject(deviceEnum);
            }
        }

        return devices.Count > 0 ? devices : [DefaultVideoSource];
    }

    private static string GetMonikerDisplayName(IMoniker moniker)
    {
        try
        {
            moniker.GetDisplayName(null!, null!, out var name);
            return name;
        }
        catch
        {
            return Guid.NewGuid().ToString("N");
        }
    }
}

internal static class ComGuids
{
    public static readonly Guid SystemDeviceEnum = new("62BE5D10-60EB-11D0-BD3B-00A0C911CE86");
    public static readonly Guid VideoInputDeviceCategory = new("860BB310-5D01-11D0-BD3B-00A0C911CE86");
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
internal struct WaveInCaps
{
    public ushort ManufacturerId;
    public ushort ProductId;
    public uint DriverVersion;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string ProductName;
    public uint Formats;
    public ushort Channels;
    public ushort Reserved;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
internal struct WaveOutCaps
{
    public ushort ManufacturerId;
    public ushort ProductId;
    public uint DriverVersion;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string ProductName;
    public uint Formats;
    public ushort Channels;
    public ushort Reserved;
    public uint Support;
}

internal static partial class WinMm
{
    [DllImport("winmm.dll")]
    public static extern int waveInGetNumDevs();

    [DllImport("winmm.dll", CharSet = CharSet.Auto)]
    public static extern int waveInGetDevCaps(IntPtr deviceId, out WaveInCaps caps, int size);

    [DllImport("winmm.dll")]
    public static extern int waveOutGetNumDevs();

    [DllImport("winmm.dll", CharSet = CharSet.Auto)]
    public static extern int waveOutGetDevCaps(IntPtr deviceId, out WaveOutCaps caps, int size);
}

[ComImport]
[Guid("29840822-5B84-11D0-BD3B-00A0C911CE86")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICreateDevEnum
{
    [PreserveSig]
    int CreateClassEnumerator(Guid classType, out IEnumMoniker? enumMoniker, int flags);
}

[ComImport]
[Guid("00000102-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEnumMoniker
{
    [PreserveSig]
    int Next(int count, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IMoniker[] monikers, IntPtr fetched);
    void Skip(int count);
    void Reset();
    void Clone(out IEnumMoniker enumMoniker);
}

[ComImport]
[Guid("55272A00-42CB-11CE-8135-00AA004BB851")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyBag
{
    void Read([MarshalAs(UnmanagedType.LPWStr)] string propertyName, [MarshalAs(UnmanagedType.Struct)] out object value, IntPtr errorLog);
    void Write(string propertyName, ref object value);
}
