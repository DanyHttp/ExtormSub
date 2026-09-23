using System.Diagnostics;
using ExtormSub.Core.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExtormSub.Core.Translation;

public sealed record TranslationQueueOptions
{
    public int MaxConcurrent { get; init; } = 2;
    public int MaxRequestsPerMinute { get; init; } = 60;
    public int MaxRetries { get; init; } = 2;
    public TimeSpan BaseBackoff { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(8);
    /// <summary>First wait after HTTP 429 without Retry-After; free tiers need seconds, not milliseconds.</summary>
    public TimeSpan RateLimitBackoff { get; init; } = TimeSpan.FromSeconds(2);
    public string SourceLanguage { get; init; } = "English";
    public string TargetLanguage { get; init; } = "Persian";
}

public sealed record TranslationJob(
    long Seq,
    string Text,
    bool IsFinal,
    IReadOnlyList<ContextLine> Context,
    IReadOnlyList<GlossaryEntry> Glossary);

public sealed record TranslationOutcome(
    long Seq,
    string SourceText,
    string? Translation,
    bool IsFinal,
    bool FromCache,
    TimeSpan Latency,
    TranslationErrorKind? Error,
    string? ErrorMessage);

public enum TranslationHealth { Idle, Ok, Retrying, Offline, AuthError, ConfigError }

/// <summary>
/// Runs translations off the ASR thread with bounded concurrency, a sliding-window rate limit,
/// exponential backoff and cache. Each seq has at most one live request. A newer text for the same
/// seq cancels the older one, and the same text is de-duplicated (a stable partial promoted to final
/// reuses the in-flight request). Cancelled requests report nothing; every other request raises
/// <see cref="Completed"/> exactly once, on a thread-pool thread.
/// </summary>
public sealed class TranslationQueue : IDisposable
{
    private sealed class Entry(string key, CancellationTokenSource cts)
    {
        public string Key { get; } = key;
        public CancellationTokenSource Cts { get; } = cts;
        public bool Final;
    }

    private readonly ITranslationProvider _provider;
    private readonly TranslationCache _cache;
    private readonly TranslationQueueOptions _options;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _concurrency;
    private readonly Dictionary<long, Entry> _entries = new();
    private readonly Queue<long> _requestTimes = new();
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private int _pending;
    private int _inFlight;
    private TranslationHealth _health = TranslationHealth.Idle;

    public TranslationQueue(ITranslationProvider provider, TranslationCache cache, TranslationQueueOptions options, ILogger? log = null)
    {
        _provider = provider;
        _cache = cache;
        _options = options;
        _log = log ?? NullLogger.Instance;
        _concurrency = new SemaphoreSlim(Math.Max(1, options.MaxConcurrent));
    }

    public event Action<TranslationOutcome>? Completed;
    public event Action<TranslationHealth, string?>? HealthChanged;

    public int Pending => Volatile.Read(ref _pending);
    public int InFlight => Volatile.Read(ref _inFlight);
    public TranslationHealth Health => _health;
    public string ProviderName => _provider.Name;

    public void Submit(TranslationJob job)
    {
        var key = TextNormalizer.ForKey(job.Text);
        var cacheKey = TranslationCache.Key(_provider.CacheScope, _options.TargetLanguage, job.Text);
        Entry entry;
        lock (_gate)
        {
            if (_entries.TryGetValue(job.Seq, out var existing))
            {
                if (existing.Key == key)
                {
                    existing.Final |= job.IsFinal;
                    return;
                }
                existing.Cts.Cancel();
                _entries.Remove(job.Seq);
            }

            if (_cache.TryGet(cacheKey, out var cached))
            {
                RaiseLater(new TranslationOutcome(job.Seq, job.Text, cached, job.IsFinal, true, TimeSpan.Zero, null, null));
                return;
            }

            entry = new Entry(key, CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token)) { Final = job.IsFinal };
            _entries[job.Seq] = entry;
        }
        Interlocked.Increment(ref _pending);
        _ = RunAsync(job, entry, cacheKey);
    }

    /// <summary>Cancels the live request for <paramref name="seq"/> (it became obsolete). Nothing is reported.</summary>
    public void Cancel(long seq)
    {
        lock (_gate)
        {
            if (_entries.Remove(seq, out var e)) e.Cts.Cancel();
        }
    }

    public void CancelAll()
    {
        lock (_gate)
        {
            foreach (var e in _entries.Values) e.Cts.Cancel();
            _entries.Clear();
        }
    }

    private async Task RunAsync(TranslationJob job, Entry entry, string cacheKey)
    {
        var ct = entry.Cts.Token;
        var sw = Stopwatch.StartNew();
        bool acquired = false;
        try
        {
            await _concurrency.WaitAsync(ct).ConfigureAwait(false);
            acquired = true;
            Interlocked.Decrement(ref _pending);
            Interlocked.Increment(ref _inFlight);

            var request = new TranslationRequest(job.Text, job.Context, job.Glossary, _options.SourceLanguage, _options.TargetLanguage);
            for (int attempt = 0; ; attempt++)
            {
                await WaitForRateLimitAsync(ct).ConfigureAwait(false);
                try
                {
                    var translation = await _provider.TranslateAsync(request, ct).ConfigureAwait(false);
                    _cache.Set(cacheKey, translation);
                    SetHealth(TranslationHealth.Ok, null);
                    Finish(job, entry, translation, sw.Elapsed, null, null);
                    return;
                }
                catch (TranslationException ex) when (IsRetryable(ex.Kind) && attempt < _options.MaxRetries)
                {
                    var delay = Backoff(attempt, ex.RetryAfter, ex.Kind);
                    SetHealth(TranslationHealth.Retrying, ex.Message);
                    _log.LogWarning("Translation #{Seq} attempt {Attempt} failed ({Kind}): {Message}. Retrying in {Delay} ms",
                        job.Seq, attempt + 1, ex.Kind, ex.Message, (int)delay.TotalMilliseconds);
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Superseded, abandoned or shutting down: silent by contract.
        }
        catch (TranslationException ex)
        {
            SetHealth(ex.Kind switch
            {
                TranslationErrorKind.Auth => TranslationHealth.AuthError,
                TranslationErrorKind.Fatal => TranslationHealth.ConfigError,
                _ => TranslationHealth.Offline,
            }, ex.Message);
            _log.LogWarning("Translation #{Seq} failed ({Kind}): {Message}", job.Seq, ex.Kind, ex.Message);
            Finish(job, entry, null, sw.Elapsed, ex.Kind, ex.Message);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Translation #{Seq} failed unexpectedly", job.Seq);
            Finish(job, entry, null, sw.Elapsed, TranslationErrorKind.Fatal, ex.Message);
        }
        finally
        {
            // Entries still in the dictionary are never disposed, so Cancel/CancelAll stay safe.
            lock (_gate) RemoveIfCurrent(job.Seq, entry);
            entry.Cts.Dispose();
            if (acquired)
            {
                Interlocked.Decrement(ref _inFlight);
                _concurrency.Release();
            }
            else
            {
                Interlocked.Decrement(ref _pending);
            }
        }
    }

    private void Finish(TranslationJob job, Entry entry, string? translation, TimeSpan latency, TranslationErrorKind? error, string? message)
    {
        bool final;
        lock (_gate)
        {
            if (entry.Cts.IsCancellationRequested) return;
            final = entry.Final;
            RemoveIfCurrent(job.Seq, entry);
        }
        Raise(new TranslationOutcome(job.Seq, job.Text, translation, final, false, latency, error, message));
    }

    private void RemoveIfCurrent(long seq, Entry entry)
    {
        if (_entries.TryGetValue(seq, out var current) && ReferenceEquals(current, entry))
            _entries.Remove(seq);
    }

    private static bool IsRetryable(TranslationErrorKind kind) => kind is TranslationErrorKind.Transient or TranslationErrorKind.RateLimited;

    private TimeSpan Backoff(int attempt, TimeSpan? retryAfter, TranslationErrorKind kind)
    {
        if (retryAfter is { } ra) return ra < _options.MaxBackoff ? ra : _options.MaxBackoff;
        var baseDelay = kind == TranslationErrorKind.RateLimited && _options.RateLimitBackoff > _options.BaseBackoff
            ? _options.RateLimitBackoff : _options.BaseBackoff;
        var ms = baseDelay.TotalMilliseconds * Math.Pow(2, attempt);
        ms *= 0.8 + Random.Shared.NextDouble() * 0.4; // jitter
        return TimeSpan.FromMilliseconds(Math.Min(ms, _options.MaxBackoff.TotalMilliseconds));
    }

    /// <summary>Sliding one-minute window: bursts allowed, sustained rate capped.</summary>
    private async Task WaitForRateLimitAsync(CancellationToken ct)
    {
        if (_options.MaxRequestsPerMinute <= 0) return;
        while (true)
        {
            TimeSpan wait;
            lock (_requestTimes)
            {
                var now = Stopwatch.GetElapsedTime(0);
                while (_requestTimes.Count > 0 && now - TimeSpan.FromTicks(_requestTimes.Peek()) >= TimeSpan.FromMinutes(1))
                    _requestTimes.Dequeue();
                if (_requestTimes.Count < _options.MaxRequestsPerMinute)
                {
                    _requestTimes.Enqueue(now.Ticks);
                    return;
                }
                wait = TimeSpan.FromTicks(_requestTimes.Peek()) + TimeSpan.FromMinutes(1) - now;
            }
            await Task.Delay(wait, ct).ConfigureAwait(false);
        }
    }

    private void SetHealth(TranslationHealth health, string? message)
    {
        if (_health == health && health == TranslationHealth.Ok) return;
        _health = health;
        HealthChanged?.Invoke(health, message);
    }

    private void RaiseLater(TranslationOutcome outcome) => ThreadPool.QueueUserWorkItem(_ => Raise(outcome));

    private void Raise(TranslationOutcome outcome)
    {
        try { Completed?.Invoke(outcome); }
        catch (Exception ex) { _log.LogError(ex, "Translation completion handler failed for #{Seq}", outcome.Seq); }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        CancelAll();
    }
}
