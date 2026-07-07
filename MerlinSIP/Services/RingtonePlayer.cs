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
                // A corporate rolling theme in C Major / A Minor (12 seconds)
                QueueTone(261.63, 0.20, 0.22);
                QueueTone(329.63, 0.20, 0.22);
                QueueTone(392.00, 0.20, 0.22);
                QueueChord([523.25, 659.25], 0.40, 0.22);
                QueuePause(0.15);

                QueueTone(220.00, 0.20, 0.22);
                QueueTone(277.18, 0.20, 0.22);
                QueueTone(329.63, 0.20, 0.22);
                QueueChord([440.00, 554.37], 0.40, 0.22);
                QueuePause(0.15);

                QueueTone(293.66, 0.20, 0.22);
                QueueTone(349.23, 0.20, 0.22);
                QueueTone(440.00, 0.20, 0.22);
                QueueChord([587.33, 698.46], 0.40, 0.22);
                QueuePause(0.15);

                QueueChord([392.00, 493.88, 587.33, 783.99], 1.00, 0.25);
                QueuePause(3.50);
                return 12000;
            case "teams":
                // 12-second elaborate pentatonic progression
                QueueTone(138.59, 0.40, 0.20); // Db3 bass
                QueueTone(277.18, 0.15, 0.25); // Db4
                QueueTone(415.30, 0.15, 0.25); // Ab4
                QueueTone(554.37, 0.15, 0.25); // Db5
                QueueTone(659.25, 0.15, 0.25); // F5
                QueueTone(783.99, 0.15, 0.25); // Ab5
                QueueTone(1046.50, 0.50, 0.25); // C6
                QueuePause(0.20);

                QueueTone(116.54, 0.40, 0.20); // Bb2 bass
                QueueTone(233.08, 0.15, 0.25); // Bb3
                QueueTone(349.23, 0.15, 0.25); // F4
                QueueTone(466.16, 0.15, 0.25); // Bb4
                QueueTone(587.33, 0.15, 0.25); // Db5
                QueueTone(698.46, 0.15, 0.25); // F5
                QueueTone(880.00, 0.50, 0.25); // Ab5
                QueuePause(0.20);

                QueueTone(155.56, 0.40, 0.20); // Eb3 bass
                QueueTone(311.13, 0.15, 0.25); // Eb4
                QueueTone(466.16, 0.15, 0.25); // Bb4
                QueueTone(622.25, 0.15, 0.25); // Eb5
                QueueTone(698.46, 0.15, 0.25); // F5
                QueueTone(830.61, 0.15, 0.25); // Ab5
                QueueTone(932.33, 0.50, 0.25); // Bb5
                QueuePause(0.20);

                QueueChord([207.65, 415.30, 523.25, 622.25, 783.99], 1.20, 0.25);
                QueuePause(1.80);
                return 12000;
            case "skype":
                // Cascading running arpeggios that build up and fall (11.5 seconds)
                QueueTone(349.23, 0.08, 0.22);
                QueueTone(523.25, 0.08, 0.22);
                QueueTone(698.46, 0.08, 0.22);
                QueueTone(880.00, 0.08, 0.22);
                QueueTone(1046.50, 0.08, 0.22);
                QueueTone(1396.91, 0.20, 0.22);
                QueuePause(0.10);

                QueueTone(261.63, 0.08, 0.22);
                QueueTone(392.00, 0.08, 0.22);
                QueueTone(523.25, 0.08, 0.22);
                QueueTone(659.25, 0.08, 0.22);
                QueueTone(783.99, 0.08, 0.22);
                QueueTone(1046.50, 0.20, 0.22);
                QueuePause(0.10);

                QueueTone(293.66, 0.08, 0.22);
                QueueTone(440.00, 0.08, 0.22);
                QueueTone(587.33, 0.08, 0.22);
                QueueTone(698.46, 0.08, 0.22);
                QueueTone(880.00, 0.08, 0.22);
                QueueTone(1174.66, 0.20, 0.22);
                QueuePause(0.10);

                QueueChord([349.23, 440.00, 523.25, 659.25, 783.99], 1.20, 0.25);
                QueuePause(2.50);
                return 11500;
            case "desk":
                Repeat(3, () =>
                {
                    QueueChord([440.00, 480.00], 0.75, 0.45);
                    QueuePause(0.20);
                    QueueChord([440.00, 480.00], 0.75, 0.45);
                    QueuePause(1.80);
                });
                return 10500;
            case "double":
                Repeat(4, () =>
                {
                    QueueChord([425.00, 475.00], 0.17, 0.38);
                    QueuePause(0.18);
                    QueueChord([425.00, 475.00], 0.17, 0.38);
                    QueuePause(1.55);
                });
                return 10500;
            case "reception":
                Repeat(3, () =>
                {
                    QueueChord([659.25, 880.00], 0.20, 0.25);
                    QueueChord([523.25, 698.46], 0.20, 0.25);
                    QueueChord([587.33, 783.99], 0.20, 0.25);
                    QueueChord([392.00, 523.25], 0.40, 0.25);
                    QueuePause(1.80);
                });
                return 9500;
            case "marimba":
                // 11-second marimba syncopated pattern with bass accompaniment
                Repeat(2, () =>
                {
                    QueueChord([130.81, 523.25], 0.15, 0.26);
                    QueueTone(659.25, 0.15, 0.26);
                    QueueTone(783.99, 0.15, 0.26);
                    QueueChord([196.00, 659.25], 0.15, 0.26);
                    QueueTone(880.00, 0.15, 0.26);
                    QueueTone(783.99, 0.15, 0.26);
                    QueueChord([130.81, 1046.50], 0.30, 0.26);
                    QueuePause(0.20);
                    
                    QueueChord([146.83, 587.33], 0.15, 0.26);
                    QueueTone(698.46, 0.15, 0.26);
                    QueueTone(880.00, 0.15, 0.26);
                    QueueChord([220.00, 698.46], 0.15, 0.26);
                    QueueTone(987.77, 0.15, 0.26);
                    QueueTone(880.00, 0.15, 0.26);
                    QueueChord([146.83, 1174.66], 0.30, 0.26);
                    QueuePause(1.50);
                });
                return 11000;
            case "piano":
                // Lounge jazz piano progression with a slow, expressive melody (13 seconds)
                QueueChord([110.00, 220.00, 329.63, 392.00, 440.00, 523.25], 1.20, 0.24);
                QueuePause(0.30);
                QueueChord([146.83, 293.66, 369.99, 440.00, 493.88, 587.33], 1.20, 0.24);
                QueuePause(0.30);
                QueueChord([98.00, 196.00, 293.66, 392.00, 440.00, 493.88], 1.20, 0.24);
                QueuePause(0.30);
                QueueChord([130.81, 261.63, 329.63, 392.00, 440.00, 523.25], 1.50, 0.24);
                QueuePause(3.00);
                return 13000;
            case "night":
                // Elaborate ambient electric piano pads (14 seconds)
                QueueChord([146.83, 293.66, 440.00, 554.37, 659.25, 739.99], 1.50, 0.24);
                QueuePause(0.40);
                QueueChord([138.59, 277.18, 415.30, 554.37, 659.25, 830.61], 1.50, 0.24);
                QueuePause(0.40);
                QueueChord([123.47, 246.94, 369.99, 440.00, 493.88, 587.33], 1.50, 0.24);
                QueuePause(0.40);
                QueueChord([110.00, 220.00, 329.63, 440.00, 554.37, 659.25], 1.80, 0.24);
                QueuePause(3.50);
                return 14000;
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
                // Arpeggiated corporate signature chord progression (12 seconds)
                QueueTone(261.63, 0.12, 0.22); // C4
                QueueTone(329.63, 0.12, 0.22); // E4
                QueueTone(392.00, 0.12, 0.22); // G4
                QueueTone(523.25, 0.12, 0.22); // C5
                QueueTone(659.25, 0.12, 0.22); // E5
                QueueTone(783.99, 0.12, 0.22); // G5
                QueueChord([1046.50, 1318.51, 1567.98], 0.80, 0.25);
                QueuePause(0.40);

                QueueTone(349.23, 0.12, 0.22); // F4
                QueueTone(440.00, 0.12, 0.22); // A4
                QueueTone(523.25, 0.12, 0.22); // C5
                QueueTone(698.46, 0.12, 0.22); // F5
                QueueTone(880.00, 0.12, 0.22); // A5
                QueueTone(1046.50, 0.12, 0.22); // C6
                QueueChord([1396.91, 1760.00, 2093.00], 1.20, 0.25);
                QueuePause(3.00);
                return 12000;
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
        var sampleCount = (int)(SampleRate * seconds);
        QueueWave(seconds, gain, (i) =>
        {
            var progress = (double)i / sampleCount;
            var modEnv = Math.Pow(1.0 - progress, 3.0);
            var modIndex = 1.4 * modEnv;
            
            var value = 0.0;
            foreach (var frequency in frequencies)
            {
                var modFreq = frequency * 2.0;
                var modulator = Math.Sin(2 * Math.PI * modFreq * i / SampleRate) * modIndex;
                var tone = Math.Sin(2 * Math.PI * frequency * i / SampleRate + modulator);
                
                var sub = Math.Sin(2 * Math.PI * (frequency / 2.0) * i / SampleRate) * 0.12;
                var second = Math.Sin(2 * Math.PI * (frequency * 2.0) * i / SampleRate) * 0.08;
                
                value += tone + sub + second;
            }

            return value / frequencies.Length;
        });
    }

    private void QueueTone(double frequency, double seconds, double gain)
    {
        var sampleCount = (int)(SampleRate * seconds);
        QueueWave(seconds, gain, (i) =>
        {
            var progress = (double)i / sampleCount;
            var modEnv = Math.Pow(1.0 - progress, 3.5);
            var modIndex = 1.6 * modEnv;
            var modFreq = frequency * 2.0;
            var modulator = Math.Sin(2 * Math.PI * modFreq * i / SampleRate) * modIndex;
            var tone = Math.Sin(2 * Math.PI * frequency * i / SampleRate + modulator);
            
            var sub = Math.Sin(2 * Math.PI * (frequency / 2.0) * i / SampleRate) * 0.15;
            var second = Math.Sin(2 * Math.PI * (frequency * 2.0) * i / SampleRate) * 0.10;
            
            return tone + sub + second;
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
