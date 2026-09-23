using System.Text.RegularExpressions;
using ExtormSub.Core.Text;

namespace ExtormSub.Core.ASR;

public readonly record struct StabilizerDecision(long Seq, string Text, bool IsFinal, bool Translate);

/// <summary>
/// Decides when a transcript is eligible for translation, so the API is not called for every partial.
/// Eligible when (a) ASR marks it final (VAD end-of-utterance), or (b) two consecutive partial hypotheses
/// agree and the first was seen at least <see cref="Debounce"/> ago. Each distinct text is released once.
/// Pure logic: time is passed in. Not thread-safe (called from the single ASR worker).
/// </summary>
public sealed class TranscriptStabilizer
{
    private long _seq = long.MinValue;
    private string _key = "";
    private TimeSpan _changedAt;
    private string? _submittedKey;
    private bool _final;

    public TranscriptStabilizer(TimeSpan debounce) => Debounce = debounce;

    public TimeSpan Debounce { get; set; }

    /// <summary>Returns null when the partial is stale (older seq, or its seq is already final).</summary>
    public StabilizerDecision? OnPartial(long seq, string text, TimeSpan now)
    {
        if (seq < _seq || (seq == _seq && _final)) return null;
        if (seq > _seq) Begin(seq);

        var key = TextNormalizer.ForKey(text);
        if (key.Length == 0) return new StabilizerDecision(seq, text, false, false);

        if (key != _key)
        {
            _key = key;
            _changedAt = now;
            return new StabilizerDecision(seq, text, false, false);
        }

        bool stable = now - _changedAt >= Debounce && key != _submittedKey;
        if (stable) _submittedKey = key;
        return new StabilizerDecision(seq, text, false, stable);
    }

    /// <summary>Final text always closes the seq. Translate is false only when the text is empty.</summary>
    public StabilizerDecision? OnFinal(long seq, string text, TimeSpan now)
    {
        if (seq < _seq || (seq == _seq && _final)) return null;
        if (seq > _seq) Begin(seq);
        _final = true;
        var key = TextNormalizer.ForKey(text);
        _key = key;
        _changedAt = now;
        _submittedKey = key;
        return new StabilizerDecision(seq, text, true, key.Length > 0);
    }

    private void Begin(long seq)
    {
        _seq = seq;
        _key = "";
        _submittedKey = null;
        _final = false;
    }
}

/// <summary>Cleans raw ASR text: drops sound annotations and known whisper hallucinations.</summary>
public static partial class TranscriptFilter
{
    // Phrases whisper emits on silence/music; never legitimate subtitle content.
    private static readonly string[] AlwaysHallucinations =
    [
        "subtitles by the amara.org community",
        "transcribed by",
        "subtitles by",
        "captions by",
        "www.",
    ];

    // Plausible speech, but also classic hallucinations. Dropped only when whisper itself is unsure.
    private static readonly HashSet<string> SuspiciousShort = new(StringComparer.OrdinalIgnoreCase)
    {
        "thank you", "thanks for watching", "thank you for watching", "you", "bye", "okay", "so",
        "please subscribe", "thank you so much for watching",
    };

    public static string Clean(string raw, float noSpeechProbability, float noSpeechThreshold = 0.6f)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var text = Annotation().Replace(raw, " ");
        text = text.Replace("♪", " ").Replace("♫", " ");
        text = TextNormalizer.CollapseWhitespace(text).Trim('-', ' ');
        if (text.Length == 0) return "";

        var lower = text.ToLowerInvariant();
        foreach (var h in AlwaysHallucinations)
            if (lower.Contains(h)) return "";

        if (noSpeechProbability >= noSpeechThreshold) return "";
        if (noSpeechProbability >= noSpeechThreshold / 2 && SuspiciousShort.Contains(lower.TrimEnd('.', '!', '?', ' ')))
            return "";

        // Nothing but punctuation.
        return text.Any(char.IsLetterOrDigit) ? text : "";
    }

    [GeneratedRegex(@"\[[^\]]*\]|\([^)]*\)|\*[^*]*\*")]
    private static partial Regex Annotation();
}
