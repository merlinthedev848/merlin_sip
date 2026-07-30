using System;
using System.Runtime.InteropServices;

namespace MerlinSip.Services;

internal sealed class AudioBuffer : IDisposable
{
	public nint DataPointer { get; }

	public nint HeaderPointer { get; }

	public AudioBuffer(int bytes)
	{
		DataPointer = Marshal.AllocHGlobal(bytes);
		WaveHeader structure = new WaveHeader
		{
			Data = DataPointer,
			BufferLength = bytes
		};
		HeaderPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHeader>());
		Marshal.StructureToPtr(structure, HeaderPointer, fDeleteOld: false);
	}

	public void Dispose()
	{
		if (HeaderPointer != IntPtr.Zero)
		{
			Marshal.FreeHGlobal(HeaderPointer);
		}
		if (DataPointer != IntPtr.Zero)
		{
			Marshal.FreeHGlobal(DataPointer);
		}
	}
}
