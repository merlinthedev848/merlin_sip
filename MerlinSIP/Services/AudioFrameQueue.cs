using System;
using System.Collections.Generic;

namespace MerlinSip.Services;

public sealed class AudioFrameQueue
{
	private readonly object _lock = new object();

	private readonly Queue<short[]> _queue = new Queue<short[]>();

	private const int TargetFrames = 4;

	private const int MaxFrames = 14;

	private bool _started;

	private int _underruns;

	private static readonly short[] SharedSilenceFrame = new short[160];

	public void Enqueue(short[] samples)
	{
		lock (_lock)
		{
			if (_queue.Count >= MaxFrames)
			{
				_queue.Dequeue();
			}
			_queue.Enqueue(samples);
		}
	}

	public static short[] GetSilenceFrame(int sampleCount)
	{
		if (sampleCount != 160)
		{
			return new short[sampleCount];
		}
		return SharedSilenceFrame;
	}

	public short[] DequeueOrSilence(int sampleCount)
	{
		lock (_lock)
		{
			if (!_started)
			{
				if (_queue.Count < TargetFrames)
				{
					return GetSilenceFrame(sampleCount);
				}

				_started = true;
			}

			if (_queue.Count > 0)
			{
				return _queue.Dequeue();
			}

			_started = false;
			_underruns++;
		}
		return GetSilenceFrame(sampleCount);
	}

	public int Count
	{
		get
		{
			lock (_lock)
			{
				return _queue.Count;
			}
		}
	}

	public int Underruns
	{
		get
		{
			lock (_lock)
			{
				return _underruns;
			}
		}
	}

	public void Clear()
	{
		lock (_lock)
		{
			_queue.Clear();
			_started = false;
			_underruns = 0;
		}
	}
}
