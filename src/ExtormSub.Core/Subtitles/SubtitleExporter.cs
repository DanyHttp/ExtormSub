using System.Globalization;
using System.Text;

namespace ExtormSub.Core.Subtitles;

public enum ExportContent { Translation, Original, Both }

public sealed record SubtitleLine(TimeSpan Start, TimeSpan End, string Original, string? Translation);

/// <summary>Writes TXT, SRT and WebVTT. Durations are padded to stay readable without overlapping the next line.</summary>
public static class SubtitleExporter
{
    public static readonly TimeSpan MinDuration = TimeSpan.FromSeconds(1.2);

    public static string ToSrt(IEnumerable<SubtitleLine> lines, ExportContent content)
    {
        var sb = new StringBuilder();
        int n = 1;
        foreach (var (line, start, end) in Timed(lines))
        {
            var text = Text(line, content);
            if (text.Length == 0) continue;
            sb.Append(n++).Append('\n')
              .Append(Stamp(start, ',')).Append(" --> ").Append(Stamp(end, ',')).Append('\n')
              .Append(text).Append("\n\n");
        }
        return sb.ToString();
    }

    public static string ToVtt(IEnumerable<SubtitleLine> lines, ExportContent content)
    {
        var sb = new StringBuilder("WEBVTT\n\n");
        foreach (var (line, start, end) in Timed(lines))
        {
            var text = Text(line, content);
            if (text.Length == 0) continue;
            sb.Append(Stamp(start, '.')).Append(" --> ").Append(Stamp(end, '.')).Append('\n')
              .Append(text).Append("\n\n");
        }
        return sb.ToString();
    }

    public static string ToText(IEnumerable<SubtitleLine> lines, ExportContent content)
    {
        var sb = new StringBuilder();
        foreach (var line in lines.OrderBy(l => l.Start))
        {
            var text = Text(line, content).Replace("\n", "\n           ");
            if (text.Length == 0) continue;
            sb.Append('[').Append(line.Start.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)).Append("] ")
              .Append(text).Append('\n');
        }
        return sb.ToString();
    }

    private static IEnumerable<(SubtitleLine Line, TimeSpan Start, TimeSpan End)> Timed(IEnumerable<SubtitleLine> lines)
    {
        var sorted = lines.OrderBy(l => l.Start).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            var l = sorted[i];
            var end = l.End < l.Start + MinDuration ? l.Start + MinDuration : l.End;
            if (i + 1 < sorted.Count && end > sorted[i + 1].Start)
                end = sorted[i + 1].Start > l.Start ? sorted[i + 1].Start : l.End;
            yield return (l, l.Start, end < l.Start ? l.Start : end);
        }
    }

    private static string Text(SubtitleLine l, ExportContent content)
    {
        var original = l.Original.Trim();
        var translation = l.Translation?.Trim() ?? "";
        return content switch
        {
            ExportContent.Original => original,
            ExportContent.Translation => translation.Length > 0 ? translation : original,
            _ => translation.Length > 0 ? $"{original}\n{translation}" : original,
        };
    }

    private static string Stamp(TimeSpan t, char msSep) =>
        $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}{msSep}{t.Milliseconds:000}";
}
