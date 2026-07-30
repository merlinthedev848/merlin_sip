using System.Runtime.InteropServices;

namespace MerlinSip.Services;

internal static partial class WinMm
{
	public delegate void WaveInProc(nint waveIn, uint message, nint instance, nint header, nint reserved);

	public const uint WimData = 960u;

	public const uint CallbackFunction = 196608u;

	[DllImport("winmm.dll")]
	public static extern int waveInOpen(out nint waveIn, int deviceId, ref WaveFormat format, WaveInProc callback, nint instance, uint flags);

	[DllImport("winmm.dll")]
	public static extern int waveInPrepareHeader(nint waveIn, nint header, int size);

	[DllImport("winmm.dll")]
	public static extern int waveInUnprepareHeader(nint waveIn, nint header, int size);

	[DllImport("winmm.dll")]
	public static extern int waveInAddBuffer(nint waveIn, nint header, int size);

	[DllImport("winmm.dll")]
	public static extern int waveInStart(nint waveIn);

	[DllImport("winmm.dll")]
	public static extern int waveInStop(nint waveIn);

	[DllImport("winmm.dll")]
	public static extern int waveInReset(nint waveIn);

	[DllImport("winmm.dll")]
	public static extern int waveInClose(nint waveIn);

	[DllImport("winmm.dll")]
	public static extern int waveOutOpen(out nint waveOut, int deviceId, ref WaveFormat format, nint callback, nint instance, uint flags);

	[DllImport("winmm.dll")]
	public static extern int waveOutPrepareHeader(nint waveOut, nint header, int size);

	[DllImport("winmm.dll")]
	public static extern int waveOutUnprepareHeader(nint waveOut, nint header, int size);

	[DllImport("winmm.dll")]
	public static extern int waveOutWrite(nint waveOut, nint header, int size);

	[DllImport("winmm.dll")]
	public static extern int waveOutReset(nint waveOut);

	[DllImport("winmm.dll")]
	public static extern int waveOutClose(nint waveOut);
}
