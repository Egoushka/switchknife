using System.Net;
using System.Text.RegularExpressions;

namespace Switchknife.SafeFetch;

/// <summary>A small main-content heuristic: drop page furniture, prefer the article or main element, flatten to text.</summary>
public static partial class ReadableText
{
    private const int MinRegion = 400;

    /// <summary>The readable text of a page, title first, at most <paramref name="maxChars"/> characters.</summary>
    public static string Extract(string html, int maxChars)
    {
        try
        {
            return ExtractCore(html.Length <= HtmlText.MaxChars ? html : html[..HtmlText.MaxChars], maxChars);
        }
        catch (RegexMatchTimeoutException)
        {
            return "";   // hostile markup: no text rather than a stalled loop
        }
    }

    private static string ExtractCore(string html, int maxChars)
    {
        var s = Comment().Replace(html, " ");
        var title = Title().Match(s) is { Success: true } t ? Collapse(WebWideDecode(t.Groups[1].Value)) : "";
        s = Furniture().Replace(s, " ");
        var region = Region(s, "article") ?? Region(s, "main") ?? Region(s, "body") ?? s;
        var body = HtmlText.ToPlain(region);
        var text = title.Length > 0 ? $"{title}\n\n{body}" : body;
        return text.Length <= maxChars ? text : text[..maxChars];
    }

    private static string? Region(string html, string tag)
    {
        var m = Regex.Match(html, $@"<{tag}\b[^>]*>(.*)</{tag}>", RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromMilliseconds(250));
        return m.Success && HtmlText.ToPlain(m.Groups[1].Value).Length >= MinRegion ? m.Groups[1].Value : tag == "body" && m.Success ? m.Groups[1].Value : null;
    }

    private static string WebWideDecode(string text) => WebUtility.HtmlDecode(text);

    private static string Collapse(string text) => Spaces().Replace(text, " ").Trim();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline, 250)]
    private static partial Regex Comment();

    [GeneratedRegex(@"<title\b[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline, 250)]
    private static partial Regex Title();

    [GeneratedRegex(@"<(script|style|noscript|svg|iframe|nav|header|footer|aside|form|template|select|button|dialog)\b[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline, 250)]
    private static partial Regex Furniture();

    [GeneratedRegex(@"\s+", RegexOptions.None, 250)]
    private static partial Regex Spaces();
}
