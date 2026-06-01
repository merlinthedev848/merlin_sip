using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using MerlinSip.Models;

namespace MerlinSip.Services;

public sealed class RtpAudioSession : IDisposable
{
    private const int SampleRate = 8000;
    private const int SamplesPerPacket = 160;
    private readonly MediaDeviceInfo _inputDevice;
    private readonly MediaDeviceInfo _outputDevice;
    private readonly double _inputGain;
    private readonly double _outputGain;
    private readonly UdpClient _rtpClient;
    private readonly List<AudioBuffer> _inputBuffers = [];
    private readonly List<AudioBuffer> _outputBuffers = [];
    private readonly WinMm.WaveInProc _waveInCallback;
    private IntPtr _waveIn;
    private IntPtr _waveOut;
    private IPEndPoint? _remoteEndPoint;
    private CancellationTokenSource? _receiveCancellation;
    private ushort _sequence;
    private uint _timestamp;
    private readonly uint _ssrc = (uint)Random.Shared.Next();
    private int _payloadType;
    private bool _running;
    private bool _devicesPrepared;
    private bool _transmitMicrophone;
    private bool _muted;
    private bool _held;
    private int _receivedPackets;
    private int _sentPackets;

    public int LocalPort { get; }

    public int ReceivedPackets => Volatile.Read(ref _receivedPackets);

    public int SentPackets => Volatile.Read(ref _sentPackets);

    public RtpAudioSession(MediaDeviceInfo inputDevice, MediaDeviceInfo outputDevice, double inputGain = 1.0, double outputGain = 1.0)
    {
        _inputDevice = inputDevice;
        _outputDevice = outputDevice;
        _inputGain = Math.Clamp(inputGain, 0.25, 2.0);
        _outputGain = Math.Clamp(outputGain, 0.25, 2.0);
        _waveInCallback = WaveInCallback;
        _rtpClient = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        LocalPort = ((IPEndPoint)_rtpClient.Client.LocalEndPoint!).Port;
    }

    public void PrepareDevices()
    {
        if (_devicesPrepared)
        {
            return;
        }

        OpenWaveOut();
        OpenWaveIn();
        _devicesPrepared = true;
        DebugLog.Write($"RTP devices prepared input={_inputDevice.Name} output={_outputDevice.Name} localPort={LocalPort}");
    }

    public async Task StartAsync(string remoteAddress, int remotePort, int payloadType, bool transmitMicrophone = true)
    {
        if (_running)
        {
            var updatedAddress = await ResolveRemoteAddressAsync(remoteAddress);
            _remoteEndPoint = new IPEndPoint(updatedAddress, remotePort);
            _payloadType = payloadType;
            _transmitMicrophone = transmitMicrophone;
            if (transmitMicrophone)
            {
                var startResult = WinMm.waveInStart(_waveIn);
                DebugLog.Write($"RTP microphone resume result={startResult}");
            }

            DebugLog.Write($"RTP remote updated while running remote={_remoteEndPoint} payload={payloadType} transmit={transmitMicrophone}");
            return;
        }

        var address = await ResolveRemoteAddressAsync(remoteAddress);
        _remoteEndPoint = new IPEndPoint(address, remotePort);
        _payloadType = payloadType;
        _transmitMicrophone = transmitMicrophone;
        DebugLog.Write($"RTP start remote={_remoteEndPoint} payload={payloadType} transmit={transmitMicrophone} input={_inputDevice.Name} output={_outputDevice.Name}");
        PrepareDevices();
        _running = true;
        _receiveCancellation = new CancellationTokenSource();
        _ = Task.Run(() => ReceiveLoopAsync(_receiveCancellation.Token));
        if (transmitMicrophone)
        {
            var startResult = WinMm.waveInStart(_waveIn);
            DebugLog.Write($"RTP microphone start result={startResult}");
        }
    }

    public void Stop()
    {
        _running = false;
        _receiveCancellation?.Cancel();
        DebugLog.Write("RTP stop");
        if (_waveIn != IntPtr.Zero)
        {
            WinMm.waveInStop(_waveIn);
            WinMm.waveInReset(_waveIn);
        }

        if (_waveOut != IntPtr.Zero)
        {
            WinMm.waveOutReset(_waveOut);
        }

        _remoteEndPoint = null;
    }

    public void SetMuted(bool muted)
    {
        _muted = muted;
        DebugLog.Write($"RTP muted={muted}");
    }

    public void SetHeld(bool held)
    {
        _held = held;
        DebugLog.Write($"RTP held={held}");
    }

    public void Dispose()
    {
        Stop();
        foreach (var buffer in _inputBuffers)
        {
            buffer.Dispose();
        }

        foreach (var buffer in _outputBuffers)
        {
            buffer.Dispose();
        }

        if (_waveIn != IntPtr.Zero)
        {
            WinMm.waveInClose(_waveIn);
            _waveIn = IntPtr.Zero;
        }

        if (_waveOut != IntPtr.Zero)
        {
            WinMm.waveOutClose(_waveOut);
            _waveOut = IntPtr.Zero;
        }

        _devicesPrepared = false;
        _receiveCancellation?.Dispose();
        _rtpClient.Dispose();
    }

    private void OpenWaveIn()
    {
        var format = WaveFormat.Pcm16Mono8k();
        var deviceId = ParseDeviceId(_inputDevice.Id);
        var result = WinMm.waveInOpen(out _waveIn, deviceId, ref format, _waveInCallback, IntPtr.Zero, WinMm.CallbackFunction);
        if (result != 0)
        {
            DebugLog.Write($"RTP microphone open failed device={_inputDevice.Name} result={result}");
            throw new InvalidOperationException($"Unable to open microphone device: {result}");
        }
        DebugLog.Write($"RTP microphone open device={_inputDevice.Name} id={deviceId}");

        for (var i = 0; i < 6; i++)
        {
            var buffer = new AudioBuffer(SamplesPerPacket * 2);
            _inputBuffers.Add(buffer);
            WinMm.waveInPrepareHeader(_waveIn, buffer.HeaderPointer, Marshal.SizeOf<WaveHeader>());
            WinMm.waveInAddBuffer(_waveIn, buffer.HeaderPointer, Marshal.SizeOf<WaveHeader>());
        }
    }

    private void OpenWaveOut()
    {
        var format = WaveFormat.Pcm16Mono8k();
        var deviceId = ParseDeviceId(_outputDevice.Id);
        var result = WinMm.waveOutOpen(out _waveOut, deviceId, ref format, IntPtr.Zero, IntPtr.Zero, 0);
        if (result != 0)
        {
            DebugLog.Write($"RTP speaker open failed device={_outputDevice.Name} result={result}");
            throw new InvalidOperationException($"Unable to open speaker device: {result}");
        }
        DebugLog.Write($"RTP speaker open device={_outputDevice.Name} id={deviceId}");
    }

    private void WaveInCallback(IntPtr waveIn, uint message, IntPtr instance, IntPtr headerPointer, IntPtr reserved)
    {
        if (message != WinMm.WimData || !_running || _remoteEndPoint is null)
        {
            return;
        }

        var header = Marshal.PtrToStructure<WaveHeader>(headerPointer);
        if (header.BytesRecorded <= 0)
        {
            WinMm.waveInAddBuffer(_waveIn, headerPointer, Marshal.SizeOf<WaveHeader>());
            return;
        }

        if (!_transmitMicrophone || _held)
        {
            WinMm.waveInAddBuffer(_waveIn, headerPointer, Marshal.SizeOf<WaveHeader>());
            return;
        }

        if (_muted)
        {
            WinMm.waveInAddBuffer(_waveIn, headerPointer, Marshal.SizeOf<WaveHeader>());
            return;
        }

        var pcm = new byte[header.BytesRecorded];
        Marshal.Copy(header.Data, pcm, 0, pcm.Length);
        var payload = new byte[pcm.Length / 2];
        for (var i = 0; i < payload.Length; i++)
        {
            var sample = BitConverter.ToInt16(pcm, i * 2);
            sample = ApplyGain(sample, _inputGain);
            payload[i] = _payloadType == 8 ? G711Codec.LinearToALaw(sample) : G711Codec.LinearToMuLaw(sample);
        }

        var packet = BuildRtpPacket(payload);
        _rtpClient.Send(packet, packet.Length, _remoteEndPoint);
        var sent = Interlocked.Increment(ref _sentPackets);
        if (sent is 1 or 50 or 250)
        {
            DebugLog.Write($"RTP sent packets={sent} bytes={packet.Length} remote={_remoteEndPoint}");
        }
        _timestamp += (uint)payload.Length;
        WinMm.waveInAddBuffer(_waveIn, headerPointer, Marshal.SizeOf<WaveHeader>());
    }

    private byte[] BuildRtpPacket(byte[] payload)
    {
        var packet = new byte[12 + payload.Length];
        packet[0] = 0x80;
        packet[1] = (byte)_payloadType;
        WriteUInt16(packet, 2, _sequence++);
        WriteUInt32(packet, 4, _timestamp);
        WriteUInt32(packet, 8, _ssrc);
        Buffer.BlockCopy(payload, 0, packet, 12, payload.Length);
        return packet;
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var result = await _rtpClient.ReceiveAsync(cancellationToken);
                if (result.Buffer.Length <= 12)
                {
                    continue;
                }

                var payloadType = result.Buffer[1] & 0x7F;
                if (payloadType is not (0 or 8))
                {
                    continue;
                }

                var headerLength = 12 + ((result.Buffer[0] & 0x0F) * 4);
                if (result.Buffer.Length <= headerLength)
                {
                    continue;
                }

                var payload = result.Buffer[headerLength..];
                var pcm = new byte[payload.Length * 2];
                for (var i = 0; i < payload.Length; i++)
                {
                    var sample = payloadType == 8 ? G711Codec.ALawToLinear(payload[i]) : G711Codec.MuLawToLinear(payload[i]);
                    sample = ApplyGain(sample, _outputGain);
                    var bytes = BitConverter.GetBytes(sample);
                    pcm[i * 2] = bytes[0];
                    pcm[i * 2 + 1] = bytes[1];
                }

                if (!_held)
                {
                    PlayPcm(pcm);
                }
                var received = Interlocked.Increment(ref _receivedPackets);
                if (received is 1 or 50 or 250)
                {
                    DebugLog.Write($"RTP received packets={received} bytes={result.Buffer.Length} from={result.RemoteEndPoint}");
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception error)
            {
                DebugLog.Write($"RTP receive/playback error={error.Message}");
            }
        }
    }

    private static async Task<IPAddress> ResolveRemoteAddressAsync(string remoteAddress)
    {
        if (IPAddress.TryParse(remoteAddress, out var parsed))
        {
            return parsed;
        }

        var addresses = await Dns.GetHostAddressesAsync(remoteAddress);
        return addresses.FirstOrDefault(item => item.AddressFamily == AddressFamily.InterNetwork)
            ?? addresses.FirstOrDefault()
            ?? throw new InvalidOperationException($"Unable to resolve RTP address {remoteAddress}.");
    }

    private void PlayPcm(byte[] pcm)
    {
        if (_waveOut == IntPtr.Zero)
        {
            return;
        }

        var buffer = new AudioBuffer(pcm.Length);
        Marshal.Copy(pcm, 0, buffer.DataPointer, pcm.Length);
        _outputBuffers.Add(buffer);
        WinMm.waveOutPrepareHeader(_waveOut, buffer.HeaderPointer, Marshal.SizeOf<WaveHeader>());
        WinMm.waveOutWrite(_waveOut, buffer.HeaderPointer, Marshal.SizeOf<WaveHeader>());

        if (_outputBuffers.Count > 120)
        {
            var old = _outputBuffers[0];
            _outputBuffers.RemoveAt(0);
            old.Dispose();
        }
    }

    private static int ParseDeviceId(string id)
    {
        return int.TryParse(id, out var value) ? value : -1;
    }

    private static short ApplyGain(short sample, double gain)
    {
        return (short)Math.Clamp(sample * gain, short.MinValue, short.MaxValue);
    }

    private static void WriteUInt16(byte[] target, int offset, ushort value)
    {
        target[offset] = (byte)(value >> 8);
        target[offset + 1] = (byte)value;
    }

    private static void WriteUInt32(byte[] target, int offset, uint value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }
}

internal sealed class AudioBuffer : IDisposable
{
    public IntPtr DataPointer { get; }
    public IntPtr HeaderPointer { get; }

    public AudioBuffer(int bytes)
    {
        DataPointer = Marshal.AllocHGlobal(bytes);
        var header = new WaveHeader
        {
            Data = DataPointer,
            BufferLength = bytes
        };
        HeaderPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHeader>());
        Marshal.StructureToPtr(header, HeaderPointer, false);
    }

    public void Dispose()
    {
        if (HeaderPointer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(HeaderPointer);
        }

        if (DataPointer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(DataPointer);
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct WaveFormat
{
    public ushort FormatTag;
    public ushort Channels;
    public uint SamplesPerSec;
    public uint AvgBytesPerSec;
    public ushort BlockAlign;
    public ushort BitsPerSample;
    public ushort Size;

    public static WaveFormat Pcm16Mono8k()
    {
        return new WaveFormat
        {
            FormatTag = 1,
            Channels = 1,
            SamplesPerSec = 8000,
            AvgBytesPerSec = 16000,
            BlockAlign = 2,
            BitsPerSample = 16,
            Size = 0
        };
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct WaveHeader
{
    public IntPtr Data;
    public int BufferLength;
    public int BytesRecorded;
    public IntPtr User;
    public int Flags;
    public int Loops;
    public IntPtr Next;
    public IntPtr Reserved;
}

internal static partial class WinMm
{
    public const uint WimData = 0x3C0;
    public const uint CallbackFunction = 0x00030000;

    public delegate void WaveInProc(IntPtr waveIn, uint message, IntPtr instance, IntPtr header, IntPtr reserved);

    [DllImport("winmm.dll")]
    public static extern int waveInOpen(out IntPtr waveIn, int deviceId, ref WaveFormat format, WaveInProc callback, IntPtr instance, uint flags);

    [DllImport("winmm.dll")]
    public static extern int waveInPrepareHeader(IntPtr waveIn, IntPtr header, int size);

    [DllImport("winmm.dll")]
    public static extern int waveInAddBuffer(IntPtr waveIn, IntPtr header, int size);

    [DllImport("winmm.dll")]
    public static extern int waveInStart(IntPtr waveIn);

    [DllImport("winmm.dll")]
    public static extern int waveInStop(IntPtr waveIn);

    [DllImport("winmm.dll")]
    public static extern int waveInReset(IntPtr waveIn);

    [DllImport("winmm.dll")]
    public static extern int waveInClose(IntPtr waveIn);

    [DllImport("winmm.dll")]
    public static extern int waveOutOpen(out IntPtr waveOut, int deviceId, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);

    [DllImport("winmm.dll")]
    public static extern int waveOutPrepareHeader(IntPtr waveOut, IntPtr header, int size);

    [DllImport("winmm.dll")]
    public static extern int waveOutWrite(IntPtr waveOut, IntPtr header, int size);

    [DllImport("winmm.dll")]
    public static extern int waveOutReset(IntPtr waveOut);

    [DllImport("winmm.dll")]
    public static extern int waveOutClose(IntPtr waveOut);
}
