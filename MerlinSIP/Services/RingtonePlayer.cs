using System.Runtime.InteropServices;
using MerlinSip.Models;

namespace MerlinSip.Services;

public sealed class RingtonePlayer : IDisposable
{
    private const int SampleRate = 44100;

    public static readonly IReadOnlyList<RingtoneChoice> Choices =
    [
        new("mobile", "Soft Mobile"),
        new("meridian", "Meridian Groove"),
        new("horizon", "Horizon Beat"),
        new("nexus", "Nexus Pulse"),
        new("summit", "Summit Rhythm"),
        new("atlas", "Atlas Loop"),
        new("keystone", "Keystone Sync"),
        new("tempo", "Tempo Suite"),
        new("boardroom", "Boardroom Beat"),
        new("azure", "Azure Motion"),
        new("signature", "Signature Rhythm")
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
                    QueueToneStep(0.40, [400.00, 450.00], null, Percussion.None, 0.10);
                    QueuePause(0.20);
                    QueueToneStep(0.40, [400.00, 450.00], null, Percussion.None, 0.10);
                    QueuePause(2.00);
                });
                return 12000;
            case "mobile":
                PlayGroove(112, GrooveFeel.Mobile, [
                    Chord([174.61, 261.63, 329.63, 440.00], [659.25, 783.99, 1046.50]),
                    Chord([196.00, 293.66, 369.99, 493.88], [739.99, 880.00, 1174.66]),
                    Chord([220.00, 329.63, 415.30, 554.37], [830.61, 987.77, 1318.51]),
                    Chord([196.00, 293.66, 392.00, 523.25], [783.99, 987.77, 1174.66])
                ]);
                return 8600;
            case "horizon":
                PlayGroove(118, GrooveFeel.Smooth, [
                    Chord([196.00, 293.66, 369.99, 493.88], [739.99, 987.77]),
                    Chord([220.00, 329.63, 440.00, 554.37], [830.61, 1108.73]),
                    Chord([246.94, 369.99, 493.88, 659.25], [987.77, 1318.51]),
                    Chord([220.00, 329.63, 440.00, 587.33], [880.00, 1174.66])
                ]);
                return 8200;
            case "nexus":
                PlayGroove(124, GrooveFeel.Digital, [
                    Chord([174.61, 261.63, 349.23, 523.25], [698.46, 1046.50]),
                    Chord([196.00, 293.66, 392.00, 587.33], [783.99, 1174.66]),
                    Chord([220.00, 329.63, 440.00, 659.25], [880.00, 1318.51]),
                    Chord([196.00, 293.66, 369.99, 587.33], [739.99, 1174.66])
                ]);
                return 7800;
            case "summit":
                PlayGroove(112, GrooveFeel.Wide, [
                    Chord([130.81, 261.63, 329.63, 392.00, 523.25], [659.25, 1046.50]),
                    Chord([146.83, 293.66, 369.99, 440.00, 587.33], [739.99, 1174.66]),
                    Chord([164.81, 329.63, 415.30, 493.88, 659.25], [830.61, 1318.51]),
                    Chord([196.00, 293.66, 392.00, 493.88, 587.33], [783.99, 1174.66])
                ]);
                return 8600;
            case "atlas":
                PlayGroove(126, GrooveFeel.Crisp, [
                    Chord([164.81, 246.94, 329.63, 493.88], [659.25, 987.77]),
                    Chord([185.00, 277.18, 369.99, 554.37], [739.99, 1108.73]),
                    Chord([207.65, 311.13, 415.30, 622.25], [830.61, 1244.51]),
                    Chord([185.00, 277.18, 369.99, 587.33], [739.99, 1174.66])
                ]);
                return 7700;
            case "keystone":
                PlayGroove(116, GrooveFeel.SoftHouse, [
                    Chord([220.00, 329.63, 415.30, 554.37], [830.61, 1108.73]),
                    Chord([196.00, 293.66, 392.00, 493.88], [739.99, 987.77]),
                    Chord([246.94, 369.99, 493.88, 659.25], [987.77, 1318.51]),
                    Chord([220.00, 329.63, 440.00, 587.33], [880.00, 1174.66])
                ]);
                return 8300;
            case "tempo":
                PlayGroove(132, GrooveFeel.Upbeat, [
                    Chord([261.63, 329.63, 392.00, 523.25], [659.25, 1046.50]),
                    Chord([293.66, 369.99, 440.00, 587.33], [739.99, 1174.66]),
                    Chord([329.63, 415.30, 493.88, 659.25], [830.61, 1318.51]),
                    Chord([293.66, 392.00, 493.88, 587.33], [783.99, 1174.66])
                ]);
                return 7300;
            case "boardroom":
                PlayGroove(108, GrooveFeel.Lounge, [
                    Chord([146.83, 220.00, 293.66, 369.99, 440.00], [587.33, 880.00]),
                    Chord([164.81, 246.94, 329.63, 415.30, 493.88], [659.25, 987.77]),
                    Chord([196.00, 293.66, 369.99, 493.88, 587.33], [739.99, 1174.66]),
                    Chord([174.61, 261.63, 349.23, 440.00, 523.25], [698.46, 1046.50])
                ]);
                return 8900;
            case "azure":
                PlayGroove(120, GrooveFeel.Airy, [
                    Chord([174.61, 261.63, 329.63, 440.00, 523.25], [698.46, 1046.50]),
                    Chord([196.00, 293.66, 369.99, 493.88, 587.33], [783.99, 1174.66]),
                    Chord([220.00, 329.63, 440.00, 554.37, 659.25], [880.00, 1318.51]),
                    Chord([196.00, 293.66, 392.00, 493.88, 587.33], [783.99, 1174.66])
                ]);
                return 8000;
            case "signature":
                PlayGroove(122, GrooveFeel.Brand, [
                    Chord([130.81, 196.00, 261.63, 329.63, 392.00], [523.25, 783.99]),
                    Chord([146.83, 220.00, 293.66, 369.99, 440.00], [587.33, 880.00]),
                    Chord([164.81, 246.94, 329.63, 415.30, 493.88], [659.25, 987.77]),
                    Chord([196.00, 293.66, 392.00, 493.88, 587.33], [783.99, 1174.66])
                ]);
                return 7900;
            case "meridian":
            default:
                PlayGroove(120, GrooveFeel.Corporate, [
                    Chord([196.00, 261.63, 329.63, 392.00, 523.25], [659.25, 1046.50]),
                    Chord([220.00, 293.66, 369.99, 440.00, 587.33], [739.99, 1174.66]),
                    Chord([246.94, 329.63, 415.30, 493.88, 659.25], [830.61, 1318.51]),
                    Chord([220.00, 293.66, 392.00, 493.88, 587.33], [783.99, 1174.66])
                ]);
                return 8000;
        }
    }

    private void PlayGroove(int bpm, GrooveFeel feel, GrooveChord[] progression)
    {
        var stepSeconds = 60.0 / bpm / 2.0;
        for (var bar = 0; bar < progression.Length; bar++)
        {
            var chord = progression[bar];
            for (var step = 0; step < 8; step++)
            {
                var percussion = GetPercussion(feel, bar, step);
                var pad = GetPadNotes(feel, chord, step);
                var pluck = GetHookNotes(feel, chord, bar, step);
                var bass = GetBassNote(feel, chord, step);

                QueueGrooveStep(stepSeconds, pad, pluck, bass, percussion, feel);
            }
        }
    }

    private static Percussion GetPercussion(GrooveFeel feel, int bar, int step)
    {
        return feel switch
        {
            GrooveFeel.Smooth => step switch
            {
                0 => Percussion.Kick | Percussion.Hat,
                2 => Percussion.Snare,
                4 => Percussion.Kick | Percussion.Hat,
                6 => Percussion.Snare | Percussion.Hat,
                _ => step % 2 == 1 ? Percussion.GhostHat : Percussion.None
            },
            GrooveFeel.Digital => step switch
            {
                0 => Percussion.Kick | Percussion.Hat,
                1 => Percussion.GhostHat,
                2 => Percussion.Snare | Percussion.Hat,
                3 => Percussion.GhostHat,
                4 => Percussion.Kick,
                5 => Percussion.Hat,
                6 => Percussion.Snare | Percussion.GhostHat,
                _ => Percussion.Hat
            },
            GrooveFeel.Wide => step switch
            {
                0 => Percussion.Kick,
                3 => Percussion.Hat,
                4 => Percussion.Kick | Percussion.Hat,
                6 => Percussion.Snare | Percussion.Clap,
                _ => Percussion.None
            },
            GrooveFeel.Crisp => step switch
            {
                0 => Percussion.Kick | Percussion.Hat,
                1 => Percussion.GhostHat,
                2 => Percussion.Snare | Percussion.Hat,
                3 => Percussion.GhostHat,
                4 => Percussion.Kick | Percussion.Hat,
                5 => Percussion.GhostHat,
                6 => Percussion.Snare | Percussion.Hat,
                _ => Percussion.GhostHat
            },
            GrooveFeel.SoftHouse => step switch
            {
                0 or 4 => Percussion.Kick | Percussion.Hat,
                2 => Percussion.Clap,
                3 or 7 => Percussion.GhostHat,
                6 => Percussion.Snare | Percussion.Clap,
                _ => Percussion.Hat
            },
            GrooveFeel.Upbeat => step switch
            {
                0 => Percussion.Kick | Percussion.Hat,
                1 => Percussion.Hat,
                2 => Percussion.Snare | Percussion.GhostHat,
                3 => Percussion.Kick | Percussion.GhostHat,
                4 => Percussion.Kick | Percussion.Hat,
                5 => Percussion.Hat,
                6 => Percussion.Snare | Percussion.Hat,
                _ => Percussion.Clap | Percussion.GhostHat
            },
            GrooveFeel.Lounge => step switch
            {
                0 => Percussion.Kick,
                2 => Percussion.Hat,
                3 => Percussion.Snare,
                5 => Percussion.Kick | Percussion.GhostHat,
                6 => Percussion.Clap,
                _ => Percussion.None
            },
            GrooveFeel.Airy => step switch
            {
                0 => Percussion.Kick,
                2 => Percussion.Hat,
                4 => Percussion.Kick | Percussion.GhostHat,
                6 => Percussion.Snare,
                _ => bar % 2 == 0 && step == 7 ? Percussion.GhostHat : Percussion.None
            },
            GrooveFeel.Brand => step switch
            {
                0 => Percussion.Kick | Percussion.Hat,
                2 => Percussion.Snare | Percussion.Clap,
                3 => Percussion.GhostHat,
                4 => Percussion.Kick | Percussion.Hat,
                5 => Percussion.GhostHat,
                6 => Percussion.Snare | Percussion.Hat,
                7 => Percussion.Clap | Percussion.GhostHat,
                _ => Percussion.Hat
            },
            GrooveFeel.Mobile => step switch
            {
                0 => Percussion.Kick,
                1 => Percussion.Hat,
                3 => Percussion.GhostHat,
                4 => Percussion.Kick | Percussion.Hat,
                6 => Percussion.Clap,
                _ => Percussion.None
            },
            _ => step switch
            {
                0 or 4 => Percussion.Kick | Percussion.Hat,
                2 or 6 => Percussion.Snare,
                1 or 5 => Percussion.Hat,
                _ => Percussion.GhostHat
            }
        };
    }

    private static double[]? GetPadNotes(GrooveFeel feel, GrooveChord chord, int step)
    {
        return feel switch
        {
            GrooveFeel.Wide => step is 0 ? chord.Pad : null,
            GrooveFeel.Lounge => step is 0 or 5 ? chord.Pad : null,
            GrooveFeel.Airy => step is 0 or 6 ? chord.Pad : null,
            GrooveFeel.Upbeat => step is 0 or 3 or 6 ? TrimChord(chord.Pad, 4) : null,
            GrooveFeel.Crisp => step is 0 or 4 or 7 ? TrimChord(chord.Pad, 3) : null,
            GrooveFeel.Digital => step is 0 or 2 or 5 ? TrimChord(chord.Pad, 3) : null,
            GrooveFeel.SoftHouse => step is 0 or 4 ? chord.Pad : null,
            GrooveFeel.Brand => step is 0 or 4 or 6 ? chord.Pad : null,
            GrooveFeel.Mobile => step is 0 or 4 ? TrimChord(chord.Pad, 3) : null,
            GrooveFeel.Smooth => step is 0 or 4 ? chord.Pad : null,
            _ => step is 0 or 4 ? chord.Pad : null
        };
    }

    private static double[]? GetHookNotes(GrooveFeel feel, GrooveChord chord, int bar, int step)
    {
        return feel switch
        {
            GrooveFeel.Smooth => step switch
            {
                1 => chord.Hook,
                5 => ShiftOctave(chord.Hook, 0.75),
                _ => null
            },
            GrooveFeel.Digital => step switch
            {
                1 or 3 => ShiftOctave(chord.Hook, 1.5),
                6 => chord.Hook,
                _ => null
            },
            GrooveFeel.Wide => step switch
            {
                2 => chord.Hook,
                7 => ShiftOctave(chord.Hook, 1.5),
                _ => null
            },
            GrooveFeel.Crisp => step switch
            {
                1 => chord.Hook,
                3 => ShiftOctave(chord.Hook, 1.5),
                5 => ShiftOctave(chord.Hook, 0.75),
                7 => chord.Hook,
                _ => null
            },
            GrooveFeel.SoftHouse => step switch
            {
                2 => ShiftOctave(chord.Hook, 0.75),
                6 => chord.Hook,
                _ => null
            },
            GrooveFeel.Upbeat => step switch
            {
                0 => bar % 2 == 0 ? null : chord.Hook,
                1 => chord.Hook,
                3 => ShiftOctave(chord.Hook, 1.5),
                4 => ShiftOctave(chord.Hook, 0.75),
                7 => chord.Hook,
                _ => null
            },
            GrooveFeel.Lounge => step switch
            {
                2 => ShiftOctave(chord.Hook, 0.75),
                6 => ShiftOctave(chord.Hook, 1.5),
                _ => null
            },
            GrooveFeel.Airy => step switch
            {
                3 => chord.Hook,
                7 => ShiftOctave(chord.Hook, 1.5),
                _ => null
            },
            GrooveFeel.Brand => step switch
            {
                1 => chord.Hook,
                2 => ShiftOctave(chord.Hook, 1.5),
                5 => chord.Hook,
                7 => ShiftOctave(chord.Hook, 0.75),
                _ => null
            },
            GrooveFeel.Mobile => step switch
            {
                1 => chord.Hook,
                2 => ShiftOctave(chord.Hook, 0.75),
                5 => chord.Hook,
                7 => ShiftOctave(chord.Hook, 1.5),
                _ => null
            },
            _ => step switch
            {
                1 => chord.Hook,
                3 => ShiftOctave(chord.Hook, 0.75),
                5 => chord.Hook,
                7 => ShiftOctave(chord.Hook, 1.5),
                _ => null
            }
        };
    }

    private static double? GetBassNote(GrooveFeel feel, GrooveChord chord, int step)
    {
        return feel switch
        {
            GrooveFeel.Wide => step is 0 or 4 ? chord.Pad[0] : null,
            GrooveFeel.Lounge => step is 0 or 5 ? chord.Pad[0] : null,
            GrooveFeel.Upbeat => step is 0 or 3 or 4 ? chord.Pad[0] : null,
            GrooveFeel.Digital => step is 0 or 4 or 6 ? chord.Pad[0] : null,
            GrooveFeel.SoftHouse => step is 0 or 2 or 4 or 6 ? chord.Pad[0] : null,
            GrooveFeel.Brand => step is 0 or 4 or 7 ? chord.Pad[0] : null,
            GrooveFeel.Mobile => step is 0 or 4 ? chord.Pad[0] : null,
            GrooveFeel.Airy => step == 0 ? chord.Pad[0] : null,
            _ => step is 0 or 4 ? chord.Pad[0] : null
        };
    }

    private static double[] TrimChord(double[] notes, int count)
    {
        if (notes.Length <= count)
        {
            return notes;
        }

        var trimmed = new double[count];
        Array.Copy(notes, trimmed, count);
        return trimmed;
    }

    private static GrooveChord Chord(double[] pad, double[] hook)
    {
        return new GrooveChord(pad, hook);
    }

    private static double[] ShiftOctave(double[] notes, double multiplier)
    {
        var shifted = new double[notes.Length];
        for (var index = 0; index < notes.Length; index++)
        {
            shifted[index] = notes[index] * multiplier;
        }

        return shifted;
    }

    private static void Repeat(int count, Action phrase)
    {
        for (var index = 0; index < count; index++)
        {
            phrase();
        }
    }

    private void QueueGrooveStep(double seconds, double[]? pad, double[]? pluck, double? bass, Percussion percussion, GrooveFeel feel)
    {
        var sampleCount = Math.Max(1, (int)(SampleRate * seconds));
        QueueWave(seconds, 0.74, i =>
        {
            var t = (double)i / SampleRate;
            var progress = (double)i / sampleCount;
            var value = 0.0;

            if (bass.HasValue)
            {
                value += BassVoice(bass.Value, t, progress) * FeelBassGain(feel);
            }

            if (pad is not null)
            {
                value += MixNotes(pad, t, progress, PadVoice) * FeelPadGain(feel);
            }

            if (pluck is not null)
            {
                value += MixNotes(pluck, t, progress, feel == GrooveFeel.Mobile ? MalletVoice : PluckVoice) * FeelHookGain(feel);
            }

            value += PercussionVoice(percussion, t, progress, i) * FeelDrumGain(feel);
            return value;
        });
    }

    private void QueueToneStep(double seconds, double[]? pad, double[]? pluck, Percussion percussion, double gain)
    {
        var sampleCount = Math.Max(1, (int)(SampleRate * seconds));
        QueueWave(seconds, gain, i =>
        {
            var t = (double)i / SampleRate;
            var progress = (double)i / sampleCount;
            var value = 0.0;
            if (pad is not null) value += MixNotes(pad, t, progress, PadVoice);
            if (pluck is not null) value += MixNotes(pluck, t, progress, PluckVoice);
            value += PercussionVoice(percussion, t, progress, i);
            return value;
        });
    }

    private static double MixNotes(double[] notes, double t, double progress, Func<double, double, double, double> voice)
    {
        var value = 0.0;
        foreach (var note in notes)
        {
            value += voice(note, t, progress);
        }

        return value / Math.Sqrt(notes.Length);
    }

    private static double BassVoice(double frequency, double t, double progress)
    {
        var env = Math.Exp(-1.6 * progress);
        var body = Math.Sin(2 * Math.PI * frequency * 0.5 * t) * 0.55;
        var upper = Math.Sin(2 * Math.PI * frequency * t) * 0.22;
        return (body + upper) * env;
    }

    private static double PadVoice(double frequency, double t, double progress)
    {
        var attack = Math.Min(1.0, progress / 0.18);
        var release = 0.68 + Math.Sin(progress * Math.PI) * 0.32;
        var detune = frequency * (1.0 + Math.Sin(2 * Math.PI * 4.1 * t) * 0.002);
        var main = Math.Sin(2 * Math.PI * detune * t) * 0.44;
        var octave = Math.Sin(2 * Math.PI * detune * 2.0 * t) * 0.09;
        var fifth = Math.Sin(2 * Math.PI * detune * 1.5 * t) * 0.11;
        return (main + octave + fifth) * attack * release;
    }

    private static double PluckVoice(double frequency, double t, double progress)
    {
        var decay = Math.Exp(-5.2 * progress);
        var main = Math.Sin(2 * Math.PI * frequency * t) * 0.44;
        var glass = Math.Sin(2 * Math.PI * frequency * 2.01 * t) * 0.16;
        var chime = Math.Sin(2 * Math.PI * frequency * 3.02 * t) * 0.06;
        return (main + glass + chime) * decay;
    }

    private static double MalletVoice(double frequency, double t, double progress)
    {
        var decay = Math.Exp(-4.0 * progress);
        var body = Math.Sin(2 * Math.PI * frequency * t) * 0.34;
        var woody = Math.Sin(2 * Math.PI * frequency * 1.997 * t) * 0.20;
        var bell = Math.Sin(2 * Math.PI * frequency * 3.01 * t) * 0.08;
        var tap = Math.Sin(2 * Math.PI * frequency * 6.0 * t) * Math.Exp(-34.0 * progress) * 0.05;
        return (body + woody + bell + tap) * decay;
    }

    private static double PercussionVoice(Percussion percussion, double t, double progress, int sampleIndex)
    {
        var value = 0.0;
        if (percussion.HasFlag(Percussion.Kick))
        {
            var freq = 78.0 - (34.0 * Math.Min(1.0, progress * 3.0));
            value += Math.Sin(2 * Math.PI * freq * t) * Math.Exp(-9.0 * progress) * 0.72;
            value += Noise(sampleIndex) * Math.Exp(-60.0 * progress) * 0.08;
        }

        if (percussion.HasFlag(Percussion.Snare))
        {
            value += Noise(sampleIndex + 173) * Math.Exp(-18.0 * progress) * 0.24;
            value += Math.Sin(2 * Math.PI * 190.0 * t) * Math.Exp(-13.0 * progress) * 0.10;
        }

        if (percussion.HasFlag(Percussion.Clap))
        {
            var burst = Math.Exp(-30.0 * Math.Abs(progress - 0.08)) +
                        Math.Exp(-36.0 * Math.Abs(progress - 0.16)) +
                        Math.Exp(-44.0 * Math.Abs(progress - 0.24));
            value += Noise(sampleIndex + 911) * burst * 0.09;
        }

        if (percussion.HasFlag(Percussion.Hat))
        {
            var hat = Noise(sampleIndex + 511) - Noise(sampleIndex / 2 + 19);
            value += hat * Math.Exp(-32.0 * progress) * 0.070;
        }

        if (percussion.HasFlag(Percussion.GhostHat))
        {
            var hat = Noise(sampleIndex + 1201) - Noise(sampleIndex / 3 + 71);
            value += hat * Math.Exp(-45.0 * progress) * 0.040;
        }

        return value;
    }

    private static double Noise(int sampleIndex)
    {
        unchecked
        {
            var x = (uint)sampleIndex;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            return (x / (double)uint.MaxValue * 2.0) - 1.0;
        }
    }

    private static double FeelBassGain(GrooveFeel feel)
    {
        return feel switch
        {
            GrooveFeel.Lounge or GrooveFeel.SoftHouse => 0.24,
            GrooveFeel.Upbeat or GrooveFeel.Crisp => 0.20,
            _ => 0.18
        };
    }

    private static double FeelPadGain(GrooveFeel feel)
    {
        return feel switch
        {
            GrooveFeel.Wide or GrooveFeel.Lounge or GrooveFeel.Airy => 0.34,
            GrooveFeel.Crisp or GrooveFeel.Upbeat => 0.24,
            _ => 0.28
        };
    }

    private static double FeelHookGain(GrooveFeel feel)
    {
        return feel switch
        {
            GrooveFeel.Upbeat or GrooveFeel.Brand => 0.34,
            GrooveFeel.Digital or GrooveFeel.Crisp => 0.31,
            _ => 0.27
        };
    }

    private static double FeelDrumGain(GrooveFeel feel)
    {
        return feel switch
        {
            GrooveFeel.Smooth or GrooveFeel.Airy => 0.42,
            GrooveFeel.Mobile => 0.28,
            GrooveFeel.Lounge or GrooveFeel.SoftHouse => 0.55,
            GrooveFeel.Upbeat or GrooveFeel.Crisp => 0.62,
            _ => 0.50
        };
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
            var attack = Math.Min(1.0, i / (SampleRate * 0.010));
            var release = Math.Min(1.0, (sampleCount - i) / (SampleRate * 0.028));
            var value = Math.Tanh(sampleFactory(i) * 0.95);
            var sample = (short)Math.Clamp(value * Math.Min(attack, release) * gain * _volume * short.MaxValue, short.MinValue, short.MaxValue);
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
            while (_buffers.Count > 128)
            {
                var old = _buffers[0];
                _buffers.RemoveAt(0);
                old.Dispose();
            }
        }
    }

    private sealed record GrooveChord(double[] Pad, double[] Hook);

    private enum GrooveFeel
    {
        Corporate,
        Smooth,
        Digital,
        Wide,
        Crisp,
        SoftHouse,
        Upbeat,
        Lounge,
        Airy,
        Brand
        ,
        Mobile
    }

    [Flags]
    private enum Percussion
    {
        None = 0,
        Kick = 1,
        Snare = 2,
        Hat = 4,
        GhostHat = 8,
        Clap = 16
    }
}

public sealed record RingtoneChoice(string Id, string Name)
{
    public override string ToString() => Name;
}
