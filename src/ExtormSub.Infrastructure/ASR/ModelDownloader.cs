using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using ExtormSub.Core.ASR;

namespace ExtormSub.Infrastructure.ASR;

public readonly record struct DownloadProgress(long Received, long? Total, double BytesPerSecond)
{
    public double? Fraction => Total is > 0 ? (double)Received / Total.Value : null;
}

public sealed class ChecksumMismatchException(string file) :
    IOException($"{Path.GetFileName(file)} failed its integrity check (SHA-256 mismatch) and was deleted. Try downloading again.");

/// <summary>
/// Resumable, verified downloads. Data goes to "&lt;target&gt;.part"; an interrupted download resumes with an
/// HTTP Range request next time. The SHA-256 of the complete file is checked before it is renamed into
/// place, so a partial or corrupted file never looks valid. A checksum mismatch deletes the partial file.
/// </summary>
public sealed class FileDownloader(HttpClient http)
{
    public static string PartPath(string target) => target + ".part";

    public static long PartialBytes(string target)
    {
        var f = new FileInfo(PartPath(target));
        return f.Exists ? f.Length : 0;
    }

    public async Task DownloadAsync(Uri uri, string target, string? sha256, long? expectedBytes,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
        var part = PartPath(target);
        long existing = PartialBytes(target);
        if (expectedBytes is { } exp && existing > exp) { File.Delete(part); existing = 0; }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (existing > 0) await HashExistingAsync(part, hash, ct).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        bool append = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (existing > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // The partial file is already complete.
            Finish(part, target, hash, sha256);
            return;
        }
        response.EnsureSuccessStatusCode();
        if (!append && existing > 0)
        {
            // Server ignored the range: start over.
            existing = 0;
            hash.GetHashAndReset();
        }

        long? total = response.Content.Headers.ContentLength is { } len ? len + existing : expectedBytes;
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(target))!);
        if (total is { } t && drive.AvailableFreeSpace < t - existing + (200L << 20))
            throw new IOException($"Not enough disk space on {drive.Name} for {Path.GetFileName(target)}.");

        await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var file = new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
        {
            var buffer = new byte[1 << 20];
            long received = existing;
            var sw = Stopwatch.StartNew();
            var lastReport = TimeSpan.Zero;
            int n;
            while ((n = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                hash.AppendData(buffer, 0, n);
                received += n;
                if (sw.Elapsed - lastReport > TimeSpan.FromMilliseconds(200))
                {
                    lastReport = sw.Elapsed;
                    progress?.Report(new DownloadProgress(received, total, (received - existing) / Math.Max(0.001, sw.Elapsed.TotalSeconds)));
                }
            }
            if (total is { } expected && received != expected)
                throw new IOException($"Download interrupted at {received:N0} of {expected:N0} bytes. It will resume next time.");
            progress?.Report(new DownloadProgress(received, total, 0));
        }
        Finish(part, target, hash, sha256);
    }

    private static void Finish(string part, string target, IncrementalHash hash, string? sha256)
    {
        var actual = Convert.ToHexString(hash.GetHashAndReset());
        if (sha256 is not null && !actual.Equals(sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(part);
            throw new ChecksumMismatchException(target);
        }
        File.Move(part, target, overwrite: true);
    }

    private static async Task HashExistingAsync(string path, IncrementalHash hash, CancellationToken ct)
    {
        await using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        var buffer = new byte[1 << 20];
        int n;
        while ((n = await f.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0) hash.AppendData(buffer, 0, n);
    }

    public static async Task<string> Sha256Async(string path, CancellationToken ct = default)
    {
        await using var f = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(f, ct).ConfigureAwait(false));
    }
}

/// <summary>GGML model files in the models folder, downloaded with <see cref="FileDownloader"/>.</summary>
public sealed class ModelDownloader(HttpClient http)
{
    private readonly FileDownloader _files = new(http);

    public static string PathFor(string directory, WhisperModel model) => Path.Combine(directory, model.FileName);

    public static bool IsDownloaded(string directory, WhisperModel model) => File.Exists(PathFor(directory, model));

    /// <summary>Bytes already present from an interrupted download (0 if none).</summary>
    public static long PartialBytes(string directory, WhisperModel model) => FileDownloader.PartialBytes(PathFor(directory, model));

    public Task DownloadAsync(WhisperModel model, string directory, IProgress<DownloadProgress>? progress, CancellationToken ct) =>
        _files.DownloadAsync(model.DownloadUri, PathFor(directory, model), model.Sha256, model.Bytes, progress, ct);

    /// <summary>Re-hashes a downloaded model. Returns false (and nothing else) when it does not match.</summary>
    public static async Task<bool> VerifyAsync(string directory, WhisperModel model, CancellationToken ct = default) =>
        model.Sha256 is null || (await FileDownloader.Sha256Async(PathFor(directory, model), ct).ConfigureAwait(false))
            .Equals(model.Sha256, StringComparison.OrdinalIgnoreCase);

    public static void Delete(string directory, WhisperModel model)
    {
        File.Delete(PathFor(directory, model));
        File.Delete(FileDownloader.PartPath(PathFor(directory, model)));
    }
}
