using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using ExtormSub.Core.ASR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExtormSub.Infrastructure.ASR;

/// <summary>
/// Private Python environment for faster-whisper under %LocalAppData%\ExtormSub\faster-whisper, so the
/// user's own Python installation is never modified. Created from any Python 3.9+ found on the PC.
/// </summary>
public sealed class FasterWhisperEnvironment(AppPaths paths, ILogger<FasterWhisperEnvironment>? log = null)
{
    public const string PackageSpec = "faster-whisper==1.2.1";
    private readonly ILogger _log = (ILogger?)log ?? NullLogger.Instance;

    public string Root => Path.Combine(paths.Local, "faster-whisper");
    public string EnvDirectory => Path.Combine(Root, "env");
    public string PythonExe => Path.Combine(EnvDirectory, "Scripts", "python.exe");
    public string ModelsDirectory => Path.Combine(Root, "models");
    private string Marker => Path.Combine(EnvDirectory, "extormsub-ready.txt");

    public static string SidecarScript => Path.Combine(AppContext.BaseDirectory, "Sidecar", "faster_whisper_server.py");

    public bool IsInstalled => File.Exists(PythonExe) && File.Exists(Marker);

    /// <summary>A usable Python 3.9+ (configured path, the py launcher, or python on PATH), or null.</summary>
    public static async Task<string?> FindPythonAsync(string? configured, CancellationToken ct = default)
    {
        var candidates = new List<(string Exe, string[] Args)>();
        if (!string.IsNullOrWhiteSpace(configured)) candidates.Add((configured, []));
        candidates.Add(("py", ["-3"]));
        candidates.Add(("python", []));
        foreach (var (exe, pre) in candidates)
        {
            var (code, output) = await RunCaptureAsync(exe, [.. pre, "-c",
                "import sys; print(sys.executable if sys.version_info >= (3, 9) else '')"], ct).ConfigureAwait(false);
            var path = output.Trim();
            // The WindowsApps "python.exe" stub opens the Store instead of running Python.
            if (code == 0 && path.Length > 0 && File.Exists(path) && !path.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
                return path;
        }
        return null;
    }

    /// <summary>Creates the venv and installs faster-whisper (plus NVIDIA CUDA libraries when <paramref name="cuda"/>).</summary>
    public async Task SetupAsync(string? pythonPath, bool cuda, IProgress<string>? progress, CancellationToken ct)
    {
        var python = await FindPythonAsync(pythonPath, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Python 3.9 or newer was not found. Install it from python.org, or choose python.exe in settings.");
        progress?.Report($"Using {python}");

        if (Directory.Exists(EnvDirectory)) Directory.Delete(EnvDirectory, recursive: true);
        await RunAsync(python, ["-m", "venv", EnvDirectory], progress, ct).ConfigureAwait(false);
        await RunAsync(PythonExe, ["-m", "pip", "install", "--disable-pip-version-check", "--upgrade", "pip"], progress, ct).ConfigureAwait(false);
        string[] packages = cuda ? [PackageSpec, "nvidia-cublas-cu12", "nvidia-cudnn-cu12==9.*"] : [PackageSpec];
        await RunAsync(PythonExe, ["-m", "pip", "install", "--disable-pip-version-check", .. packages], progress, ct).ConfigureAwait(false);
        await RunAsync(PythonExe, ["-c", "import faster_whisper, ctranslate2; print('faster-whisper', faster_whisper.__version__, '/ ctranslate2', ctranslate2.__version__)"], progress, ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(Marker, $"{PackageSpec} cuda={cuda} {DateTime.Now:O}", ct).ConfigureAwait(false);
        _log.LogInformation("faster-whisper environment ready (CUDA libraries: {Cuda})", cuda);
    }

    public void Remove()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }

    internal static async Task RunAsync(string exe, string[] args, IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report($"> {Path.GetFileName(exe)} {string.Join(' ', args)}");
        using var p = Start(exe, args);
        p.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) progress?.Report(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) progress?.Report(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        if (p.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(exe)} {args.FirstOrDefault()} failed (exit code {p.ExitCode}). See the setup log above.");
    }

    private static async Task<(int Code, string Output)> RunCaptureAsync(string exe, string[] args, CancellationToken ct)
    {
        try
        {
            using var p = Start(exe, args);
            var output = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return (p.ExitCode, output);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or OperationCanceledException or InvalidOperationException)
        {
            return (-1, "");
        }
    }

    internal static Process Start(string exe, IEnumerable<string> args, bool redirectInput = false, IReadOnlyDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectInput,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PIP_NO_INPUT"] = "1";
        psi.Environment["HF_HUB_DISABLE_SYMLINKS_WARNING"] = "1";
        psi.Environment["HF_HUB_DISABLE_TELEMETRY"] = "1";
        foreach (var (k, v) in env ?? new Dictionary<string, string>()) psi.Environment[k] = v;
        return Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {exe}");
    }
}

/// <summary>
/// faster-whisper (CTranslate2) in a Python sidecar process that loads the model once and stays warm.
/// Audio goes over stdin as float32, results come back as JSON lines (see Sidecar/faster_whisper_server.py).
/// The sidecar exits by itself if ExtormSub dies, because its stdin closes.
/// <see cref="AsrOptions.ModelPath"/> is a faster-whisper model name (e.g. "small.en") for this engine.
/// </summary>
public sealed class FasterWhisperProvider(FasterWhisperEnvironment env, ILogger<FasterWhisperProvider>? log = null) : IASRProvider
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(20); // first run downloads the model
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(120);

    private readonly ILogger _log = (ILogger?)log ?? NullLogger.Instance;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Queue<string> _recentStderr = new();
    private Process? _process;
    private Stream? _stdin;
    private byte[] _header = new byte[4];

    public string Name => "faster-whisper";
    public string BackendDescription { get; private set; } = "";
    public bool IsReady => _process is { HasExited: false };
    public AsrOptions? Current { get; private set; }

    public async Task InitializeAsync(AsrOptions options, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsReady && Current == options) return;
            if (!env.IsInstalled)
                throw new AsrInitializationException("faster-whisper is not set up yet. Open Settings → Speech Recognition and click “Set up faster-whisper”.");
            StopProcess();
            await StartAsync(options, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StartAsync(AsrOptions options, CancellationToken ct)
    {
        bool cuda = options.Backend == AsrBackend.Gpu && CudaRuntimePack.NvidiaDriverPresent;
        string device = cuda ? "cuda" : "cpu", compute = cuda ? "float16" : "int8";
        var args = new List<string>
        {
            FasterWhisperEnvironment.SidecarScript,
            "--model", options.ModelPath, "--device", device, "--compute-type", compute,
            "--language", options.Language, "--threads", options.Threads.ToString(),
            "--download-root", env.ModelsDirectory,
        };
        if (!string.IsNullOrWhiteSpace(options.InitialPrompt)) args.AddRange(["--prompt", options.InitialPrompt]);

        var sw = Stopwatch.StartNew();
        _recentStderr.Clear();
        var process = FasterWhisperEnvironment.Start(env.PythonExe, args, redirectInput: true);
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            _log.LogDebug("faster-whisper: {Line}", e.Data);
            lock (_recentStderr)
            {
                _recentStderr.Enqueue(e.Data);
                while (_recentStderr.Count > 15) _recentStderr.Dequeue();
            }
        };
        process.BeginErrorReadLine();
        _process = process;
        _stdin = process.StandardInput.BaseStream;

        JsonNode? ready;
        try
        {
            ready = await ReadLineAsync(StartupTimeout, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            StopProcess();
            throw new AsrInitializationException($"faster-whisper did not start: {ex.Message}{RecentStderr()}", ex);
        }
        if (ready?["ready"]?.GetValue<bool>() != true)
        {
            var error = ready?["error"]?.GetValue<string>() ?? "no response";
            StopProcess();
            throw new AsrInitializationException($"faster-whisper could not load {options.ModelPath} on {device}: {error}{RecentStderr()}");
        }

        Current = options;
        BackendDescription = $"faster-whisper · {device} {compute}";
        _log.LogInformation("faster-whisper loaded {Model} on {Device} ({Compute}) in {Ms} ms", options.ModelPath, device, compute, sw.ElapsedMilliseconds);
    }

    public async Task<AsrResult> TranscribeAsync(float[] samples, int count, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!IsReady || _stdin is null)
            {
                // The sidecar died (out of memory, killed): restart it once with the same options.
                if (Current is not { } options) throw new InvalidOperationException("faster-whisper is not initialized.");
                _log.LogWarning("faster-whisper sidecar exited; restarting");
                StopProcess();
                await StartAsync(options, ct).ConfigureAwait(false);
            }
            var sw = Stopwatch.StartNew();
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(_header, count);
            await _stdin!.WriteAsync(_header, ct).ConfigureAwait(false);
            await _stdin.WriteAsync(MemoryMarshal.AsBytes(samples.AsSpan(0, count)).ToArray(), ct).ConfigureAwait(false);
            await _stdin.FlushAsync(ct).ConfigureAwait(false);

            var reply = await ReadLineAsync(RequestTimeout, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("faster-whisper returned nothing." + RecentStderr());
            if (reply["error"] is { } err) throw new InvalidOperationException("faster-whisper: " + err.GetValue<string>());

            double? logprob = reply["avg_logprob"]?.GetValue<double>();
            return new AsrResult(
                reply["text"]?.GetValue<string>() ?? "",
                reply["language"]?.GetValue<string>(),
                logprob is { } lp ? (float)Math.Exp(lp) : null,
                (float)(reply["no_speech_prob"]?.GetValue<double>() ?? 0),
                sw.Elapsed);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<JsonNode?> ReadLineAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        var line = await _process!.StandardOutput.ReadLineAsync(linked.Token).ConfigureAwait(false);
        return line is null ? null : JsonNode.Parse(line);
    }

    private string RecentStderr()
    {
        lock (_recentStderr)
            return _recentStderr.Count == 0 ? "" : "\n" + string.Join("\n", _recentStderr.TakeLast(4));
    }

    private void StopProcess()
    {
        var p = _process;
        _process = null;
        _stdin = null;
        Current = null;
        if (p is null) return;
        try
        {
            if (!p.HasExited)
            {
                // Ask politely (count 0 = quit), then make sure.
                try { p.StandardInput.BaseStream.Write(new byte[4]); p.StandardInput.BaseStream.Flush(); } catch (IOException) { }
                if (!p.WaitForExit(2000)) p.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) { }
        p.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { StopProcess(); }
        finally { _gate.Release(); }
    }
}
