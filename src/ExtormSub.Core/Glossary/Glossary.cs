using System.Text;
using System.Text.RegularExpressions;

namespace ExtormSub.Core.Text;

/// <summary>
/// A preferred spelling (<see cref="Term"/>), ASR mis-hearings that map to it (<see cref="Aliases"/>),
/// and an optional fixed rendering in the target language.
/// </summary>
public sealed record GlossaryEntry
{
    public string Term { get; init; } = "";
    public List<string> Aliases { get; init; } = [];
    public string? Translation { get; init; }
}

/// <summary>Compiled glossary: normalizes ASR output and produces prompt/translation hints.</summary>
public sealed class Glossary
{
    private readonly List<(GlossaryEntry Entry, Regex Pattern)> _rules;

    public Glossary(IEnumerable<GlossaryEntry> entries)
    {
        Entries = entries.Where(e => !string.IsNullOrWhiteSpace(e.Term)).ToList();
        _rules = Entries.Select(e => (e, Compile(e))).ToList();
    }

    public static Glossary Empty { get; } = new([]);

    public IReadOnlyList<GlossaryEntry> Entries { get; }

    /// <summary>Whisper initial prompt listing the terms, which biases recognition toward their spelling.</summary>
    public string? AsrPrompt => Entries.Count == 0 ? null : string.Join(", ", Entries.Select(e => e.Term)) + ".";

    /// <summary>Replaces aliases and wrong-cased variants with the canonical term (whole words only).</summary>
    public string ApplyToTranscript(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        foreach (var (entry, pattern) in _rules)
            text = pattern.Replace(text, entry.Term);
        return text;
    }

    /// <summary>Entries whose term occurs in <paramref name="text"/>, used as translation hints.</summary>
    public IReadOnlyList<GlossaryEntry> FindIn(string text) =>
        _rules.Where(r => r.Pattern.IsMatch(text)).Select(r => r.Entry).ToList();

    private static Regex Compile(GlossaryEntry e)
    {
        var variants = e.Aliases.Append(e.Term)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(a => a.Length)
            // "chat gpt" also matches "chat-gpt" and "chat   gpt".
            .Select(a => string.Join(@"[\s\-]*", a.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape)));
        return new Regex($@"(?<![\p{{L}}\p{{N}}])(?:{string.Join("|", variants)})(?![\p{{L}}\p{{N}}])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }

    /// <summary>
    /// Parses the editor format, one entry per line: <c>Term; alias1, alias2; translation</c>.
    /// Alias and translation fields are optional. Lines starting with # are comments.
    /// </summary>
    public static List<GlossaryEntry> Parse(string text)
    {
        var result = new List<GlossaryEntry>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split(';');
            var term = parts[0].Trim();
            if (term.Length == 0) continue;
            var aliases = parts.Length > 1
                ? parts[1].Split(',').Select(a => a.Trim()).Where(a => a.Length > 0).ToList()
                : [];
            var translation = parts.Length > 2 && parts[2].Trim().Length > 0 ? parts[2].Trim() : null;
            result.Add(new GlossaryEntry { Term = term, Aliases = aliases, Translation = translation });
        }
        return result;
    }

    public static string Format(IEnumerable<GlossaryEntry> entries)
    {
        var sb = new StringBuilder();
        foreach (var e in entries)
        {
            sb.Append(e.Term);
            if (e.Aliases.Count > 0 || e.Translation is not null) sb.Append("; ").Append(string.Join(", ", e.Aliases));
            if (e.Translation is not null) sb.Append("; ").Append(e.Translation);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public static List<GlossaryEntry> Defaults() =>
    [
        new() { Term = "OpenAI", Aliases = ["open ai"] },
        new() { Term = "ChatGPT", Aliases = ["chat gpt", "chat g p t", "chatgbt"] },
        new() { Term = "Claude", Aliases = [] },
        new() { Term = "GitHub", Aliases = ["git hub"] },
        new() { Term = "YouTube", Aliases = ["you tube"] },
    ];
}
