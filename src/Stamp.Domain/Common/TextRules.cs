using System.Text;
using System.Text.RegularExpressions;

namespace Stamp.Domain.Common;

public static partial class TextRules
{
    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    /// <summary>
    /// For names and subjects: every run of whitespace (newlines included) becomes one space and
    /// other control characters are dropped, so the value is safe to put in an email subject.
    /// </summary>
    public static string SingleLine(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var visible = Keep(value, c => !char.IsControl(c) || char.IsWhiteSpace(c));
        return Whitespace().Replace(visible, " ").Trim();
    }

    /// <summary>
    /// For message bodies: line endings become \n (browsers submit \r\n, which would otherwise
    /// count twice against length limits) and control characters other than newlines and tabs are dropped.
    /// </summary>
    public static string MultiLine(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var unixNewlines = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return Keep(unixNewlines, c => !char.IsControl(c) || c is '\n' or '\t').Trim();
    }

    private static string Keep(string value, Func<char, bool> predicate)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (predicate(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
