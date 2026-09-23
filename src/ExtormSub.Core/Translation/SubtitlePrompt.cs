using System.Text;
using System.Text.RegularExpressions;

namespace ExtormSub.Core.Translation;

/// <summary>Prompt construction and response clean-up for LLM subtitle translation.</summary>
public static partial class SubtitlePrompt
{
    public static string System(string source, string target) => $"""
        You are a professional subtitle translator. You translate live {source} speech transcripts into natural, fluent {target} subtitles.

        Rules:
        - Translate ONLY the text inside <translate>. Lines inside <context> are earlier subtitles, given so you understand the conversation. Never translate, repeat or summarize them.
        - Write {target} the way a native speaker would naturally say it, not a literal word-for-word translation. Preserve meaning, tone, register, humor, slang and emotion.
        - Keep names, brands and technical terms unchanged, or use the rendering given in <glossary>.
        - Keep it short and readable, like a subtitle. The source comes from automatic speech recognition and may contain small errors or be an unfinished sentence; translate the most likely intended meaning and do not complete it.
        - Output only the {target} translation: no quotes, no labels, no notes, no explanations, no markdown, no transliteration.
        """;

    public static string User(TranslationRequest r)
    {
        var sb = new StringBuilder();
        if (r.Context.Count > 0)
        {
            sb.AppendLine("<context>");
            foreach (var c in r.Context)
                sb.AppendLine(c.Translation is null ? c.Source : $"{c.Source} => {c.Translation}");
            sb.AppendLine("</context>");
        }
        if (r.Glossary.Count > 0)
        {
            sb.AppendLine("<glossary>");
            foreach (var g in r.Glossary)
                sb.AppendLine($"{g.Term} => {g.Translation ?? g.Term}");
            sb.AppendLine("</glossary>");
        }
        sb.Append("<translate>").Append(r.Text).Append("</translate>");
        return sb.ToString();
    }

    /// <summary>Strips things models add despite instructions: tags, fences, quotes, labels, extra lines.</summary>
    public static string CleanResponse(string raw)
    {
        var s = raw.Trim();
        s = Fence().Replace(s, "").Trim();
        s = Tag().Replace(s, "").Trim();
        s = Label().Replace(s, "").Trim();
        s = string.Join(' ', s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (s.Length >= 2 && IsQuote(s[0]) && IsQuote(s[^1])) s = s[1..^1].Trim();
        return s;
    }

    private static bool IsQuote(char c) => c is '"' or '“' or '”' or '«' or '»' or '\'';

    [GeneratedRegex(@"^```[a-zA-Z]*\s*|\s*```$")]
    private static partial Regex Fence();

    [GeneratedRegex(@"</?(translate|translation|context|glossary)>", RegexOptions.IgnoreCase)]
    private static partial Regex Tag();

    [GeneratedRegex(@"^(translation|persian|farsi|ترجمه)\s*[:：]\s*", RegexOptions.IgnoreCase)]
    private static partial Regex Label();
}
