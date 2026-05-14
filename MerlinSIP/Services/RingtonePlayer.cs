using System.Runtime.InteropServices;
using MerlinSip.Models;

namespace MerlinSip.Services;

public sealed class RingtonePlayer : IDisposable
{
    private const int SampleRate = 8000;
    private readonly List<AudioBuffer> _buffers = [];
    private readonly object _sync = new();
    private CancellationTokenSource? _cancellation;
    private IntPtr _waveOut;

    public void Start(MediaDeviceInfo outputDevice)
    {
        Stop();

        var format = WaveFormat.Pcm16Mono8k();
        var deviceId = int.TryParse(outputDevice.Id, out var parsed) ? parsed : -1;
        var result = WinMm.waveOutOpen(out _waveOut, deviceId, ref format, IntPtr.Zero, IntPtr.Zero, 0);
        if (result != 0)
        {
            DebugLog.Write($"RINGTONE open failed device={outputDevice.Name} result={result}");
            return;
        }

        DebugLog.Write($"RINGTONE start device={outputDevice.Name}");
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
            QueueTone(659.25, 0.16, 0.26);
            QueueTone(783.99, 0.16, 0.22);
            QueueTone(987.77, 0.18, 0.18);
            QueueTone(783.99, 0.14, 0.20);

            try
            {
                await Task.Delay(1700, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            TrimBuffers();
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
