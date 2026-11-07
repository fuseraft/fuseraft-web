using fuseraft.Web.Components.Shared;

namespace FuseraftWeb.Tests;

public class MarkdownTests
{
    [Theory]
    [InlineData("hi <img src=x onerror=alert(1)>")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("[a](https://x.io){onclick=alert(1)}")]
    [InlineData("# Title {onmouseover=alert(1)}")]
    public void Html_AndAddedAttributes_AreText(string reply)
    {
        var html = Markdown.Render(reply);

        Assert.DoesNotContain("<img", html);
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("=\"alert", html);
    }

    [Fact]
    public void MediaLinks_AreNotEmbedded() =>
        Assert.DoesNotContain("<iframe", Markdown.Render("![v](https://www.youtube.com/watch?v=abc)"));

    [Theory]
    [InlineData("[a](javascript:alert(1))")]
    [InlineData("[a](JaVaScRiPt:alert(1))")]
    [InlineData("![a](javascript:alert(1))")]
    [InlineData("<javascript:alert(1)>")]
    [InlineData("[a][r]\n\n[r]: javascript:alert(1)")]
    [InlineData("[a](data:text/html,x)")]
    [InlineData("[a](vbscript:x)")]
    public void UnsafeLinks_LoseTheirTarget(string reply)
    {
        var html = Markdown.Render(reply);

        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data:", html);
        Assert.DoesNotContain("vbscript:", html);
    }

    [Theory]
    [InlineData("https://fuseraft.ai/docs", true)]
    [InlineData("http://localhost:5000/chat", true)]
    [InlineData("mailto:hi@example.com", true)]
    [InlineData("/chat", true)]
    [InlineData("docs/guide.md#setup", true)]
    [InlineData("#top", true)]
    [InlineData("a/b:c", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("java\tscript:alert(1)", false)]
    [InlineData(" javascript:alert(1)", false)]
    [InlineData("file:///etc/passwd", false)]
    public void IsSafeUrl_AllowsWebLinksOnly(string url, bool safe) =>
        Assert.Equal(safe, Markdown.IsSafeUrl(url));

    [Fact]
    public void Formatting_StillRenders()
    {
        var html = Markdown.Render("**bold** ~~gone~~ `code`\n\n| a | b |\n|---|---|\n| 1 | 2 |\n\n- [x] done\n\n[docs](https://fuseraft.ai) and https://fuseraft.ai/web");

        Assert.Contains("<strong>bold</strong>", html);
        Assert.Contains("<del>gone</del>", html);
        Assert.Contains("<code>code</code>", html);
        Assert.Contains("<table>", html);
        Assert.Contains("type=\"checkbox\"", html);
        Assert.Contains("<a href=\"https://fuseraft.ai\">docs</a>", html);
        Assert.Contains("<a href=\"https://fuseraft.ai/web\">", html);
    }
}
