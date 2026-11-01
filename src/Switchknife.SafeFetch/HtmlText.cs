using System.Net;
using System.Text.RegularExpressions;

namespace Switchknife.SafeFetch;

/// <summary>HTML to plain text: blocks become lines, tags go, entities decode.</summary>
public static partial class HtmlText
{
    /// <summary>Input beyond this is ignored: no feed item or page needs more, and the cut bounds the regex work.</summary>
    public const int MaxChars = 256 * 1024;

    /// <summary>Plain text of <paramref name="html"/>; empty for empty input or markup that would time the regex out.</summary>
    public static string ToPlain(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return "";
        }

        try
        {
            var s = (html.Length <= MaxChars ? html : html[..MaxChars]);
            s = ScriptOrStyle().Replace(s, " ");
            s = BlockEnd().Replace(s, "\n");
            s = Tag().Replace(s, " ");
            s = WebUtility.HtmlDecode(s);
            s = Spaces().Replace(s, " ");
            s = LineEdges().Replace(s, "\n");
            s = BlankLines().Replace(s, "\n\n");
            return s.Trim();
        }
        catch (RegexMatchTimeoutException)
        {
            return "";   // hostile markup that would take quadratic time: no text
        }
    }

    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline, 250)]
    private static partial Regex ScriptOrStyle();

    [GeneratedRegex(@"<(br|/p|/div|/li|/tr|/h[1-6])\b[^>]*>", RegexOptions.IgnoreCase, 250)]
    private static partial Regex BlockEnd();

    [GeneratedRegex("<[^>]+>", RegexOptions.None, 250)]
    private static partial Regex Tag();

    [GeneratedRegex(@"[ \t\r\f\v ]+", RegexOptions.None, 250)]
    private static partial Regex Spaces();

    [GeneratedRegex(@" *\n *", RegexOptions.None, 250)]
    private static partial Regex LineEdges();

    [GeneratedRegex(@"\n{3,}", RegexOptions.None, 250)]
    private static partial Regex BlankLines();
}
