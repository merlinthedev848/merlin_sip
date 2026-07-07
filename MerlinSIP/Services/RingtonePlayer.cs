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

    public void StartUkRingback(MediaDeviceInfo outputDevice, double volume = 1.0)
    {
        Start(outputDevice, "uk-ringback", volume, allowHiddenTone: true);
    }

    public void Start(MediaDeviceInfo outputDevice, string? ringtone = null, double volume = 1.0)
    {
        Start(outputDevice, ringtone, volume, allowHiddenTone: false);
    }

    private void Start(MediaDeviceInfo outputDevice, string? ringtone, double volume, bool allowHiddenTone)
    {
        Stop();
        _ringtone = Choices.Any(choice => choice.Id == ringtone) || allowHiddenTone && ringtone == "uk-ringback"
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

        lock (_sync)
        {
            if (_waveOut != IntPtr.Zero)
            {
                WinMm.waveOutReset(_waveOut);

                foreach (var buffer in _buffers)
                {
                    WinMm.waveOutUnprepareHeader(_waveOut, buffer.HeaderPointer, Marshal.SizeOf<WaveHeader>());
                }

                WinMm.waveOutClose(_waveOut);
                _waveOut = IntPtr.Zero;
                DebugLog.Write("RINGTONE stop");
            }

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
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                IntPtr waveOut;
                lock (_sync)
                {
                    waveOut = _waveOut;
                }
                if (waveOut == IntPtr.Zero)
                {
                    break;
                }

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
        catch (Exception ex)
        {
            DebugLog.Write($"RINGTONE play loop error: {ex.Message}");
        }
    }

    private int PlayPattern()
    {
        switch (_ringtone)
        {
            case "uk-ringback":
                Repeat(4, () =>
                {
                    QueueChord([400.00, 450.00], 0.40, 0.12);
                    QueuePause(0.20);
                    QueueChord([400.00, 450.00], 0.40, 0.12);
                    QueuePause(2.00);
                });
                return 12000;
            case "office":
                Repeat(4, () =>
                {
                    QueueTone(659.25, 0.10, 0.30);
                    QueueTone(783.99, 0.10, 0.30);
                    QueueTone(880.00, 0.10, 0.30);
                    QueueTone(987.77, 0.15, 0.30);
                    QueueTone(1174.66, 0.15, 0.30);
                    QueueTone(1318.51, 0.30, 0.30);
                    QueuePause(1.80);
                });
                return 11000;
            case "teams":
                Repeat(4, () =>
                {
                    QueueTone(554.37, 0.08, 0.35);
                    QueueTone(622.25, 0.08, 0.35);
                    QueueTone(830.61, 0.08, 0.35);
                    QueueTone(622.25, 0.08, 0.35);
                    QueueTone(932.33, 0.08, 0.35);
                    QueueTone(830.61, 0.30, 0.35);
                    QueuePause(1.60);
                });
                return 10000;
            case "skype":
                Repeat(4, () =>
                {
                    QueueTone(698.46, 0.06, 0.25);
                    QueueTone(880.00, 0.06, 0.25);
                    QueueTone(1046.50, 0.06, 0.25);
                    QueueTone(1396.91, 0.06, 0.25);
                    QueueTone(1760.00, 0.15, 0.25);
                    QueueTone(1396.91, 0.12, 0.25);
                    QueueTone(1046.50, 0.12, 0.25);
                    QueueTone(880.00, 0.20, 0.25);
                    QueuePause(1.60);
                });
                return 10500;
            case "desk":
                Repeat(3, () =>
                {
                    QueueTone(440.00, 0.75, 0.50);
                    QueueTone(480.00, 0.75, 0.50);
                    QueuePause(1.80);
                });
                return 10500;
            case "double":
                Repeat(4, () =>
                {
                    QueueTone(425.00, 0.17, 0.38);
                    QueuePause(0.18);
                    QueueTone(425.00, 0.17, 0.38);
                    QueuePause(1.55);
                });
                return 10500;
            case "reception":
                Repeat(4, () =>
                {
                    QueueTone(659.25, 0.16, 0.25);
                    QueueTone(523.25, 0.16, 0.25);
                    QueueTone(587.33, 0.16, 0.25);
                    QueueTone(392.00, 0.30, 0.25);
                    QueuePause(1.80);
                });
                return 10500;
            case "marimba":
                Repeat(4, () =>
                {
                    QueueTone(523.25, 0.08, 0.30);
                    QueueTone(659.25, 0.08, 0.30);
                    QueueTone(783.99, 0.08, 0.30);
                    QueueTone(659.25, 0.08, 0.30);
                    QueueTone(880.00, 0.08, 0.30);
                    QueueTone(783.99, 0.08, 0.30);
                    QueueTone(1046.50, 0.25, 0.30);
                    QueuePause(1.60);
                });
                return 10500;
            case "piano":
                Repeat(3, () =>
                {
                    QueueChord([329.63, 440.00, 523.25, 659.25], 0.30, 0.35);
                    QueuePause(0.10);
                    QueueChord([349.23, 440.00, 587.33, 698.46], 0.30, 0.35);
                    QueuePause(0.10);
                    QueueChord([392.00, 493.88, 587.33, 783.99], 0.50, 0.35);
                    QueuePause(1.80);
                });
                return 10500;
            case "night":
                Repeat(3, () =>
                {
                    QueueChord([293.66, 440.00, 554.37, 659.25], 0.40, 0.35);
                    QueuePause(0.15);
                    QueueChord([329.63, 493.88, 587.33, 659.25], 0.60, 0.35);
                    QueuePause(1.80);
                });
                return 10500;
            case "urgent":
                Repeat(8, () =>
                {
                    QueueChord([880.00, 1046.50], 0.10, 0.30);
                    QueuePause(0.05);
                    QueueChord([880.00, 1046.50], 0.10, 0.30);
                    QueuePause(0.05);
                    QueueChord([880.00, 1046.50], 0.25, 0.30);
                    QueuePause(1.00);
                });
                return 10500;
            case "merlin":
            default:
                Repeat(4, () =>
                {
                    QueueTone(659.25, 0.10, 0.25);
                    QueueTone(783.99, 0.10, 0.25);
                    QueueTone(880.00, 0.10, 0.25);
                    QueueTone(1046.50, 0.20, 0.30);
                    QueuePause(1.50);
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
                var tone = Math.Sin(2 * Math.PI * frequency * index / SampleRate);
                var shimmer = Math.Sin(2 * Math.PI * (frequency * 2) * index / SampleRate) * 0.18;
                value += tone + shimmer;
            }

            return value / frequencies.Length;
        });
    }

    private void QueueTone(double frequency, double seconds, double gain)
    {
        QueueWave(seconds, gain, index =>
        {
            var tone = Math.Sin(2 * Math.PI * frequency * index / SampleRate);
            var shimmer = Math.Sin(2 * Math.PI * (frequency * 2) * index / SampleRate) * 0.20;
            var subOctave = Math.Sin(2 * Math.PI * (frequency / 2) * index / SampleRate) * 0.10;
            return tone + shimmer + subOctave;
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
            var progress = (double)i / sampleCount;
            var envelope = Math.Min(1.0, Math.Min(i / 200.0, Math.Pow(1.0 - progress, 2.0)));
            var value = sampleFactory(i);
            var sample = (short)Math.Clamp(value * envelope * gain * _volume * short.MaxValue, short.MinValue, short.MaxValue);
            pcm[i * 2] = (byte)(sample & 0xFF);
            pcm[i * 2 + 1] = (byte)(sample >> 8);
        }

        var buffer = new AudioBuffer(pcm.Length);
        Marshal.Copy(pcm, 0, buffer.DataPointer, pcm.Length);
        lock (_sync)
        {
            if (_waveOut == IntPtr.Zero)
            {
                buffer.Dispose();
                return;
            }
            _buffers.Add(buffer);
            WinMm.waveOutPrepareHeader(_waveOut, buffer.HeaderPointer, Marshal.SizeOf<WaveHeader>());
            WinMm.waveOutWrite(_waveOut, buffer.HeaderPointer, Marshal.SizeOf<WaveHeader>());
        }
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
