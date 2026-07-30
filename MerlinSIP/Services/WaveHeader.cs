namespace MerlinSip.Services;

internal struct WaveHeader
{
	public nint Data;

	public int BufferLength;

	public int BytesRecorded;

	public nint User;

	public int Flags;

	public int Loops;

	public nint Next;

	public nint Reserved;
}
