using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using MerlinSip.Models;

namespace MerlinSip.Services;

public sealed class UpdateService
{
    private const string UpdatesBaseUrl = "https://updates.chriskendall.media/merlin-sip/";
    private const string ReleasesUrl = "https://updates.chriskendall.media/merlin-sip/releases/";
    private static readonly TimeSpan InstallerRetention = TimeSpan.FromDays(14);
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 5);
            var latestFromFeed = await TryLoadLatestReleaseAsync(cancellationToken);
            if (latestFromFeed is not null)
            {
                return BuildResult(current, latestFromFeed);
            }

            var html = await HttpClient.GetStringAsync(ReleasesUrl, cancellationToken);
            var releases = FindMsiReleases(html).ToList();
            if (releases.Count == 0)
            {
                return new UpdateCheckResult(false, false, "No installer releases were found.");
            }

            return BuildResult(current, releases.OrderByDescending(release => release.Version).First());
        }
        catch (Exception error)
        {
            DebugLog.Write($"UPDATE CHECK failed error={error.Message}");
            return new UpdateCheckResult(false, false, "Unable to check for updates right now.");
        }
    }

    public async Task<string> DownloadInstallerAsync(UpdateCheckResult update, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(update.DownloadUrl))
        {
            throw new InvalidOperationException("No update download URL is available.");
        }

        var updateDir = Path.Combine(Path.GetTempPath(), "MerlinSIP", "Updates");
        Directory.CreateDirectory(updateDir);
        CleanupOldInstallerDownloads(updateDir);
        var versionPart = string.IsNullOrWhiteSpace(update.Version) ? "latest" : update.Version;
        var installerPath = Path.Combine(updateDir, $"MerlinSIP-{versionPart}.msi");

        using var response = await HttpClient.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = File.Create(installerPath);
        var buffer = new byte[81920];
        long readTotal = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            readTotal += read;
            if (totalBytes is > 0)
            {
                progress?.Report((int)Math.Clamp(readTotal * 100 / totalBytes.Value, 0, 100));
            }
        }

        progress?.Report(100);
        return installerPath;
    }

    private static void CleanupOldInstallerDownloads(string updateDir)
    {
        try
        {
            var cutoff = DateTimeOffset.Now.Subtract(InstallerRetention);
            foreach (var file in Directory.EnumerateFiles(updateDir, "MerlinSIP-*.msi"))
            {
                var info = new FileInfo(file);
                if (info.LastWriteTime < cutoff)
                {
                    info.Delete();
                }
            }
        }
        catch (Exception error)
        {
            DebugLog.Write($"UPDATE cleanup failed error={error.Message}");
        }
    }

    private static UpdateCheckResult BuildResult(Version current, MsiRelease latest)
    {
        var updateAvailable = latest.AuthoritativeFeed
            ? latest.Version != current
            : latest.Version > current;

        if (updateAvailable)
        {
            return new UpdateCheckResult(
                true,
                true,
                $"Version {latest.Version} is available.",
                latest.Version.ToString(),
                latest.Url,
                latest.Notes);
        }

        return new UpdateCheckResult(true, false, "You are up to date.", current.ToString());
    }

    private static async Task<MsiRelease?> TryLoadLatestReleaseAsync(CancellationToken cancellationToken)
    {
        foreach (var url in new[]
        {
            $"{UpdatesBaseUrl}latest.json",
            $"{UpdatesBaseUrl}update.json",
            UpdatesBaseUrl
        })
        {
            try
            {
                var json = await HttpClient.GetStringAsync(url, cancellationToken);
                var feed = JsonSerializer.Deserialize<UpdateFeed>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (feed is null ||
                    string.IsNullOrWhiteSpace(feed.Version) ||
                    string.IsNullOrWhiteSpace(feed.DownloadUrl) ||
                    !Version.TryParse(feed.Version, out var version))
                {
                    continue;
                }

                return new MsiRelease(version, feed.DownloadUrl, feed.Notes, true);
            }
            catch
            {
                // Try the next conventional feed location.
            }
        }

        return null;
    }

    private static IEnumerable<MsiRelease> FindMsiReleases(string html)
    {
        foreach (Match match in Regex.Matches(html, "href=[\"'](?<href>[^\"']+\\.msi)[\"']", RegexOptions.IgnoreCase))
        {
            var href = match.Groups["href"].Value;
            var fileName = Uri.UnescapeDataString(href.Split(['/', '\\']).Last());
            var version = ExtractVersion(fileName);
            if (version is null)
            {
                continue;
            }

            var url = Uri.TryCreate(href, UriKind.Absolute, out var absolute)
                ? absolute.ToString()
                : new Uri(new Uri(ReleasesUrl), href).ToString();

            yield return new MsiRelease(version, url, null, false);
        }
    }

    private static Version? ExtractVersion(string fileName)
    {
        var match = Regex.Match(fileName, @"(?<version>\d+\.\d+\.\d+(?:\.\d+)?)");
        return match.Success && Version.TryParse(match.Groups["version"].Value, out var version)
            ? version
            : null;
    }

    private sealed record MsiRelease(Version Version, string Url, string? Notes, bool AuthoritativeFeed);

    private sealed record UpdateFeed(string Version, string DownloadUrl, string? Notes);
}
