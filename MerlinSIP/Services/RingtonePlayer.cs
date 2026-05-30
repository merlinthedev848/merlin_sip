using System.Runtime.InteropServices;
using MerlinSip.Models;

namespace MerlinSip.Services;

public sealed class RingtonePlayer : IDisposable
{
    private const int SampleRate = 8000;
    public static readonly IReadOnlyList<RingtoneChoice> Choices =
    [
        new("classic", "Classic"),
        new("bright", "Bright"),
        new("pulse", "Pulse"),
        new("soft", "Soft"),
        new("urgent", "Urgent")
    ];

    private readonly List<AudioBuffer> _buffers = [];
    private readonly object _sync = new();
    private CancellationTokenSource? _cancellation;
    private IntPtr _waveOut;
    private string _ringtone = AppStartupConfig.DefaultRingtone;

    public void Start(MediaDeviceInfo outputDevice, string? ringtone = null)
    {
        Stop();
        _ringtone = Choices.Any(choice => choice.Id == ringtone)
            ? ringtone!
            : AppStartupConfig.DefaultRingtone;

        var format = WaveFormat.Pcm16Mono8k();
        var deviceId = int.TryParse(outputDevice.Id, out var parsed) ? parsed : -1;
        var result = WinMm.waveOutOpen(out _waveOut, deviceId, ref format, IntPtr.Zero, IntPtr.Zero, 0);
        if (result != 0)
        {
            DebugLog.Write($"RINGTONE open failed device={outputDevice.Name} result={result}");
            return;
        }

        DebugLog.Write($"RINGTONE start device={outputDevice.Name} tone={_ringtone}");
        _cancellation = new CancellationTokenSource();
        _ = Task.Run(() => PlayLoopAsync(_cancellation.Token));
    }

    public void Stop()
    {
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;

        if (_waveOut != IntPtr.Zero)
        {
            WinMm.waveOutReset(_waveOut);
            WinMm.waveOutClose(_waveOut);
            _waveOut = IntPtr.Zero;
            DebugLog.Write("RINGTONE stop");
        }

        lock (_sync)
        {
            foreach (var buffer in _buffers)
            {
                buffer.Dispose();
            }

            _buffers.Clear();
        }
    }

    public void Dispose()
    {
        Stop();
    }

    private async Task PlayLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _waveOut != IntPtr.Zero)
        {
            var pause = PlayPattern();

            try
            {
                await Task.Delay(pause, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            TrimBuffers();
        }
    }

    private int PlayPattern()
    {
        switch (_ringtone)
        {
            case "bright":
                QueueTone(880.00, 0.12, 0.24);
                QueueTone(1174.66, 0.12, 0.20);
                QueueTone(1318.51, 0.16, 0.18);
                return 1150;
            case "pulse":
                QueueTone(523.25, 0.22, 0.24);
                QueueTone(523.25, 0.22, 0.24);
                return 900;
            case "soft":
                QueueTone(392.00, 0.28, 0.14);
                QueueTone(493.88, 0.28, 0.12);
                QueueTone(587.33, 0.32, 0.10);
                return 1900;
            case "urgent":
                QueueTone(1046.50, 0.10, 0.22);
                QueueTone(880.00, 0.10, 0.22);
                QueueTone(1046.50, 0.10, 0.22);
                QueueTone(880.00, 0.10, 0.22);
                return 650;
            default:
                QueueTone(659.25, 0.16, 0.26);
                QueueTone(783.99, 0.16, 0.22);
                QueueTone(987.77, 0.18, 0.18);
                QueueTone(783.99, 0.14, 0.20);
                return 1700;
        }
    }

    private void QueueTone(double frequency, double seconds, double gain)
    {
        var sampleCount = (int)(SampleRate * seconds);
        var pcm = new byte[sampleCount * 2];
        for (var i = 0; i < sampleCount; i++)
        {
            var envelope = Math.Min(1.0, Math.Min(i / 120.0, (sampleCount - i) / 120.0));
            var shimmer = Math.Sin(2 * Math.PI * (frequency * 2) * i / SampleRate) * 0.18;
            var value = Math.Sin(2 * Math.PI * frequency * i / SampleRate) + shimmer;
            var sample = (short)(value * envelope * gain * short.MaxValue);
            pcm[i * 2] = (byte)(sample & 0xFF);
            pcm[i * 2 + 1] = (byte)(sample >> 8);
        }

        var buffer = new AudioBuffer(pcm.Length);
        Marshal.Copy(pcm, 0, buffer.DataPointer, pcm.Length);
        lock (_sync)
        {
            _buffers.Add(buffer);
        }

        WinMm.waveOutPrepareHeader(_waveOut, buffer.HeaderPointer, Marshal.SizeOf<WaveHeader>());
        WinMm.waveOutWrite(_waveOut, buffer.HeaderPointer, Marshal.SizeOf<WaveHeader>());
    }

    private void TrimBuffers()
    {
        lock (_sync)
        {
            while (_buffers.Count > 12)
            {
                var old = _buffers[0];
                _buffers.RemoveAt(0);
                old.Dispose();
            }
        }
    }
}

public sealed record RingtoneChoice(string Id, string Name);
