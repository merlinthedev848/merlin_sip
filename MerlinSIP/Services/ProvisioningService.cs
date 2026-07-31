using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using MerlinSip.Models;

namespace MerlinSip.Services;

public sealed class ProvisioningService
{
    private static readonly string[] ProvisioningUrlTemplates =
    [
        "https://accounts.chriskendall.media/sip/register/{code}",
        "https://accounts.chriskendall.media/index.php/sip/register/{code}",
        "https://accounts.chriskendall.media/sip/device/{code}",
        "https://accounts.chriskendall.media/index.php/sip/device/{code}",
        "https://accounts.chriskendall.media/sip/autoprovision/{code}",
        "https://accounts.chriskendall.media/index.php/sip/autoprovision/{code}",
        "https://accounts.chriskendall.media/sip/details/{code}",
        "https://accounts.chriskendall.media/index.php/sip/details/{code}",
        "https://accounts.chriskendall.media/sip/provision/{code}",
        "https://accounts.chriskendall.media/index.php/sip/provision/{code}",
        "https://accounts.chriskendall.media/sip/details",
        "https://accounts.chriskendall.media/index.php/sip/details",
        "https://accounts.chriskendall.media/sip/autoprovision/details",
        "https://accounts.chriskendall.media/index.php/sip/autoprovision/details",
        "https://accounts.chriskendall.media/sip/provision/details",
        "https://accounts.chriskendall.media/index.php/sip/provision/details",
        "https://accounts.chriskendall.media/sip/autoprovision",
        "https://accounts.chriskendall.media/index.php/sip/autoprovision",
        "https://accounts.chriskendall.media/sip/provision",
        "https://accounts.chriskendall.media/index.php/sip/provision"
    ];

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    public async Task<ProvisioningResult> ProvisionAsync(
        string code,
        string licenseKey,
        string licenseStatus,
        string licenseLocalKey,
        MediaDeviceInfo audioInput,
        MediaDeviceInfo audioOutput,
        CancellationToken cancellationToken = default)
    {
        var cleanedCode = new string(code.Where(char.IsDigit).ToArray());
        if (cleanedCode.Length != 8)
        {
            return ProvisioningResult.Fail("Enter the 8 digit provisioning code.");
        }

        try
        {
            ProvisioningResult? lastFailure = null;
            var maxDelay = TimeSpan.FromSeconds(30);
            var delay = TimeSpan.FromSeconds(1);

            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (attempt > 0)
                {
                    var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500));
                    await Task.Delay(delay + jitter, cancellationToken);
                    delay = delay * 2;
                    if (delay > maxDelay) delay = maxDelay;
                }

                var urls = ProvisioningUrlTemplates.Select(t => new Uri(t.Replace("{code}", cleanedCode))).ToArray();
                foreach (var url in urls)
                {
                    HttpResponseMessage response;
                    try
                    {
                        response = await HttpClient.PostAsJsonAsync(url, new ProvisioningRequest(cleanedCode), cancellationToken);
                    }
                    catch (HttpRequestException)
                    {
                        lastFailure = ProvisioningResult.Fail("The provisioning service is not available right now.");
                        continue; // try next URL or retry
                    }
                    
                    using (response)
                    {
                        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
                        var isJson = contentType.Contains("json", StringComparison.OrdinalIgnoreCase);

                        if (!isJson)
                        {
                            DebugLog.Write($"PROVISION endpoint returned non-json status={(int)response.StatusCode} url={url}");
                            lastFailure = ProvisioningResult.Fail("The provisioning service is not available right now.");
                            continue;
                        }

                        var payload = await response.Content.ReadFromJsonAsync<ProvisioningResponse>(cancellationToken: cancellationToken);
                        if (!response.IsSuccessStatusCode || payload is null || !payload.Success || payload.Sip is null)
                        {
                            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests || 
                                (int)response.StatusCode >= 500)
                            {
                                lastFailure = ProvisioningResult.Fail(ToCustomerMessage(payload?.Error, response.StatusCode));
                                break; // Break URL loop, go to outer retry loop
                            }
                            return ProvisioningResult.Fail(ToCustomerMessage(payload?.Error, response.StatusCode));
                        }

                        if (string.IsNullOrWhiteSpace(payload.Sip.Extension) ||
                            string.IsNullOrWhiteSpace(payload.Sip.AuthName) ||
                            string.IsNullOrWhiteSpace(payload.Sip.SipPassword))
                        {
                            return ProvisioningResult.Fail("The provisioning code did not return complete account details.");
                        }

                        var config = new AppStartupConfig(
                            AppStartupConfig.FixedSipServer,
                            AppStartupConfig.FixedSipPort,
                            AppStartupConfig.FixedSipServer,
                            payload.Sip.Extension.Trim(),
                            payload.Sip.AuthName.Trim(),
                            payload.Sip.SipPassword,
                            licenseKey,
                            licenseStatus,
                            audioInput,
                            audioOutput,
                            LicenseLocalKey: licenseLocalKey).WithFixedSipEndpoint();

                        return ProvisioningResult.Ok(config);
                    }
                }
            }

            return lastFailure ?? ProvisioningResult.Fail("The provisioning service is not available right now.");
        }
        catch (Exception error)
        {
            DebugLog.Write($"PROVISION failed error={error.Message}");
            return ProvisioningResult.Fail("Unable to provision the account right now.");
        }
    }

    private static string ToCustomerMessage(string? error, System.Net.HttpStatusCode statusCode)
    {
        if (string.Equals(error, "rate_limited", StringComparison.OrdinalIgnoreCase))
        {
            return "Too many attempts. Please wait a few minutes and try again.";
        }

        if (string.Equals(error, "invalid_or_expired_code", StringComparison.OrdinalIgnoreCase) ||
            statusCode is System.Net.HttpStatusCode.Unauthorized)
        {
            return "The provisioning code is invalid or has expired.";
        }

        return "The provisioning code could not be accepted.";
    }

    private sealed record ProvisioningRequest([property: JsonPropertyName("code")] string Code);

    private sealed record ProvisioningResponse(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("sip")] ProvisionedSipDetails? Sip,
        [property: JsonPropertyName("error")] string? Error);

    private sealed record ProvisionedSipDetails(
        [property: JsonPropertyName("extension")] string Extension,
        [property: JsonPropertyName("auth_name")] string AuthName,
        [property: JsonPropertyName("sip_password")] string SipPassword);
}

public sealed record ProvisioningResult(bool Success, AppStartupConfig? Config, string Message)
{
    public static ProvisioningResult Ok(AppStartupConfig config) => new(true, config, "Account provisioned.");

    public static ProvisioningResult Fail(string message) => new(false, null, message);
}
