using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using ExtormSub.Infrastructure.ASR;
using ExtormSub.Infrastructure.Security;

namespace ExtormSub.Infrastructure.Updates;

public sealed record UpdateInfo(Version Version, Uri Url, string Sha256, long? Bytes);

/// <summary>
/// Checks a small JSON manifest (written next to the installer by build\build-installer.ps1):
/// <c>{"version":"0.3.0","url":"ExtormSub-Setup-0.3.0.exe","sha256":"…","size":123}</c>.
/// A relative "url" resolves against the manifest URL, so both files are uploaded to the same place.
/// </summary>
public sealed class UpdateChecker(HttpClient http, Uri manifestUrl)
{
    public async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, manifestUrl);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var info = ParseManifest(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false), manifestUrl);
        return info.Version > Normalize(current) ? info : null;
    }

    public static UpdateInfo ParseManifest(string json, Uri manifestUrl)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var version = Normalize(Version.Parse(root.GetProperty("version").GetString()!));
            var url = new Uri(manifestUrl, root.GetProperty("url").GetString()!);
            var sha = root.GetProperty("sha256").GetString()!;
            long? size = root.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : null;
            if (url.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException("The update URL is not HTTPS.");
            if (sha.Length != 64 || !sha.All(Uri.IsHexDigit)) throw new InvalidDataException("The update checksum is malformed.");
            return new UpdateInfo(version, url, sha, size);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException
                                       or FormatException or ArgumentException or OverflowException or UriFormatException)
        {
            throw new InvalidDataException("The update manifest is malformed.", ex);
        }
    }

    /// <summary>"0.3.0" and "0.3.0.0" are the same version (Version treats a missing part as lower).</summary>
    public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

    /// <summary>
    /// Downloads (resumably) and verifies the installer: SHA-256 from the manifest, and — when this build is
    /// signed — a valid signature from the same publisher, so a compromised download host cannot push its own code.
    /// </summary>
    public async Task<string> DownloadAsync(UpdateInfo info, string directory, string currentExe,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var target = Path.Combine(directory, $"ExtormSub-Setup-{info.Version.ToString(3)}.exe");
        if (!File.Exists(target))
            await new FileDownloader(http).DownloadAsync(info.Url, target, info.Sha256, info.Bytes, progress, ct).ConfigureAwait(false);
        var expected = Authenticode.TrustedSigner(currentExe);
        if (expected is not null && Authenticode.TrustedSigner(target) != expected)
        {
            File.Delete(target);
            throw new InvalidDataException("The downloaded update is not signed by the ExtormSub publisher, so it was deleted.");
        }
        return target;
    }

    /// <summary>
    /// Runs the installer silently. It reuses the existing install mode (per-user or all users, asking for elevation
    /// itself when needed), closes this app through "--exit" and starts it again afterwards (/RELAUNCH=1).
    /// </summary>
    public static void Launch(string installer) =>
        Process.Start(new ProcessStartInfo(installer, "/SILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH=1") { UseShellExecute = true })?.Dispose();
}
