using System.Runtime.InteropServices;
using MerlinSip.Models;

namespace MerlinSip.Services;

public sealed class RingtonePlayer : IDisposable
{
    private const int SampleRate = 44100;

    public static readonly IReadOnlyList<RingtoneChoice> Choices =
    [
        new("meridian", "Meridian"),
        new("horizon", "Horizon"),
        new("nexus", "Nexus"),
        new("summit", "Summit"),
        new("atlas", "Atlas"),
        new("keystone", "Keystone"),
        new("tempo", "Tempo"),
        new("boardroom", "Boardroom"),
        new("azure", "Azure"),
        new("signature", "Signature")
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
            : Choices[0].Id;
        _volume = Math.Clamp(volume, 0.25, 2.0);

        var format = WaveFormat.Pcm16Mono44k();
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
                    QueueCorporateChord([400.00, 450.00], 0.40, 0.10, VoiceStyle.Pad);
                    QueuePause(0.20);
                    QueueCorporateChord([400.00, 450.00], 0.40, 0.10, VoiceStyle.Pad);
                    QueuePause(2.00);
                });
                return 12000;
            case "horizon":
                QueueCorporateChord([196.00, 293.66, 369.99, 493.88], 0.72, 0.16, VoiceStyle.Pad);
                QueueCorporateChord([493.88, 739.99, 987.77], 0.34, 0.13, VoiceStyle.Bell);
                QueuePause(0.18);
                QueueCorporateChord([220.00, 329.63, 440.00, 554.37], 0.72, 0.16, VoiceStyle.Pad);
                QueueCorporateChord([554.37, 830.61, 1108.73], 0.34, 0.13, VoiceStyle.Bell);
                QueuePause(0.42);
                QueueCorporateChord([246.94, 369.99, 493.88, 659.25], 0.86, 0.15, VoiceStyle.Pad);
                QueueCorporateChord([659.25, 987.77, 1318.51], 0.48, 0.12, VoiceStyle.Bell);
                QueuePause(1.95);
                return 6400;
            case "nexus":
                Repeat(2, () =>
                {
                    QueueCorporateChord([174.61, 261.63, 349.23, 523.25], 0.46, 0.15, VoiceStyle.Pad);
                    QueueCorporateChord([698.46, 880.00, 1046.50], 0.22, 0.13, VoiceStyle.Bell);
                    QueuePause(0.16);
                    QueueCorporateChord([196.00, 293.66, 392.00, 587.33], 0.46, 0.15, VoiceStyle.Pad);
                    QueueCorporateChord([783.99, 987.77, 1174.66], 0.22, 0.13, VoiceStyle.Bell);
                    QueuePause(0.52);
                });
                QueueCorporateChord([220.00, 329.63, 440.00, 659.25, 880.00], 0.90, 0.13, VoiceStyle.Glass);
                QueuePause(1.55);
                return 6600;
            case "summit":
                QueueCorporateChord([130.81, 261.63, 329.63, 392.00, 523.25], 0.88, 0.15, VoiceStyle.Pad);
                QueueCorporateChord([659.25, 783.99, 1046.50], 0.24, 0.12, VoiceStyle.Bell);
                QueueCorporateChord([587.33, 739.99, 987.77], 0.24, 0.12, VoiceStyle.Bell);
                QueuePause(0.34);
                QueueCorporateChord([146.83, 293.66, 369.99, 440.00, 587.33], 0.88, 0.15, VoiceStyle.Pad);
                QueueCorporateChord([739.99, 880.00, 1174.66], 0.42, 0.12, VoiceStyle.Glass);
                QueuePause(1.90);
                return 5600;
            case "atlas":
                Repeat(3, () =>
                {
                    QueueCorporateChord([164.81, 246.94, 329.63, 493.88], 0.38, 0.15, VoiceStyle.Pad);
                    QueuePause(0.10);
                    QueueCorporateChord([493.88, 659.25, 987.77], 0.28, 0.12, VoiceStyle.Bell);
                    QueuePause(0.18);
                    QueueCorporateChord([185.00, 277.18, 369.99, 554.37], 0.38, 0.15, VoiceStyle.Pad);
                    QueueCorporateChord([554.37, 739.99, 1108.73], 0.28, 0.12, VoiceStyle.Bell);
                    QueuePause(0.78);
                });
                return 7000;
            case "keystone":
                QueueCorporateChord([220.00, 329.63, 415.30, 554.37], 0.56, 0.15, VoiceStyle.Pad);
                QueueCorporateChord([554.37, 830.61, 1108.73], 0.30, 0.12, VoiceStyle.Glass);
                QueuePause(0.22);
                QueueCorporateChord([196.00, 293.66, 392.00, 493.88], 0.56, 0.15, VoiceStyle.Pad);
                QueueCorporateChord([493.88, 739.99, 987.77], 0.30, 0.12, VoiceStyle.Glass);
                QueuePause(0.22);
                QueueCorporateChord([246.94, 369.99, 493.88, 659.25], 0.88, 0.14, VoiceStyle.Pad);
                QueueCorporateChord([659.25, 987.77, 1318.51], 0.42, 0.11, VoiceStyle.Bell);
                QueuePause(1.65);
                return 5900;
            case "tempo":
                Repeat(2, () =>
                {
                    QueueCorporateChord([261.63, 329.63, 392.00, 523.25], 0.26, 0.13, VoiceStyle.Pluck);
                    QueuePause(0.08);
                    QueueCorporateChord([329.63, 392.00, 523.25, 659.25], 0.26, 0.13, VoiceStyle.Pluck);
                    QueuePause(0.08);
                    QueueCorporateChord([392.00, 523.25, 659.25, 783.99], 0.36, 0.12, VoiceStyle.Glass);
                    QueuePause(0.48);
                    QueueCorporateChord([246.94, 329.63, 493.88, 659.25], 0.34, 0.12, VoiceStyle.Pluck);
                    QueueCorporateChord([293.66, 392.00, 587.33, 783.99], 0.42, 0.12, VoiceStyle.Glass);
                    QueuePause(0.70);
                });
                return 6400;
            case "boardroom":
                QueueCorporateChord([146.83, 220.00, 293.66, 369.99, 440.00], 0.96, 0.14, VoiceStyle.Pad);
                QueueCorporateChord([587.33, 739.99, 880.00], 0.30, 0.11, VoiceStyle.Bell);
                QueuePause(0.28);
                QueueCorporateChord([164.81, 246.94, 329.63, 415.30, 493.88], 0.96, 0.14, VoiceStyle.Pad);
                QueueCorporateChord([659.25, 830.61, 987.77], 0.30, 0.11, VoiceStyle.Bell);
                QueuePause(2.10);
                return 5900;
            case "azure":
                QueueCorporateChord([174.61, 261.63, 329.63, 440.00, 523.25], 0.70, 0.14, VoiceStyle.Glass);
                QueueCorporateChord([698.46, 880.00, 1046.50], 0.26, 0.11, VoiceStyle.Bell);
                QueuePause(0.14);
                QueueCorporateChord([196.00, 293.66, 369.99, 493.88, 587.33], 0.70, 0.14, VoiceStyle.Glass);
                QueueCorporateChord([783.99, 987.77, 1174.66], 0.26, 0.11, VoiceStyle.Bell);
                QueuePause(0.14);
                QueueCorporateChord([220.00, 329.63, 440.00, 554.37, 659.25], 0.82, 0.13, VoiceStyle.Glass);
                QueuePause(1.80);
                return 5600;
            case "signature":
                QueueCorporateChord([130.81, 196.00, 261.63, 329.63, 392.00], 0.54, 0.15, VoiceStyle.Pad);
                QueueCorporateChord([523.25, 659.25, 783.99, 1046.50], 0.30, 0.12, VoiceStyle.Bell);
                QueuePause(0.18);
                QueueCorporateChord([146.83, 220.00, 293.66, 369.99, 440.00], 0.54, 0.15, VoiceStyle.Pad);
                QueueCorporateChord([587.33, 739.99, 880.00, 1174.66], 0.30, 0.12, VoiceStyle.Bell);
                QueuePause(0.18);
                QueueCorporateChord([164.81, 246.94, 329.63, 415.30, 493.88], 0.78, 0.14, VoiceStyle.Glass);
                QueueCorporateChord([659.25, 830.61, 987.77, 1318.51], 0.42, 0.11, VoiceStyle.Bell);
                QueuePause(1.65);
                return 5900;
            case "meridian":
            default:
                QueueCorporateChord([196.00, 261.63, 329.63, 392.00, 523.25], 0.64, 0.15, VoiceStyle.Pad);
                QueueCorporateChord([659.25, 783.99, 1046.50], 0.28, 0.12, VoiceStyle.Bell);
                QueuePause(0.16);
                QueueCorporateChord([220.00, 293.66, 369.99, 440.00, 587.33], 0.64, 0.15, VoiceStyle.Pad);
                QueueCorporateChord([739.99, 880.00, 1174.66], 0.28, 0.12, VoiceStyle.Bell);
                QueuePause(0.34);
                QueueCorporateChord([246.94, 329.63, 415.30, 493.88, 659.25], 0.84, 0.14, VoiceStyle.Glass);
                QueueCorporateChord([830.61, 987.77, 1318.51], 0.42, 0.11, VoiceStyle.Bell);
                QueuePause(1.70);
                return 6100;
        }
    }

    private static void Repeat(int count, Action phrase)
    {
        for (var index = 0; index < count; index++)
        {
            phrase();
        }
    }

    private void QueueCorporateChord(double[] frequencies, double seconds, double gain, VoiceStyle style)
    {
        var sampleCount = Math.Max(1, (int)(SampleRate * seconds));
        QueueWave(seconds, gain, i =>
        {
            var t = (double)i / SampleRate;
            var progress = (double)i / sampleCount;
            var value = 0.0;

            foreach (var frequency in frequencies)
            {
                value += style switch
                {
                    VoiceStyle.Bell => BellVoice(frequency, t, progress),
                    VoiceStyle.Glass => GlassVoice(frequency, t, progress),
                    VoiceStyle.Pluck => PluckVoice(frequency, t, progress),
                    _ => PadVoice(frequency, t, progress)
                };
            }

            return value / Math.Sqrt(frequencies.Length);
        });
    }

    private static double PadVoice(double frequency, double t, double progress)
    {
        var vibrato = Math.Sin(2 * Math.PI * 4.2 * t) * 0.003;
        var detune = frequency * (1.0 + vibrato);
        var fundamental = Math.Sin(2 * Math.PI * detune * t);
        var warm = Math.Sin(2 * Math.PI * detune * 0.5 * t) * 0.22;
        var air = Math.Sin(2 * Math.PI * detune * 2.0 * t) * 0.08;
        var sheen = Math.Sin(2 * Math.PI * detune * 3.01 * t) * 0.035;
        return (fundamental * 0.76) + warm + air + sheen;
    }

    private static double BellVoice(double frequency, double t, double progress)
    {
        var decay = Math.Exp(-3.7 * progress);
        var strike = Math.Exp(-18.0 * progress);
        var fundamental = Math.Sin(2 * Math.PI * frequency * t) * 0.58;
        var fifth = Math.Sin(2 * Math.PI * frequency * 1.5 * t) * 0.25;
        var octave = Math.Sin(2 * Math.PI * frequency * 2.0 * t) * 0.16;
        var sparkle = Math.Sin(2 * Math.PI * frequency * 2.98 * t) * 0.08;
        return ((fundamental + fifth + octave + sparkle) * decay) + (Math.Sin(2 * Math.PI * frequency * 5.03 * t) * strike * 0.05);
    }

    private static double GlassVoice(double frequency, double t, double progress)
    {
        var shimmer = 1.0 + Math.Sin(2 * Math.PI * 5.6 * t) * 0.0025;
        var carrier = frequency * shimmer;
        var fundamental = Math.Sin(2 * Math.PI * carrier * t) * 0.58;
        var octave = Math.Sin(2 * Math.PI * carrier * 2.0 * t) * 0.20;
        var softFifth = Math.Sin(2 * Math.PI * carrier * 1.498 * t) * 0.16;
        var decay = 0.58 + (Math.Exp(-2.2 * progress) * 0.42);
        return (fundamental + octave + softFifth) * decay;
    }

    private static double PluckVoice(double frequency, double t, double progress)
    {
        var decay = Math.Exp(-5.8 * progress);
        var body = Math.Sin(2 * Math.PI * frequency * t) * 0.62;
        var octave = Math.Sin(2 * Math.PI * frequency * 2.0 * t) * 0.19;
        var transient = Math.Sin(2 * Math.PI * frequency * 4.0 * t) * Math.Exp(-22.0 * progress) * 0.08;
        return (body + octave + transient) * decay;
    }

    private void QueuePause(double seconds)
    {
        QueueWave(seconds, 0, _ => 0);
    }

    private void QueueWave(double seconds, double gain, Func<int, double> sampleFactory)
    {
        var sampleCount = Math.Max(1, (int)(SampleRate * seconds));
        var pcm = new byte[sampleCount * 2];
        for (var i = 0; i < sampleCount; i++)
        {
            var progress = (double)i / sampleCount;
            var attack = Math.Min(1.0, i / (SampleRate * 0.018));
            var release = Math.Min(1.0, (sampleCount - i) / (SampleRate * 0.090));
            var curve = Math.Sin(progress * Math.PI);
            var envelope = Math.Min(attack, release) * (0.84 + curve * 0.16);
            var value = Math.Tanh(sampleFactory(i) * 0.82);
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
            while (_buffers.Count > 96)
            {
                var old = _buffers[0];
                _buffers.RemoveAt(0);
                old.Dispose();
            }
        }
    }

    private enum VoiceStyle
    {
        Pad,
        Bell,
        Glass,
        Pluck
    }
}

public sealed record RingtoneChoice(string Id, string Name)
{
    public override string ToString() => Name;
}
