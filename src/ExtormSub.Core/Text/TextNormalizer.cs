using System.Text;

namespace ExtormSub.Core.Text;

public static class TextNormalizer
{
    /// <summary>Collapses runs of whitespace to one space and trims.</summary>
    public static string CollapseWhitespace(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        bool space = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch)) { space = sb.Length > 0; continue; }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Key used for caching and "has the text changed" checks: whitespace collapsed, lower-case,
    /// trailing periods/ellipses dropped. '?' and '!' are kept because they change the meaning of a translation.
    /// </summary>
    public static string ForKey(string text)
    {
        var s = CollapseWhitespace(text).ToLowerInvariant();
        return s.TrimEnd('.', '…', ' ', ',');
    }
}
