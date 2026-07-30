namespace MerlinSip.Services;

public sealed record HeartbeatStatusEventArgs(bool Success, int ResponseCode, int LatencyMs, int ConsecutiveFailures, string Message);
