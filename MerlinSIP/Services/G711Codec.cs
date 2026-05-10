namespace MerlinSip.Services;

internal static class G711Codec
{
    private const int Bias = 0x84;
    private const int Clip = 32635;

    public static byte LinearToMuLaw(short sample)
    {
        var sign = (sample >> 8) & 0x80;
        if (sign != 0)
        {
            sample = (short)-sample;
        }

        if (sample > Clip)
        {
            sample = Clip;
        }

        sample += Bias;
        var exponent = 7;
        for (var mask = 0x4000; (sample & mask) == 0 && exponent > 0; mask >>= 1)
        {
            exponent--;
        }

        var mantissa = (sample >> (exponent + 3)) & 0x0F;
        return (byte)~(sign | (exponent << 4) | mantissa);
    }

    public static short MuLawToLinear(byte value)
    {
        value = (byte)~value;
        var sign = value & 0x80;
        var exponent = (value >> 4) & 0x07;
        var mantissa = value & 0x0F;
        var sample = ((mantissa << 3) + Bias) << exponent;
        sample -= Bias;
        return (short)(sign != 0 ? -sample : sample);
    }

    public static byte LinearToALaw(short sample)
    {
        var sign = 0x00;
        if (sample < 0)
        {
            sample = (short)-sample;
            sign = 0x80;
        }

        if (sample > 32635)
        {
            sample = 32635;
        }

        int encoded;
        if (sample >= 256)
        {
            var exponent = 7;
            for (var mask = 0x4000; (sample & mask) == 0 && exponent > 0; mask >>= 1)
            {
                exponent--;
            }

            var mantissa = (sample >> (exponent + 3)) & 0x0F;
            encoded = sign | (exponent << 4) | mantissa;
        }
        else
        {
            encoded = sign | (sample >> 4);
        }

        return (byte)(encoded ^ 0x55);
    }

    public static short ALawToLinear(byte value)
    {
        value ^= 0x55;
        var sign = value & 0x80;
        var exponent = (value & 0x70) >> 4;
        var mantissa = value & 0x0F;
        var sample = exponent == 0
            ? (mantissa << 4) + 8
            : ((mantissa << 4) + 0x108) << (exponent - 1);
        return (short)(sign != 0 ? -sample : sample);
    }
}
