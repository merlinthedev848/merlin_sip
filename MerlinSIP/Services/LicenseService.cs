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

#if DEBUG
    private const string TestLicenseKey = "TEST-BFC2-DF38-F81D-F08E-135A-9058";
#endif
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

#if DEBUG
        if (string.Equals(licenseKey, TestLicenseKey, StringComparison.OrdinalIgnoreCase))
        {
            Status = "Licensed";
            LocalKey = null;
            return LicenseActivationResult.Ok(Status);
        }
#endif

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

    public async Task<LicenseVerificationResult> VerifyAsync(string token, string? localKey = null, CancellationToken cancellationToken = default)
    {
        var licenseKey = token?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(licenseKey))
        {
            return LicenseVerificationResult.Inactive("No licence key is saved.");
        }

#if DEBUG
        if (string.Equals(licenseKey, TestLicenseKey, StringComparison.OrdinalIgnoreCase))
        {
            Status = "Licensed to CK Media Services";
            LocalKey = null;
            return LicenseVerificationResult.Valid(Status, "CK Media Services");
        }
#endif

        try
        {
            var verify = await PostLicenseAsync(VerifyUrl, licenseKey, cancellationToken, localKey);
            if (!IsAccepted(verify))
            {
                return LicenseVerificationResult.Inactive(ToCustomerMessage(verify));
            }

            Status = BuildStatus(verify);
            LocalKey = verify.LocalKey ?? localKey;
            return LicenseVerificationResult.Valid(Status, GetLicenseeDisplay(verify));
        }
        catch (Exception error)
        {
            DebugLog.Write($"LICENSE verification failed error={error.Message}");
            return LicenseVerificationResult.Unchecked("Unable to verify the licence right now.");
        }
    }

    private static async Task<LicenseResponse> PostLicenseAsync(Uri url, string licenseKey, CancellationToken cancellationToken, string? localKey = null)
    {
        using var response = await HttpClient.PostAsJsonAsync(url, new LicenseRequest(
            licenseKey,
            ProductId,
            BuildMachineId(),
            GetSoftwareVersion(),
            localKey), cancellationToken);

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
        var licensee = GetLicenseeDisplay(response);
        return string.IsNullOrWhiteSpace(licensee)
            ? "Licensed"
            : $"Licensed to {licensee}";
    }

    private static string GetLicenseeDisplay(LicenseResponse response)
    {
        if (!string.IsNullOrWhiteSpace(response.Licensee?.Company))
        {
            return response.Licensee.Company;
        }

        if (!string.IsNullOrWhiteSpace(response.Licensee?.Name))
        {
            return response.Licensee.Name;
        }

        if (!string.IsNullOrWhiteSpace(response.Buyer))
        {
            return response.Buyer;
        }

        return string.Empty;
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
        return version is null ? "unknown" : version.Revision > 0
            ? $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}"
            : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    private sealed record LicenseRequest(
        [property: JsonPropertyName("license_key")] string LicenseKey,
        [property: JsonPropertyName("product_id")] string ProductId,
        [property: JsonPropertyName("machine_id")] string MachineId,
        [property: JsonPropertyName("software_version")] string SoftwareVersion,
        [property: JsonPropertyName("local_key")] string? LocalKey = null);

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

public sealed record LicenseVerificationResult(bool Checked, bool Active, string Message, string Licensee)
{
    public static LicenseVerificationResult Valid(string message, string licensee) => new(true, true, message, licensee);

    public static LicenseVerificationResult Inactive(string message) => new(true, false, message, "");

    public static LicenseVerificationResult Unchecked(string message) => new(false, true, message, "");
}
