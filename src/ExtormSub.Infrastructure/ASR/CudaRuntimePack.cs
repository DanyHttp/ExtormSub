using System.IO.Compression;
using System.Runtime.InteropServices;

namespace ExtormSub.Infrastructure.ASR;

/// <summary>
/// Optional NVIDIA acceleration pack, downloaded on demand instead of bundled (it is ~800 MB):
/// whisper.cpp's CUDA 12 build (Whisper.net.Runtime.Cuda12.Windows) plus NVIDIA's redistributable
/// cudart/cuBLAS, which are skipped when a CUDA 12 toolkit is already installed. Every archive is
/// pinned by SHA-256 and downloads resume. Installed under %LocalAppData%\ExtormSub\gpu, where
/// Whisper.net's loader finds it via RuntimeOptions.LibraryPath.
/// </summary>
public sealed class CudaRuntimePack(HttpClient http, AppPaths paths)
{
    private sealed record Artifact(string Name, Uri Url, string Sha256, long Bytes, string EntryPrefix, string[]? Dlls);

    private static readonly Artifact WhisperCuda = new("whisper-cuda12.nupkg",
        new("https://api.nuget.org/v3-flatcontainer/whisper.net.runtime.cuda12.windows/1.9.1/whisper.net.runtime.cuda12.windows.1.9.1.nupkg"),
        "eb8d4233c202fe948cace50e3fa08fec7beead699db86a17df9820d03180a30c", 250_058_371, "build/win-x64/", null);

    private static readonly Artifact CudaRuntime = new("cuda-runtime.whl",
        new("https://files.pythonhosted.org/packages/59/df/e7c3a360be4f7b93cee39271b792669baeb3846c58a4df6dfcf187a7ffab/nvidia_cuda_runtime_cu12-12.9.79-py3-none-win_amd64.whl"),
        "8e018af8fa02363876860388bd10ccb89eb9ab8fb0aa749aaf58430a9f7c4891", 3_591_604, "nvidia/cuda_runtime/bin/", ["cudart64_12.dll"]);

    private static readonly Artifact Cublas = new("cublas.whl",
        new("https://files.pythonhosted.org/packages/20/e2/fc9a0e985249d873150276d5afb02e39a66817fedbf1a385724393e505ed/nvidia_cublas_cu12-12.9.2.10-py3-none-win_amd64.whl"),
        "623f43027d40d44ceadf0043f002bd25cf353e8f13ce90b9a87057019f560661", 553_162_896, "nvidia/cublas/bin/", ["cublas64_12.dll", "cublasLt64_12.dll"]);

    private readonly FileDownloader _downloader = new(http);

    public string Root => Path.Combine(paths.Local, "gpu");
    public string RuntimeDirectory => Path.Combine(Root, "runtimes", "cuda12", "win-x64");

    public static bool NvidiaDriverPresent => CanLoad("nvcuda.dll");

    /// <summary>A CUDA 12 toolkit/runtime is already on PATH, so NVIDIA's libraries need not be downloaded.</summary>
    public static bool SystemHasCudaRuntime => CanLoad("cudart64_12.dll") && CanLoad("cublas64_12.dll");

    public bool IsInstalled =>
        File.Exists(Path.Combine(RuntimeDirectory, "whisper.dll")) &&
        File.Exists(Path.Combine(RuntimeDirectory, "ggml-cuda-whisper.dll")) &&
        (File.Exists(Path.Combine(RuntimeDirectory, "cublas64_12.dll")) || SystemHasCudaRuntime);

    public long DownloadBytes => WhisperCuda.Bytes + (SystemHasCudaRuntime ? 0 : CudaRuntime.Bytes + Cublas.Bytes);

    /// <summary>Downloads (resumably) and extracts the pack. Progress reports the stage and bytes of the current file.</summary>
    public async Task InstallAsync(IProgress<(string Stage, DownloadProgress Progress)>? progress, CancellationToken ct)
    {
        var artifacts = SystemHasCudaRuntime ? new[] { WhisperCuda } : [WhisperCuda, CudaRuntime, Cublas];
        Directory.CreateDirectory(RuntimeDirectory);
        foreach (var (a, i) in artifacts.Select((a, i) => (a, i + 1)))
        {
            var stage = $"Downloading {i}/{artifacts.Length}";
            var archive = Path.Combine(Root, "downloads", a.Name);
            var fileProgress = progress is null ? null : new Progress<DownloadProgress>(p => progress.Report((stage, p)));
            if (!File.Exists(archive))
                await _downloader.DownloadAsync(a.Url, archive, a.Sha256, a.Bytes, fileProgress, ct).ConfigureAwait(false);
            progress?.Report(($"Extracting {i}/{artifacts.Length}", new DownloadProgress(a.Bytes, a.Bytes, 0)));
            await Task.Run(() => Extract(a, archive), ct).ConfigureAwait(false);
            File.Delete(archive);
        }
    }

    private void Extract(Artifact a, string archive)
    {
        using var zip = ZipFile.OpenRead(archive);
        foreach (var entry in zip.Entries)
        {
            if (!entry.FullName.StartsWith(a.EntryPrefix, StringComparison.OrdinalIgnoreCase) ||
                !entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
            if (a.Dlls is not null && !a.Dlls.Contains(entry.Name, StringComparer.OrdinalIgnoreCase)) continue;
            var target = Path.Combine(RuntimeDirectory, entry.Name);
            entry.ExtractToFile(target + ".tmp", overwrite: true);
            File.Move(target + ".tmp", target, overwrite: true);
        }
    }

    /// <summary>Removes the pack. Takes effect after restart if the CUDA runtime is currently loaded.</summary>
    public void Uninstall()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }

    /// <summary>
    /// Makes the pack visible to Whisper.net's loader. Must run before the first model load: points
    /// RuntimeOptions.LibraryPath at the pack and puts it on PATH so ggml-cuda resolves cudart/cuBLAS.
    /// </summary>
    public void Activate()
    {
        Whisper.net.LibraryLoader.RuntimeOptions.LibraryPath = Path.Combine(Root, "whisper.dll");
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        if (!path.Contains(RuntimeDirectory, StringComparison.OrdinalIgnoreCase))
            Environment.SetEnvironmentVariable("PATH", RuntimeDirectory + ";" + path);
    }

    private static bool CanLoad(string library)
    {
        if (!NativeLibrary.TryLoad(library, out var handle)) return false;
        NativeLibrary.Free(handle);
        return true;
    }
}
