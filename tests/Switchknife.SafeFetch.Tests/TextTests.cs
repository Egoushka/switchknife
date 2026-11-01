namespace Switchknife.SafeFetch.Tests;

public sealed class TextTests
{
    private static readonly string Filler = string.Join(" ", Enumerable.Repeat("The quick brown fox jumps over the lazy dog.", 12));

    [Fact]
    public void Html_becomes_plain_text_without_scripts()
    {
        Assert.Equal("Hello world\nTwo", HtmlText.ToPlain("<p>Hello&nbsp;<b>world</b></p><script>x()</script><p>Two</p>"));
        Assert.Equal("a & b <c>", HtmlText.ToPlain("a &amp; b &lt;c&gt;"));
        Assert.Equal("a\n\nb", HtmlText.ToPlain("<p>a</p><p></p><p></p><p>b</p>"));
        Assert.Equal("", HtmlText.ToPlain(null));
        Assert.Equal("", HtmlText.ToPlain("   "));
    }

    [Fact]
    public void Readable_text_prefers_the_article_and_keeps_the_title()
    {
        var html = $"<html><head><title>Post &amp; title</title></head><body><nav>Home About</nav><p>Sidebar teaser</p>"
            + $"<article><p>{Filler}</p></article><footer>Copyright</footer></body></html>";

        var text = ReadableText.Extract(html, 10_000);

        Assert.StartsWith("Post & title\n\n", text, StringComparison.Ordinal);
        Assert.Contains("quick brown fox", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Sidebar teaser", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Home About", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Copyright", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Readable_text_falls_back_to_main_then_body_and_drops_furniture()
    {
        var main = ReadableText.Extract($"<body><p>teaser</p><main><p>{Filler}</p></main></body>", 10_000);
        var body = ReadableText.Extract("<body><nav>menu</nav><p>Short body</p><aside>ad</aside></body>", 10_000);

        Assert.DoesNotContain("teaser", main, StringComparison.Ordinal);
        Assert.Equal("Short body", body);
    }

    [Fact]
    public void Readable_text_is_cut_at_the_limit()
    {
        Assert.Equal(20, ReadableText.Extract($"<article>{Filler}</article>", 20).Length);
    }

    [Fact]
    public void Hostile_markup_yields_no_text_rather_than_an_exception()
    {
        var hostile = string.Concat(Enumerable.Repeat("<script>", 40_000));

        Assert.Equal("", HtmlText.ToPlain(hostile));
        Assert.Equal("", ReadableText.Extract(string.Concat(Enumerable.Repeat("<nav>", 60_000)), 10_000));
    }
}
