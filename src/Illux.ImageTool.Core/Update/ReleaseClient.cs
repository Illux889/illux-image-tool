using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Illux.ImageTool.Update;

/// <summary>The latest full release: its version and where to fetch the installer and its checksum.</summary>
public sealed record ReleaseInfo(Version Version, Uri InstallerUrl, Uri ChecksumUrl);

/// <summary>Asks the public GitHub API (no key) for the latest full release of the project.</summary>
public sealed partial class ReleaseClient(HttpClient http)
{
    public const string LatestReleaseUrl = "https://api.github.com/repos/Illux889/illux-image-tool/releases/latest";

    private static readonly string AppVersion =
        typeof(ReleaseClient).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    [GeneratedRegex(@"^v(\d+)\.(\d+)\.(\d+)$")]
    private static partial Regex TagPattern();

    /// <summary>
    /// The latest release, or null when there is none, it is a draft/pre-release, the tag is not <c>vX.Y.Z</c>, or the
    /// installer or checksum asset is missing. Network and HTTP errors throw (<see cref="HttpRequestException"/>).
    /// </summary>
    public async Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
        request.Headers.UserAgent.ParseAdd($"IlluxImageTool/{AppVersion}");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null; // no release published yet
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return Parse(doc.RootElement);
    }

    private static ReleaseInfo? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (IsTrue(root, "draft") || IsTrue(root, "prerelease")) return null;

        if (!root.TryGetProperty("tag_name", out var tagElement) || tagElement.ValueKind != JsonValueKind.String) return null;
        var match = TagPattern().Match(tagElement.GetString()!);
        if (!match.Success) return null;
        if (!int.TryParse(match.Groups[1].Value, out var major)
            || !int.TryParse(match.Groups[2].Value, out var minor)
            || !int.TryParse(match.Groups[3].Value, out var patch)) return null;

        var version = new Version(major, minor, patch);
        var installerName = $"IlluxImageTool-Setup-{version}.exe";
        var installer = FindAsset(root, installerName);
        var checksum = FindAsset(root, installerName + ".sha256");
        return installer is null || checksum is null ? null : new ReleaseInfo(version, installer, checksum);
    }

    private static bool IsTrue(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static Uri? FindAsset(JsonElement release, string name)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object) continue;
            if (!asset.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String || n.GetString() != name) continue;
            if (!asset.TryGetProperty("browser_download_url", out var u) || u.ValueKind != JsonValueKind.String) continue;
            // The installer is run with elevated rights, so only GitHub's own https hosts are accepted.
            if (Uri.TryCreate(u.GetString(), UriKind.Absolute, out var uri) && IsTrustedDownload(uri)) return uri;
        }
        return null;
    }

    internal static bool IsTrustedDownload(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps
        && string.IsNullOrEmpty(uri.UserInfo)
        && (uri.Host == "github.com" || uri.Host.EndsWith(".githubusercontent.com", StringComparison.Ordinal));
}
