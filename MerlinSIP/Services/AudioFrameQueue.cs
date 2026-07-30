using System;
using System.Collections.Generic;

namespace MerlinSip.Services;

public sealed class AudioFrameQueue
{
	private readonly object _lock = new object();

	private readonly Queue<short[]> _queue = new Queue<short[]>();

	private int _maxFrames = 8;

	private long _lastArrivalTicks;

	private static readonly short[] SharedSilenceFrame = new short[160];

	public void Enqueue(short[] samples)
	{
		long tickCount = Environment.TickCount64;
		lock (_lock)
		{
			if (_lastArrivalTicks > 0)
			{
				long num = Math.Abs(tickCount - _lastArrivalTicks - 20);
				if (num > 25 && _maxFrames < 14)
				{
					_maxFrames++;
				}
				else if (num <= 4 && _maxFrames > 4)
				{
					_maxFrames--;
				}
			}
			_lastArrivalTicks = tickCount;
			if (_queue.Count >= _maxFrames)
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
			if (_queue.Count > 0)
			{
				return _queue.Dequeue();
			}
		}
		return GetSilenceFrame(sampleCount);
	}

	public void Clear()
	{
		lock (_lock)
		{
			_queue.Clear();
			_maxFrames = 8;
			_lastArrivalTicks = 0L;
		}
	}
}
