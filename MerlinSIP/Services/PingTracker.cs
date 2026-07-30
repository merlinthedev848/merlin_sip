using System;
using System.Collections.Generic;
using System.IO;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MerlinSip.Services;

public class PingTracker
{
	private CancellationTokenSource? _cts;
	private readonly List<PingResult> _allResults = new List<PingResult>();
	private readonly List<PingResult> _recentResults = new List<PingResult>();
	private const int MaxRecentCount = 60;
	private readonly object _lock = new object();
	private long _minLatency = long.MaxValue;
	private long _maxLatency = long.MinValue;
	private double _sumLatencies;
	private int _successfulPingsCount;
	private int _totalPings;
	private int _failedPings;
	private double _sumJitterDiff;
	private long? _lastSuccessfulLatency;

	public bool IsRunning => _cts != null;

	public string CurrentTarget { get; private set; } = string.Empty;

	public int CurrentIntervalMs { get; private set; } = 1000;

	public event Action<PingResult, PingStats>? OnPingResult;

	public void Start(string target, int intervalMs)
	{
		Stop();
		lock (_lock)
		{
			_cts = new CancellationTokenSource();
			CurrentTarget = target;
			CurrentIntervalMs = intervalMs;
			_allResults.Clear();
			_recentResults.Clear();
			_minLatency = long.MaxValue;
			_maxLatency = long.MinValue;
			_sumLatencies = 0.0;
			_successfulPingsCount = 0;
			_totalPings = 0;
			_failedPings = 0;
			_sumJitterDiff = 0.0;
			_lastSuccessfulLatency = null;
			CancellationToken token = _cts.Token;
			Task.Run(() => RunPingLoopAsync(target, intervalMs, token), token);
		}
	}

	public void Stop()
	{
		CancellationTokenSource? oldCts;
		lock (_lock)
		{
			oldCts = _cts;
			_cts = null;
		}
		if (oldCts != null)
		{
			oldCts.Cancel();
			Task.Delay(500).ContinueWith(delegate
			{
				oldCts.Dispose();
			});
		}
	}

	public List<PingResult> GetRecentResults()
	{
		lock (_lock)
		{
			return new List<PingResult>(_recentResults);
		}
	}

	public List<PingResult> GetAllResults()
	{
		lock (_lock)
		{
			return new List<PingResult>(_allResults);
		}
	}

	public PingStats GetCurrentStats()
	{
		lock (_lock)
		{
			return CalculateStats();
		}
	}

	private async Task RunPingLoopAsync(string target, int intervalMs, CancellationToken token)
	{
		while (!token.IsCancellationRequested)
		{
			DateTime startTime = DateTime.Now;
			PingResult pingResult;
			try
			{
				using Ping ping = new Ping();
				int timeout = Math.Min(intervalMs, 2000);
				PingReply pingReply = await ping.SendPingAsync(target, timeout);
				pingResult = new PingResult
				{
					Timestamp = DateTime.Now,
					Target = target,
					LatencyMs = (pingReply.Status == IPStatus.Success) ? (long?)pingReply.RoundtripTime : null,
					Status = pingReply.Status
				};
			}
			catch (Exception)
			{
				pingResult = new PingResult
				{
					Timestamp = DateTime.Now,
					Target = target,
					LatencyMs = null,
					Status = IPStatus.Unknown
				};
			}
			PingStats arg;
			lock (_lock)
			{
				_allResults.Add(pingResult);
				_recentResults.Add(pingResult);
				if (_recentResults.Count > 60)
				{
					_recentResults.RemoveAt(0);
				}
				_totalPings++;
				if (pingResult.LatencyMs.HasValue)
				{
					long value = pingResult.LatencyMs.Value;
					_successfulPingsCount++;
					_sumLatencies += value;
					if (value < _minLatency)
					{
						_minLatency = value;
					}
					if (value > _maxLatency)
					{
						_maxLatency = value;
					}
					if (_lastSuccessfulLatency.HasValue)
					{
						_sumJitterDiff += Math.Abs(value - _lastSuccessfulLatency.Value);
					}
					_lastSuccessfulLatency = value;
				}
				else
				{
					_failedPings++;
				}
				arg = CalculateStats();
			}
			if (!token.IsCancellationRequested)
			{
				this.OnPingResult?.Invoke(pingResult, arg);
			}
			double totalMilliseconds = (DateTime.Now - startTime).TotalMilliseconds;
			double num = (double)intervalMs - totalMilliseconds;
			if (num > 0.0)
			{
				try
				{
					await Task.Delay((int)num, token);
				}
				catch (TaskCanceledException)
				{
					break;
				}
			}
			else
			{
				try
				{
					await Task.Delay(50, token);
				}
				catch (TaskCanceledException)
				{
					break;
				}
			}
		}
	}

	private PingStats CalculateStats()
	{
		PingStats pingStats = new PingStats();
		if (_totalPings == 0)
		{
			return pingStats;
		}
		pingStats.LossPercentage = (double)_failedPings / (double)_totalPings * 100.0;
		if (_successfulPingsCount > 0)
		{
			pingStats.Current = _lastSuccessfulLatency.GetValueOrDefault();
			pingStats.Min = _minLatency;
			pingStats.Max = _maxLatency;
			pingStats.Average = _sumLatencies / (double)_successfulPingsCount;
			if (_successfulPingsCount > 1)
			{
				pingStats.Jitter = _sumJitterDiff / (double)(_successfulPingsCount - 1);
			}
			else
			{
				pingStats.Jitter = 0.0;
			}
		}
		else
		{
			pingStats.Current = 0L;
			pingStats.Min = 0L;
			pingStats.Max = 0L;
			pingStats.Average = 0.0;
			pingStats.Jitter = 0.0;
		}
		return pingStats;
	}

	public void ExportLog(string filePath)
	{
		StringBuilder stringBuilder = new StringBuilder();
		stringBuilder.AppendLine("Timestamp,Target,LatencyMs,Status");
		lock (_lock)
		{
			foreach (PingResult allResult in _allResults)
			{
				string value = allResult.LatencyMs.HasValue ? allResult.LatencyMs.Value.ToString() : "TIMEOUT";
				StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(3, 4, stringBuilder);
				handler.AppendFormatted(allResult.Timestamp, "yyyy-MM-dd HH:mm:ss");
				handler.AppendLiteral(",");
				handler.AppendFormatted(CsvEscape(allResult.Target));
				handler.AppendLiteral(",");
				handler.AppendFormatted(value);
				handler.AppendLiteral(",");
				handler.AppendFormatted(allResult.Status);
				stringBuilder.AppendLine(ref handler);
			}
		}
		File.WriteAllText(filePath, stringBuilder.ToString());
	}

	private static string CsvEscape(string field)
	{
		if (string.IsNullOrEmpty(field))
		{
			return field;
		}
		if (field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r'))
		{
			return "\"" + field.Replace("\"", "\"\"") + "\"";
		}
		return field;
	}
}
