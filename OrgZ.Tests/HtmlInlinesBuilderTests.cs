// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Helpers;

namespace OrgZ.Tests;

/// <summary>
/// The HTML show-notes -> plain-text pipeline (tag stripping, entity decoding, list
/// bullets, block breaks, whitespace cleanup) that powers podcast description previews.
/// </summary>
public class HtmlInlinesBuilderTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ToPlainText_blank_input_is_empty(string? html)
        => Assert.Equal("", HtmlInlinesBuilder.ToPlainText(html));

    [Fact]
    public void ToPlainText_strips_inline_tags()
        => Assert.Equal("Hello world", HtmlInlinesBuilder.ToPlainText("<b>Hello</b> <i>world</i>"));

    [Fact]
    public void ToPlainText_decodes_html_entities()
        => Assert.Equal("Tom & Jerry > all", HtmlInlinesBuilder.ToPlainText("Tom &amp; Jerry &gt; all"));

    [Fact]
    public void ToPlainText_paragraphs_become_newlines()
    {
        var result = HtmlInlinesBuilder.ToPlainText("<p>One</p><p>Two</p>");
        Assert.Contains("One", result);
        Assert.Contains("Two", result);
        Assert.Contains("\n", result);
    }

    [Fact]
    public void ToPlainText_list_items_become_bullets()
        => Assert.Contains("· Item", HtmlInlinesBuilder.ToPlainText("<ul><li>Item</li></ul>"));

    [Fact]
    public void ToPlainText_collapses_excess_blank_lines()
        => Assert.DoesNotContain("\n\n\n", HtmlInlinesBuilder.ToPlainText("A<br><br><br><br>B"));

    [Fact]
    public void ToPlainText_trims_leading_whitespace_after_newlines()
    {
        var result = HtmlInlinesBuilder.ToPlainText("<p>   Hosted on Acast</p>");
        Assert.DoesNotContain("\n ", result);
        Assert.Contains("Hosted on Acast", result);
    }
}
