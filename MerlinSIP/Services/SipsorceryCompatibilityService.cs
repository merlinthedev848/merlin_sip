using System.Net;
using MerlinSip.Models;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;

namespace MerlinSip.Services;

public sealed class SipsorceryCompatibilityService
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    public async Task<SipsorceryProbeResult> TestTcpRegistrationAsync(AppStartupConfig config, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(config.Username) || string.IsNullOrWhiteSpace(config.Password))
        {
            return new SipsorceryProbeResult(false, "TCP signalling was not tested because account credentials are missing.");
        }

        SIPTransport? transport = null;
        SIPRegistrationUserAgent? registrationAgent = null;

        try
        {
            transport = new SIPTransport();
            transport.AddSIPChannel(new SIPTCPChannel(new IPEndPoint(IPAddress.Any, 0)));

            var server = $"sip:{config.Server}:{config.Port};transport=tcp";
            var completion = new TaskCompletionSource<SipsorceryProbeResult>(TaskCreationOptions.RunContinuationsAsynchronously);

            registrationAgent = new SIPRegistrationUserAgent(
                transport,
                config.Username,
                config.Password,
                server,
                120,
                5,
                1,
                1,
                true,
                true);

            registrationAgent.RegistrationSuccessful += (uri, response) =>
            {
                completion.TrySetResult(new SipsorceryProbeResult(true, "TCP signalling is available. This can avoid UDP SIP ALG rewriting on supported PBX profiles."));
            };

            registrationAgent.RegistrationFailed += (uri, response, error) =>
            {
                completion.TrySetResult(new SipsorceryProbeResult(false, $"TCP signalling failed: {error}"));
            };

            registrationAgent.RegistrationTemporaryFailure += (uri, response, error) =>
            {
                completion.TrySetResult(new SipsorceryProbeResult(false, $"TCP signalling temporarily failed: {error}"));
            };

            registrationAgent.Start();

            var completed = await Task.WhenAny(completion.Task, Task.Delay(ProbeTimeout, cancellationToken));
            if (completed != completion.Task)
            {
                return new SipsorceryProbeResult(false, "TCP signalling did not receive a response before the test timed out.");
            }

            return await completion.Task;
        }
        catch (OperationCanceledException)
        {
            return new SipsorceryProbeResult(false, "TCP signalling test was cancelled.");
        }
        catch (Exception error)
        {
            DebugLog.Write($"SIPSORCERY TCP probe failed error={error.Message}");
            return new SipsorceryProbeResult(false, $"TCP signalling could not be tested: {error.Message}");
        }
        finally
        {
            try
            {
                registrationAgent?.Stop(false);
            }
            catch
            {
            }

            transport?.Shutdown();
        }
    }
}

public sealed record SipsorceryProbeResult(bool Supported, string Message);
