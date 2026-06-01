using System.Runtime.InteropServices;
using MerlinSip.Models;

namespace MerlinSip.Services;

public sealed class RingtonePlayer : IDisposable
{
    private const int SampleRate = 8000;
    public static readonly IReadOnlyList<RingtoneChoice> Choices =
    [
        new("merlin", "Merlin Signature"),
        new("office", "Modern Office"),
        new("teams", "Conference Chime"),
        new("skype", "Soft Digital Ring"),
        new("desk", "Analogue Desk Phone"),
        new("double", "Classic Double Ring"),
        new("reception", "Reception Bell"),
        new("marimba", "Warm Marimba"),
        new("piano", "Piano Cascade"),
        new("night", "Night Shift"),
        new("urgent", "Urgent Pulse")
    ];

    private readonly List<AudioBuffer> _buffers = [];
    private readonly object _sync = new();
    private CancellationTokenSource? _cancellation;
    private IntPtr _waveOut;
    private string _ringtone = AppStartupConfig.DefaultRingtone;
    private double _volume = 1.0;

    public void Start(MediaDeviceInfo outputDevice, string? ringtone = null, double volume = 1.0)
    {
        Stop();
        _ringtone = Choices.Any(choice => choice.Id == ringtone)
            ? ringtone!
            : AppStartupConfig.DefaultRingtone;
        _volume = Math.Clamp(volume, 0.25, 2.0);

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
            case "office":
                Repeat(4, () =>
                {
                    QueueChord([659.25, 987.77], 0.34, 0.12);
                    QueuePause(0.08);
                    QueueTone(783.99, 0.24, 0.14);
                    QueuePause(0.10);
                    QueueChord([587.33, 880.00], 0.40, 0.11);
                    QueuePause(1.55);
                });
                return 10500;
            case "teams":
                Repeat(4, () =>
                {
                    QueueChord([622.25, 830.61], 0.28, 0.12);
                    QueuePause(0.08);
                    QueueTone(739.99, 0.24, 0.13);
                    QueuePause(0.08);
                    QueueChord([554.37, 739.99], 0.32, 0.11);
                    QueuePause(1.70);
                });
                return 10500;
            case "skype":
                Repeat(5, () =>
                {
                    QueueTone(783.99, 0.22, 0.12);
                    QueueTone(987.77, 0.18, 0.11);
                    QueueTone(739.99, 0.28, 0.11);
                    QueuePause(1.25);
                });
                return 10500;
            case "desk":
                Repeat(3, () =>
                {
                    QueueTone(440.00, 0.75, 0.16);
                    QueueTone(480.00, 0.75, 0.15);
                    QueuePause(1.80);
                });
                return 10500;
            case "double":
                Repeat(4, () =>
                {
                    QueueTone(425.00, 0.38, 0.17);
                    QueuePause(0.18);
                    QueueTone(425.00, 0.38, 0.17);
                    QueuePause(1.55);
                });
                return 10500;
            case "reception":
                Repeat(5, () =>
                {
                    QueueChord([1046.50, 1318.51], 0.24, 0.10);
                    QueuePause(0.08);
                    QueueChord([880.00, 1174.66], 0.30, 0.11);
                    QueuePause(1.35);
                });
                return 10500;
            case "marimba":
                Repeat(5, () =>
                {
                    QueueTone(523.25, 0.18, 0.12);
                    QueueTone(659.25, 0.18, 0.12);
                    QueueTone(783.99, 0.24, 0.10);
                    QueueTone(659.25, 0.22, 0.10);
                    QueuePause(1.15);
                });
                return 10500;
            case "piano":
                Repeat(4, () =>
                {
                    QueueChord([392.00, 493.88, 659.25], 0.45, 0.08);
                    QueuePause(0.10);
                    QueueChord([440.00, 554.37, 739.99], 0.45, 0.08);
                    QueuePause(1.55);
                });
                return 10500;
            case "night":
                Repeat(3, () =>
                {
                    QueueTone(349.23, 0.55, 0.08);
                    QueuePause(0.18);
                    QueueChord([440.00, 523.25], 0.65, 0.07);
                    QueuePause(1.95);
                });
                return 10500;
            case "urgent":
                Repeat(8, () =>
                {
                    QueueTone(1046.50, 0.14, 0.14);
                    QueuePause(0.06);
                    QueueTone(880.00, 0.14, 0.14);
                    QueuePause(0.82);
                });
                return 10500;
            default:
                Repeat(5, () =>
                {
                    QueueTone(659.25, 0.20, 0.13);
                    QueueTone(783.99, 0.20, 0.12);
                    QueueTone(987.77, 0.24, 0.10);
                    QueueTone(783.99, 0.22, 0.11);
                    QueuePause(1.10);
                });
                return 10500;
        }
    }

    private static void Repeat(int count, Action phrase)
    {
        for (var index = 0; index < count; index++)
        {
            phrase();
        }
    }

    private void QueueChord(double[] frequencies, double seconds, double gain)
    {
        QueueWave(seconds, gain, index =>
        {
            var value = 0.0;
            foreach (var frequency in frequencies)
            {
                value += Math.Sin(2 * Math.PI * frequency * index / SampleRate);
            }

            return value / frequencies.Length;
        });
    }

    private void QueueTone(double frequency, double seconds, double gain)
    {
        QueueWave(seconds, gain, index =>
        {
            var shimmer = Math.Sin(2 * Math.PI * (frequency * 2) * index / SampleRate) * 0.12;
            return Math.Sin(2 * Math.PI * frequency * index / SampleRate) + shimmer;
        });
    }

    private void QueuePause(double seconds)
    {
        QueueWave(seconds, 0, _ => 0);
    }

    private void QueueWave(double seconds, double gain, Func<int, double> sampleFactory)
    {
        var sampleCount = (int)(SampleRate * seconds);
        var pcm = new byte[sampleCount * 2];
        for (var i = 0; i < sampleCount; i++)
        {
            var envelope = Math.Min(1.0, Math.Min(i / 120.0, (sampleCount - i) / 120.0));
            var value = sampleFactory(i);
            var sample = (short)Math.Clamp(value * envelope * gain * _volume * short.MaxValue, short.MinValue, short.MaxValue);
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
            while (_buffers.Count > 72)
            {
                var old = _buffers[0];
                _buffers.RemoveAt(0);
                old.Dispose();
            }
        }
    }
}

public sealed record RingtoneChoice(string Id, string Name)
{
    public override string ToString() => Name;
}
