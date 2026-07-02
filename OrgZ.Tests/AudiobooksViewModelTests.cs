// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Services.Audiobooks;
using OrgZ.ViewModels;

namespace OrgZ.Tests;

/// <summary>
/// The store panel VM's pure pieces: HTML-fragment descriptions rendered to plain text, and the
/// book-detail chapter rows built from an item's file list (via the m4b-preferred picker),
/// exercised against the captured archive.org fixture.
/// </summary>
public class AudiobooksViewModelTests
{
    // ===== StripHtml - archive.org descriptions are HTML fragments =====

    [Fact]
    public void Tags_drop()
    {
        Assert.Equal("The Count of Monte Cristo is an adventure novel.",
            AudiobooksViewModel.StripHtml("<i>The Count of Monte Cristo</i> is an <a href=\"x\">adventure</a> novel."));
    }

    [Fact]
    public void Br_and_paragraph_ends_become_line_breaks()
    {
        var text = AudiobooksViewModel.StripHtml("First line.<br />Second line.<br/>Third.");
        Assert.Equal("First line.\nSecond line.\nThird.", text);
    }

    [Fact]
    public void Entities_decode()
    {
        Assert.Equal("Dumas, père — “classic”", AudiobooksViewModel.StripHtml("Dumas, p&egrave;re &mdash; &ldquo;classic&rdquo;"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_input_returns_null(string? html)
    {
        Assert.Null(AudiobooksViewModel.StripHtml(html));
    }

    // ===== BuildChapterRows - numbered play-order rows from the real fixture =====

    [Fact]
    public void Chapter_rows_come_from_the_picked_files_numbered_in_order()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "archiveorg_metadata_item.json"));
        var item = ArchiveOrgClient.ParseJson<ArchiveItemResponse>(json)!;

        var rows = AudiobooksViewModel.BuildChapterRows(item.Files);

        // The captured item carries a two-part m4b set - rows mirror the picker's choice.
        Assert.Equal(2, rows.Count);
        Assert.Equal([1, 2], rows.Select(r => r.Number));
        Assert.All(rows, r => Assert.False(string.IsNullOrEmpty(r.Name)));
        Assert.All(rows, r => Assert.False(string.IsNullOrEmpty(r.Duration)));
    }

    [Fact]
    public void Chapter_durations_format_with_hours_when_a_part_runs_long()
    {
        List<ArchiveItemFile> files =
        [
            new() { Name = "part1.m4b", Format = "Audiobook", Length = "5:28:11" },
            new() { Name = "part2.m4b", Format = "Audiobook", Length = "45:02" },
            new() { Name = "part3.m4b", Format = "Audiobook", Length = null },
        ];

        var rows = AudiobooksViewModel.BuildChapterRows(files);

        Assert.Equal("5:28:11", rows[0].Duration);
        Assert.Equal("45:02", rows[1].Duration);
        Assert.Equal("—", rows[2].Duration);   // unknown duration renders as the em-dash empty marker
    }
}
