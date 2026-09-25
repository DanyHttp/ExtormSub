using System.Diagnostics;
using ExtormSub.Infrastructure.ASR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExtormSub.Infrastructure.Translation;

/// <summary>
/// Self-hosted LibreTranslate in a private Python environment under %LocalAppData%\ExtormSub\libretranslate.
/// Language models also live there (XDG_* dirs), so Remove deletes everything.
/// </summary>
public sealed class LibreTranslateServer(AppPaths paths, ILogger<LibreTranslateServer>? log = null) : IDisposable
{
    public const string PackageSpec = "libretranslate";
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(20); // first start downloads language models
    private readonly ILogger _log = (ILogger?)log ?? NullLogger.Instance;
    private readonly HttpClient _probe = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private string? _languages;

    public string Root => Path.Combine(paths.Local, "libretranslate");
    public string EnvDirectory => Path.Combine(Root, "env");
    private string PythonExe => Path.Combine(EnvDirectory, "Scripts", "python.exe");
    private string ServerExe => Path.Combine(EnvDirectory, "Scripts", "libretranslate.exe");
    private string Marker => Path.Combine(EnvDirectory, "extormsub-ready.txt");

    public bool IsInstalled => File.Exists(ServerExe) && File.Exists(Marker);
    public bool IsRunning => _process is { HasExited: false };

    public async Task SetupAsync(string? pythonPath, IProgress<string>? progress, CancellationToken ct)
    {
        var python = await FasterWhisperEnvironment.FindPythonAsync(pythonPath, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Python 3.9 or newer was not found. Install it from python.org, or choose python.exe in Speech Recognition settings.");
        progress?.Report($"Using {python}");

        Stop();
        if (Directory.Exists(EnvDirectory)) Directory.Delete(EnvDirectory, recursive: true);
        await FasterWhisperEnvironment.RunAsync(python, ["-m", "venv", EnvDirectory], progress, ct).ConfigureAwait(false);
        await FasterWhisperEnvironment.RunAsync(PythonExe, ["-m", "pip", "install", "--disable-pip-version-check", "--upgrade", "pip"], progress, ct).ConfigureAwait(false);
        await FasterWhisperEnvironment.RunAsync(PythonExe, ["-m", "pip", "install", "--disable-pip-version-check", PackageSpec], progress, ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(Marker, $"{PackageSpec} {DateTime.Now:O}", ct).ConfigureAwait(false);
        _log.LogInformation("LibreTranslate environment ready");
    }

    /// <summary>
    /// Starts the server on <paramref name="baseUrl"/>'s port with only <paramref name="languages"/> (e.g. "en,fa") loaded,
    /// and waits until it answers. Reuses anything already answering there. Restarts our own server if the languages changed.
    /// </summary>
    public async Task EnsureStartedAsync(Uri baseUrl, string languages, IProgress<string>? progress, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsRunning && _languages != languages) Stop();
            if (await RespondsAsync(baseUrl, ct).ConfigureAwait(false)) return;
            if (!IsInstalled) throw new InvalidOperationException("LibreTranslate is not installed. Install it in Settings → Translation.");

            Stop();
            progress?.Report($"Starting LibreTranslate ({languages}). The first start downloads the language models…");
            _log.LogInformation("Starting LibreTranslate on port {Port} with {Languages}", baseUrl.Port, languages);
            var p = FasterWhisperEnvironment.Start(ServerExe,
                ["--host", "127.0.0.1", "--port", baseUrl.Port.ToString(), "--load-only", languages], env: new Dictionary<string, string>
                {
                    ["XDG_DATA_HOME"] = Path.Combine(Root, "data"),
                    ["XDG_CACHE_HOME"] = Path.Combine(Root, "cache"),
                    ["XDG_CONFIG_HOME"] = Path.Combine(Root, "config"),
                });
            _process = p;
            _languages = languages;
            p.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) _log.LogDebug("libretranslate: {Line}", e.Data); };
            p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) _log.LogDebug("libretranslate: {Line}", e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            var deadline = DateTime.UtcNow + StartupTimeout;
            while (!await RespondsAsync(baseUrl, ct).ConfigureAwait(false))
            {
                if (p.HasExited) throw new InvalidOperationException($"LibreTranslate exited (code {p.ExitCode}). Check the language codes and the log.");
                if (DateTime.UtcNow > deadline) { Stop(); throw new TimeoutException("LibreTranslate did not start within 20 minutes."); }
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
            progress?.Report("LibreTranslate is running.");
            _log.LogInformation("LibreTranslate is running");
        }
        finally { _gate.Release(); }
    }

    private async Task<bool> RespondsAsync(Uri baseUrl, CancellationToken ct)
    {
        try { return (await _probe.GetAsync(new Uri(baseUrl, "languages"), ct).ConfigureAwait(false)).IsSuccessStatusCode; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { return false; }
    }

    // ponytail: if ExtormSub crashes the server keeps running; the next start reuses it. A Job object would kill it with us.
    public void Stop()
    {
        var p = _process;
        _process = null;
        if (p is null) return;
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        p.Dispose();
    }

    public void Remove()
    {
        Stop();
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }

    public void Dispose()
    {
        Stop();
        _probe.Dispose();
    }
}
