using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using MerlinSip.Models;

namespace MerlinSip.Services;

public sealed class ProvisioningService
{
    private const string ProvisioningUrl = "https://dev.chriskendall.media/sip/provision";
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    public async Task<ProvisioningResult> ProvisionAsync(
        string code,
        string licenseKey,
        string licenseStatus,
        MediaDeviceInfo audioInput,
        MediaDeviceInfo audioOutput,
        MediaDeviceInfo videoSource,
        CancellationToken cancellationToken = default)
    {
        var cleanedCode = new string(code.Where(char.IsDigit).ToArray());
        if (cleanedCode.Length != 8)
        {
            return ProvisioningResult.Fail("Enter the 8 digit provisioning code.");
        }

        try
        {
            using var response = await HttpClient.PostAsJsonAsync(
                ProvisioningUrl,
                new ProvisioningRequest(cleanedCode, LicenseService.ProductId),
                cancellationToken);
            var payload = await response.Content.ReadFromJsonAsync<ProvisioningResponse>(cancellationToken: cancellationToken);

            if (!response.IsSuccessStatusCode || payload is null || !payload.Success || payload.Sip is null)
            {
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
                videoSource).WithFixedSipEndpoint();

            return ProvisioningResult.Ok(config);
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

    private sealed record ProvisioningRequest(
        [property: JsonPropertyName("code")] string Code,
        [property: JsonPropertyName("product_id")] string ProductId);

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
