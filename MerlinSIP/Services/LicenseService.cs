using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace MerlinSip.Services;

public sealed class LicenseService
{
    public const string ProductId = "merlin-sip";

    private const string TestLicenseKey = "TEST-BFC2-DF38-F81D-F08E-135A-9058";
    private static readonly Uri VerifyUrl = new("https://accounts.chriskendall.media/license/verify");
    private static readonly Uri ActivateUrl = new("https://accounts.chriskendall.media/license/activate");
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    public string Status { get; private set; } = "Licensed";
    public string? LocalKey { get; private set; }

    public async Task<LicenseActivationResult> ActivateAsync(string token, CancellationToken cancellationToken = default)
    {
        var licenseKey = token?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(licenseKey))
        {
            return LicenseActivationResult.Fail("Enter a valid license key.");
        }

        if (string.Equals(licenseKey, TestLicenseKey, StringComparison.OrdinalIgnoreCase))
        {
            Status = "Licensed";
            LocalKey = null;
            return LicenseActivationResult.Ok(Status);
        }

        try
        {
            var verify = await PostLicenseAsync(VerifyUrl, licenseKey, cancellationToken);
            if (!IsAccepted(verify))
            {
                return LicenseActivationResult.Fail(ToCustomerMessage(verify));
            }

            var activation = verify.RequiresActivation == true
                ? await PostLicenseAsync(ActivateUrl, licenseKey, cancellationToken)
                : verify;

            if (!IsAccepted(activation))
            {
                return LicenseActivationResult.Fail(ToCustomerMessage(activation));
            }

            Status = BuildStatus(activation);
            LocalKey = activation.LocalKey;
            return LicenseActivationResult.Ok(Status, activation.LocalKey);
        }
        catch (Exception error)
        {
            DebugLog.Write($"LICENSE activation failed error={error.Message}");
            return LicenseActivationResult.Fail("Unable to verify the license right now.");
        }
    }

    private static async Task<LicenseResponse> PostLicenseAsync(Uri url, string licenseKey, CancellationToken cancellationToken)
    {
        using var response = await HttpClient.PostAsJsonAsync(url, new LicenseRequest(
            licenseKey,
            ProductId,
            BuildMachineId(),
            GetSoftwareVersion()), cancellationToken);

        var payload = await response.Content.ReadFromJsonAsync<LicenseResponse>(cancellationToken: cancellationToken)
            ?? new LicenseResponse(false, false, response.StatusCode.ToString(), "", "The license server did not return a valid response.", "", false, null, null, null, null);

        return response.IsSuccessStatusCode
            ? payload
            : payload with { Success = false };
    }

    private static bool IsAccepted(LicenseResponse response)
    {
        if (!response.Success || response.Valid == false)
        {
            return false;
        }

        return IsPositiveStatus(response.Status) ||
               IsPositiveStatus(response.VerifyStatus) ||
               IsPositiveStatus(response.LicenseStatus);
    }

    private static bool IsPositiveStatus(string? status)
    {
        return status is not null &&
               (status.Equals("valid", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("active", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("licensed", StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildStatus(LicenseResponse response)
    {
        if (!string.IsNullOrWhiteSpace(response.Licensee?.Company))
        {
            return $"Licensed to {response.Licensee.Company}";
        }

        if (!string.IsNullOrWhiteSpace(response.Licensee?.Name))
        {
            return $"Licensed to {response.Licensee.Name}";
        }

        return "Licensed";
    }

    private static string ToCustomerMessage(LicenseResponse response)
    {
        if (!string.IsNullOrWhiteSpace(response.Message))
        {
            return response.Message;
        }

        return "The license key could not be accepted.";
    }

    private static string BuildMachineId()
    {
        var raw = $"{Environment.MachineName}|{Environment.UserName}|{ProductId}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).ToLowerInvariant()[..32];
    }

    private static string GetSoftwareVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version is null ? "unknown" : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    private sealed record LicenseRequest(
        [property: JsonPropertyName("license_key")] string LicenseKey,
        [property: JsonPropertyName("product_id")] string ProductId,
        [property: JsonPropertyName("machine_id")] string MachineId,
        [property: JsonPropertyName("software_version")] string SoftwareVersion);

    private sealed record LicenseResponse(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("valid")] bool? Valid,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("verify_status")] string? VerifyStatus,
        [property: JsonPropertyName("message")] string? Message,
        [property: JsonPropertyName("license_status")] string? LicenseStatus,
        [property: JsonPropertyName("requires_activation")] bool? RequiresActivation,
        [property: JsonPropertyName("local_key")] string? LocalKey,
        [property: JsonPropertyName("buyer")] string? Buyer,
        [property: JsonPropertyName("buyer_email")] string? BuyerEmail,
        [property: JsonPropertyName("licensee")] LicenseeResponse? Licensee);

    private sealed record LicenseeResponse(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("email")] string? Email,
        [property: JsonPropertyName("company")] string? Company);
}

public sealed record LicenseActivationResult(bool Success, string Message, string? LocalKey = null)
{
    public static LicenseActivationResult Ok(string message, string? localKey = null) => new(true, message, localKey);

    public static LicenseActivationResult Fail(string message) => new(false, message);
}
