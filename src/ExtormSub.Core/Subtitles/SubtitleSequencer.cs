using ExtormSub.Core.Translation;

namespace ExtormSub.Core.Subtitles;

/// <summary>A subtitle ready for display, released strictly in seq order.</summary>
public sealed record SequencedSubtitle(
    long Seq,
    string Original,
    string? Translation,
    bool IsFinal,
    TranslationErrorKind? Error,
    bool Abandoned = false);

public sealed class SequencerOutput
{
    public static readonly SequencerOutput None = new([], []);

    public SequencerOutput(IReadOnlyList<SequencedSubtitle> display, IReadOnlyList<long> abandoned)
    {
        Display = display;
        Abandoned = abandoned;
    }

    public IReadOnlyList<SequencedSubtitle> Display { get; }

    /// <summary>Seqs given up on; their in-flight translations should be cancelled.</summary>
    public IReadOnlyList<long> Abandoned { get; }
}

/// <summary>
/// Guarantees translations reach the screen in seq order even when requests finish out of order.
/// A seq is "open" from speech start until its final result, an empty/no-text close, or abandonment.
/// Results for the lowest open seq are shown immediately; results for later seqs wait behind it.
/// If a later seq has a final result waiting and the head blocks it for longer than the head timeout,
/// the head is abandoned. Thread-safe; time is passed in.
/// </summary>
public sealed class SubtitleSequencer(TimeSpan headTimeout)
{
    private sealed class State(string original)
    {
        public string LastOriginal = original;
        public SequencedSubtitle? Pending;
        public bool Closed;
    }

    private readonly SortedDictionary<long, State> _open = new();
    private readonly object _gate = new();
    private long _floor = long.MinValue; // every seq below this is closed and gone
    private TimeSpan? _blockedSince;

    public TimeSpan HeadTimeout { get; set; } = headTimeout;

    public int OpenCount { get { lock (_gate) return _open.Count; } }

    public void Open(long seq)
    {
        lock (_gate)
        {
            if (seq >= _floor && !_open.ContainsKey(seq)) _open[seq] = new State("");
        }
    }

    /// <summary>Records the latest source text so an abandoned seq can still fall back to it.</summary>
    public void NoteOriginal(long seq, string original)
    {
        lock (_gate)
        {
            if (_open.TryGetValue(seq, out var state)) state.LastOriginal = original;
        }
    }

    public SequencerOutput Offer(SequencedSubtitle result, TimeSpan now)
    {
        lock (_gate)
        {
            if (result.Seq < _floor) return SequencerOutput.None; // obsolete
            if (!_open.TryGetValue(result.Seq, out var state))
                _open[result.Seq] = state = new State(result.Original);
            if (state.Closed) return SequencerOutput.None; // duplicate final
            state.LastOriginal = result.Original;
            state.Pending = result;
            if (result.IsFinal) state.Closed = true;
            return Flush(now, []);
        }
    }

    /// <summary>Closes a seq that produced no text (or needs no translation) without displaying anything.</summary>
    public SequencerOutput Close(long seq, TimeSpan now)
    {
        lock (_gate)
        {
            if (seq < _floor) return SequencerOutput.None;
            if (!_open.TryGetValue(seq, out var state)) _open[seq] = state = new State("");
            state.Closed = true;
            return Flush(now, []);
        }
    }

    /// <summary>Call periodically: abandons a head seq that has blocked a finished later seq for too long.</summary>
    public SequencerOutput Poll(TimeSpan now)
    {
        lock (_gate)
        {
            if (_blockedSince is not { } since || now - since < HeadTimeout || _open.Count == 0)
                return SequencerOutput.None;

            var (seq, head) = _open.First();
            _open.Remove(seq);
            _floor = seq + 1;
            _blockedSince = null;
            var display = new List<SequencedSubtitle>();
            if (head.LastOriginal.Length > 0)
                display.Add(new SequencedSubtitle(seq, head.LastOriginal, null, true, null, Abandoned: true));
            var output = Flush(now, display);
            return new SequencerOutput(output.Display, [seq, .. output.Abandoned]);
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _open.Clear();
            _floor = long.MinValue;
            _blockedSince = null;
        }
    }

    private SequencerOutput Flush(TimeSpan now, List<SequencedSubtitle> display)
    {
        while (_open.Count > 0)
        {
            var (seq, head) = _open.First();
            if (head.Pending is { } p)
            {
                display.Add(p);
                head.Pending = null;
            }
            if (!head.Closed) break;
            _open.Remove(seq);
            _floor = seq + 1;
        }

        // Is the (open) head holding back a finished later seq?
        bool blocked = _open.Count > 1 && _open.Skip(1).Any(kv => kv.Value.Closed && kv.Value.Pending is not null);
        if (!blocked) _blockedSince = null;
        else _blockedSince ??= now;

        return display.Count == 0 ? SequencerOutput.None : new SequencerOutput(display, []);
    }
}
