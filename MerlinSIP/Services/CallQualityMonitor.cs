namespace MerlinSip.Services;

using System;
using System.Threading;
using System.Threading.Tasks;
using MerlinSip.Models;

/// <summary>
/// Provides real-time RTP statistics required for call quality calculation.
/// </summary>
public interface ICallQualityDataSource
{
    /// <summary>
    /// Gets the current packet loss percentage (0.0 - 100.0).
    /// </summary>
    double GetPacketLossPercentage();

    /// <summary>
    /// Gets the current estimated jitter in milliseconds.
    /// </summary>
    double GetJitterMs();

    /// <summary>
    /// Gets the current estimated round-trip latency in milliseconds.
    /// </summary>
    double GetLatencyMs();

    /// <summary>
    /// Gets a value indicating whether a call is currently active.
    /// </summary>
    bool IsCallActive { get; }
}

/// <summary>
/// Monitors live call quality metrics (MOS, R-factor) based on RTP statistics.
/// </summary>
public class CallQualityMonitor : IDisposable
{
    private readonly ICallQualityDataSource _dataSource;
    private CancellationTokenSource? _monitorCancellation;

    /// <summary>
    /// Event fired when the call quality metrics change.
    /// </summary>
    public event EventHandler<CallQualityMetrics>? CallQualityChanged;

    public CallQualityMonitor(ICallQualityDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    /// <summary>
    /// Starts monitoring call quality every 2 seconds.
    /// </summary>
    public void Start(CancellationToken cancellationToken = default)
    {
        _monitorCancellation?.Cancel();
        _monitorCancellation = new CancellationTokenSource();
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _monitorCancellation.Token);

        _ = Task.Run(() => MonitorLoopAsync(linkedCts.Token), linkedCts.Token);
    }

    /// <summary>
    /// Stops monitoring.
    /// </summary>
    public void Stop()
    {
        _monitorCancellation?.Cancel();
    }

    private async Task MonitorLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_dataSource.IsCallActive)
                {
                    var metrics = CalculateCurrentMetrics();
                    CallQualityChanged?.Invoke(this, metrics);
                }
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Optionally log the exception
            }
        }
    }

    private CallQualityMetrics CalculateCurrentMetrics()
    {
        double packetLoss = _dataSource.GetPacketLossPercentage();
        double jitterMs = _dataSource.GetJitterMs();
        double latencyMs = _dataSource.GetLatencyMs();

        // R = 93.2 - (packet_loss * 2.5) - (jitter_ms * 0.1) - (latency_ms * 0.03)
        double rFactor = 93.2 - (packetLoss * 2.5) - (jitterMs * 0.1) - (latencyMs * 0.03);
        
        // MOS = 1 + 0.035 * R + R * (R - 60) * (100 - R) * 7e-6
        double mosScore = 1.0;
        if (rFactor > 0)
        {
            mosScore = 1 + (0.035 * rFactor) + (rFactor * (rFactor - 60) * (100 - rFactor) * 0.000007);
        }
        
        mosScore = Math.Clamp(mosScore, 1.0, 5.0);

        CallQualityLevel level;
        if (mosScore > 4.0)
            level = CallQualityLevel.Excellent;
        else if (mosScore >= 3.5)
            level = CallQualityLevel.Good;
        else if (mosScore >= 3.0)
            level = CallQualityLevel.Fair;
        else
            level = CallQualityLevel.Poor;

        return new CallQualityMetrics
        {
            MosScore = Math.Round(mosScore, 2),
            Level = level,
            Jitter = jitterMs,
            PacketLoss = packetLoss,
            Latency = latencyMs
        };
    }

    public void Dispose()
    {
        Stop();
        _monitorCancellation?.Dispose();
        GC.SuppressFinalize(this);
    }
}
