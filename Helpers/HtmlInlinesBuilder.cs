// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace OrgZ.Helpers;

/// <summary>
/// Renders the basic-HTML show notes podcast feeds ship as a sequence of
/// Avalonia <see cref="Inline"/> elements for <c>TextBlock.Inlines</c>: plain
/// text becomes <see cref="Run"/>, anchor tags + bare http(s) URLs become
/// hyperlink-styled runs (blue + underline) that carry their target URL via
/// the attached <see cref="UrlProperty"/>. Paragraph / br / li tags become
/// line breaks.
/// </summary>
internal static class HtmlInlinesBuilder
{
    // U+0001 (SOH) -- a control codepoint that won't appear in real RSS HTML.
    // We replace each anchor with [SOH]A[index][SOH] during the pre-pass so
    // we can split on the surviving cleaned-up text without colliding with
    // anything the source might contain.
    private const char SOH = '';

    private static readonly Regex AnchorRegex = new(
        @"<a\s+[^>]*?href\s*=\s*[""']([^""']+)[""'][^>]*?>(.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex BlockBreakRegex = new(
        @"</?(p|br|hr|li|div|h[1-6])[^>]*>",
        RegexOptions.IgnoreCase);

    private static readonly Regex AnyTagRegex = new(
        @"<[^>]+>",
        RegexOptions.IgnoreCase);

    private static readonly Regex UrlRegex = new(
        @"\bhttps?://[^\s<>""']+",
        RegexOptions.IgnoreCase);

    private static readonly Regex ListItemRegex = new(
        @"<li[^>]*>",
        RegexOptions.IgnoreCase);

    private static readonly IBrush LinkBrush = new ImmutableSolidColorBrush(Color.Parse("#4A9EFF"));

    /// <summary>
    /// Parses <paramref name="html"/> and returns inlines suitable for
    /// assigning to a <see cref="TextBlock.Inlines"/> or
    /// <see cref="SelectableTextBlock.Inlines"/>. Hyperlink runs carry their
    /// URL through <see cref="UrlProperty"/>; a host control wires a tap
    /// handler that resolves which inline was hit and dispatches via
    /// <see cref="OpenUrl"/>.
    /// </summary>
    public static List<Inline> Build(string? html)
    {
        var result = new List<Inline>();

        if (string.IsNullOrWhiteSpace(html))
        {
            return result;
        }

        // 1. Anchors first so we keep their text + URL pairs while we're
        //    still parsing structured HTML. Replace each anchor with a
        //    placeholder token we can split on later.
        var anchors = new List<(string Label, string Url)>();
        var withTokens = AnchorRegex.Replace(html, m =>
        {
            var url = WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
            var label = StripInnerTags(m.Groups[2].Value);

            if (string.IsNullOrEmpty(url))
            {
                return label;
            }

            anchors.Add((label, url));
            return $"{SOH}A{anchors.Count - 1}{SOH}";
        });

        // 2. List bullets, then block-level breaks.
        withTokens = ListItemRegex.Replace(withTokens, "\n· ");
        withTokens = BlockBreakRegex.Replace(withTokens, "\n");

        // 3. Strip everything else; decode entities.
        withTokens = AnyTagRegex.Replace(withTokens, "");
        withTokens = WebUtility.HtmlDecode(withTokens);

        // 4. Trim whitespace immediately after each newline so leading spaces
        //    inside <p> (a common podcast-feed pattern: `<p> Hosted on...`)
        //    don't render as a literal space at the start of every paragraph.
        withTokens = Regex.Replace(withTokens, @"\n[ \t]+", "\n");

        // Collapse 3+ consecutive newlines to keep spacing reasonable.
        withTokens = Regex.Replace(withTokens, @"\n{3,}", "\n\n");
        withTokens = withTokens.Trim();

        // 5. Walk the cleaned string. Anchor tokens emit hyperlink runs.
        //    Bare URLs inside plain text emit hyperlink runs too.
        //    Everything else lands in Runs (with LineBreaks for \n).
        int cursor = 0;
        while (cursor < withTokens.Length)
        {
            if (withTokens[cursor] == SOH
                && cursor + 2 < withTokens.Length
                && withTokens[cursor + 1] == 'A')
            {
                var end = withTokens.IndexOf(SOH, cursor + 2);
                if (end > 0)
                {
                    var idxStr = withTokens.Substring(cursor + 2, end - cursor - 2);
                    if (int.TryParse(idxStr, out var idx) && idx >= 0 && idx < anchors.Count)
                    {
                        var (label, url) = anchors[idx];
                        result.Add(BuildHyperlinkRun(label, url));
                        cursor = end + 1;
                        continue;
                    }
                }
            }

            var nextToken = withTokens.IndexOf(SOH, cursor);
            var nextNewline = withTokens.IndexOf('\n', cursor);
            int runEnd;

            if (nextToken < 0 && nextNewline < 0)
            {
                runEnd = withTokens.Length;
            }
            else if (nextToken < 0)
            {
                runEnd = nextNewline;
            }
            else if (nextNewline < 0)
            {
                runEnd = nextToken;
            }
            else
            {
                runEnd = Math.Min(nextToken, nextNewline);
            }

            if (runEnd > cursor)
            {
                AppendTextWithBareUrls(result, withTokens.Substring(cursor, runEnd - cursor));
            }

            if (runEnd < withTokens.Length && withTokens[runEnd] == '\n')
            {
                result.Add(new LineBreak());
                cursor = runEnd + 1;
            }
            else
            {
                cursor = runEnd;
            }
        }

        return result;
    }

    /// <summary>
    /// Plain-text helper used by callers that need a flat string preview
    /// without the inline machinery (status bar previews, etc.).
    /// </summary>
    public static string ToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return "";
        }

        var stripped = ListItemRegex.Replace(html, "\n· ");
        stripped = BlockBreakRegex.Replace(stripped, "\n");
        stripped = AnyTagRegex.Replace(stripped, "");
        stripped = WebUtility.HtmlDecode(stripped);
        stripped = Regex.Replace(stripped, @"\n[ \t]+", "\n");
        stripped = Regex.Replace(stripped, @"\n{3,}", "\n\n");
        return stripped.Trim();
    }

    private static string StripInnerTags(string fragment)
    {
        return WebUtility.HtmlDecode(AnyTagRegex.Replace(fragment, "")).Trim();
    }

    private static void AppendTextWithBareUrls(List<Inline> sink, string text)
    {
        int cursor = 0;
        foreach (Match m in UrlRegex.Matches(text))
        {
            if (m.Index > cursor)
            {
                sink.Add(new Run(text.Substring(cursor, m.Index - cursor)));
            }

            sink.Add(BuildHyperlinkRun(m.Value, m.Value));
            cursor = m.Index + m.Length;
        }

        if (cursor < text.Length)
        {
            sink.Add(new Run(text.Substring(cursor)));
        }
    }

    private static Run BuildHyperlinkRun(string label, string url)
    {
        // Avalonia has no Hyperlink inline, so a Run styled like one plus an
        // attached URL is what the host control resolves at tap time. The
        // visual cues (blue + underline) are what users expect; the click
        // handler lives on the host so we don't capture closures per run.
        var run = new Run(label)
        {
            Foreground = LinkBrush,
            TextDecorations = TextDecorations.Underline,
        };

        run.SetValue(UrlProperty, url);
        return run;
    }

    /// <summary>
    /// Attached property: the URL a <see cref="Run"/> opens when its host
    /// text block dispatches a tap inside the run's glyph range.
    /// </summary>
    public static readonly AttachedProperty<string?> UrlProperty =
        AvaloniaProperty.RegisterAttached<Inline, string?>(
            "Url",
            typeof(HtmlInlinesBuilder));

    public static string? GetUrl(Inline element)
    {
        return element.GetValue(UrlProperty);
    }

    public static void SetUrl(Inline element, string? value)
    {
        element.SetValue(UrlProperty, value);
    }

    /// <summary>
    /// Opens <paramref name="url"/> with the OS default handler.
    /// </summary>
    public static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch
        {
            // Best-effort: an invalid URL or a sandboxed shell just falls
            // through silently. The link still selects/copies as text.
        }
    }
}
