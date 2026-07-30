namespace MerlinSip.Services;

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
			SamplesPerSec = 8000u,
			AvgBytesPerSec = 16000u,
			BlockAlign = 2,
			BitsPerSample = 16,
			Size = 0
		};
	}

	public static WaveFormat Pcm16Mono44k()
	{
		return new WaveFormat
		{
			FormatTag = 1,
			Channels = 1,
			SamplesPerSec = 44100u,
			AvgBytesPerSec = 88200u,
			BlockAlign = 2,
			BitsPerSample = 16,
			Size = 0
		};
	}
}
