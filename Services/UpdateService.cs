using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace EndfieldGraph.Services;

public interface IUpdateService
{
    Task CheckForUpdatesAsync(CancellationToken ct = default);
}

public sealed record GitHubRelease(
    [property: JsonPropertyName("tag_name")] string Tag,
    [property: JsonPropertyName("assets")] GitHubAsset[] Assets
);

public sealed record GitHubAsset(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl
);

public sealed class GitHubUpdateService(IContentDialogService dialog) : IUpdateService
{
    private readonly IContentDialogService dialog = dialog;

    private const string Owner = "AkiEvansDev";
    private const string Repo = "EndfieldGraph";
    private const string AssetName = "EndfieldGraph.exe";

    public async Task CheckForUpdatesAsync(CancellationToken ct = default)
    {
        if (!NetworkInterface.GetIsNetworkAvailable())
            return;

        try
        {
            var current = VersionUtil.GetCurrentVersion();

            var (latest, downloadUrl) = await GetLatestFromGitHubAsync(ct);
            if (latest is null)
                return;

            if (latest <= current)
                return;

            var result = await dialog.ShowAsync(
                new ContentDialog
                {
                    Title = "Update available!",
                    Content = $"The v{latest.Major}.{latest.Minor} version is available. You have the v{current.Major}.{current.Minor} version.\nDownload?",
                    PrimaryButtonText = "Download",
                    CloseButtonText = "Later"
                },
                ct
            );

            if (result == ContentDialogResult.Primary)
            {
                var url = downloadUrl ?? $"https://github.com/{Owner}/{Repo}/releases/latest";
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
        }
        catch { }
    }

    private static async Task<(Version? latest, string? downloadUrl)> GetLatestFromGitHubAsync(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(Repo, "1.0"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        var api = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
        var json = await http.GetStringAsync(api, ct);

        var release = JsonSerializer.Deserialize<GitHubRelease>(json);
        if (release is null || !VersionUtil.TryParseTag(release.Tag, out var latest))
            return (null, null);

        var asset = release.Assets?.FirstOrDefault(a =>
            string.Equals(a.Name, AssetName, StringComparison.OrdinalIgnoreCase));

        return (latest, asset?.BrowserDownloadUrl);
    }
}

public static class VersionUtil
{
    public static Version GetCurrentVersion()
        => typeof(VersionUtil).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);

    public static bool TryParseTag(string tag, out Version? version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(tag)) return false;

        var s = tag.Trim();
        if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            s = s[1..];

        var parts = s.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1) s = $"{parts[0]}.0.0";
        else if (parts.Length == 2) s = $"{parts[0]}.{parts[1]}.0";

        return Version.TryParse(s, out version);
    }
}
