using ExtormSub.Core.Text;

namespace ExtormSub.Core.Translation;

public sealed record ContextLine(string Source, string? Translation);

public sealed record TranslationRequest(
    string Text,
    IReadOnlyList<ContextLine> Context,
    IReadOnlyList<GlossaryEntry> Glossary,
    string SourceLanguage,
    string TargetLanguage);

public enum TranslationErrorKind
{
    /// <summary>401/403: the key is wrong. Retrying will not help.</summary>
    Auth,
    /// <summary>429: back off (honouring Retry-After).</summary>
    RateLimited,
    /// <summary>Timeout, network error, 5xx: retry with backoff.</summary>
    Transient,
    /// <summary>400/404, unparseable response: configuration problem.</summary>
    Fatal,
}

public sealed class TranslationException(TranslationErrorKind kind, string message, TimeSpan? retryAfter = null, Exception? inner = null)
    : Exception(message, inner)
{
    public TranslationErrorKind Kind { get; } = kind;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>A remote or local machine-translation engine. Must be safe for concurrent calls.</summary>
public interface ITranslationProvider
{
    string Name { get; }

    /// <summary>Identifies provider+model for cache keys, so switching model does not reuse stale results.</summary>
    string CacheScope { get; }

    /// <summary>Returns the translation only. Throws <see cref="TranslationException"/> on failure.</summary>
    Task<string> TranslateAsync(TranslationRequest request, CancellationToken ct);
}

/// <summary>Keeps the last few finalized subtitles to give the translator conversational context.</summary>
public sealed class TranslationContextManager(int capacity)
{
    private readonly LinkedList<ContextLine> _lines = new();
    private readonly object _gate = new();

    public int Capacity { get; set; } = capacity;

    public void Add(string source, string? translation)
    {
        if (string.IsNullOrWhiteSpace(source)) return;
        lock (_gate)
        {
            _lines.AddLast(new ContextLine(source, translation));
            while (_lines.Count > Math.Max(0, Capacity)) _lines.RemoveFirst();
        }
    }

    public IReadOnlyList<ContextLine> Snapshot()
    {
        lock (_gate) return _lines.ToArray();
    }

    public void Clear()
    {
        lock (_gate) _lines.Clear();
    }
}

/// <summary>Thread-safe LRU cache of translations keyed by normalized source text.</summary>
public sealed class TranslationCache(int capacity)
{
    private readonly Dictionary<string, LinkedListNode<(string Key, string Value)>> _map = new();
    private readonly LinkedList<(string Key, string Value)> _lru = new();
    private readonly object _gate = new();
    private long _hits;
    private long _misses;

    public int Count { get { lock (_gate) return _map.Count; } }
    public long Hits => Interlocked.Read(ref _hits);
    public long Misses => Interlocked.Read(ref _misses);

    public static string Key(string scope, string targetLanguage, string text) =>
        $"{scope}\u001f{targetLanguage}\u001f{TextNormalizer.ForKey(text)}";

    public bool TryGet(string key, out string value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                value = node.Value.Value;
                _hits++;
                return true;
            }
            _misses++;
            value = "";
            return false;
        }
    }

    public void Set(string key, string value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _lru.Remove(existing);
                _map.Remove(key);
            }
            _map[key] = _lru.AddFirst((key, value));
            while (_map.Count > Math.Max(1, capacity))
            {
                var last = _lru.Last!;
                _lru.RemoveLast();
                _map.Remove(last.Value.Key);
            }
        }
    }

    public void Clear()
    {
        lock (_gate) { _map.Clear(); _lru.Clear(); }
    }
}
