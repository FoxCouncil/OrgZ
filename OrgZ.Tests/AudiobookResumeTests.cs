// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Services.Audiobooks;

namespace OrgZ.Tests;

/// <summary>
/// Book-level resume: which chapter a book resumes at and the shelf's listened fraction,
/// both derived from per-chapter LastPositionMs / LastPlayed under a linear-listening
/// assumption. A finished chapter has position 0 and LastPlayed stamped (the finish
/// handler's contract); an in-progress one carries a &gt;10s position.
/// </summary>
public class AudiobookResumeTests
{
    private static MediaItem Chapter(int index, long positionMs = 0, bool played = false, int minutes = 30) => new()
    {
        Id = $"ch-{index}",
        Kind = MediaKind.Audiobook,
        Duration = TimeSpan.FromMinutes(minutes),
        LastPositionMs = positionMs,
        LastPlayed = played ? new DateTime(2026, 8, 1, 0, 0, index, DateTimeKind.Utc) : null,
    };

    [Fact]
    public void Fresh_book_starts_at_chapter_one()
    {
        var chapters = new List<MediaItem> { Chapter(0), Chapter(1), Chapter(2) };

        Assert.Equal(0, AudiobookLibrary.ResumeChapterIndex(chapters));
        Assert.Equal(0, AudiobookLibrary.ListenProgress(chapters));
    }

    [Fact]
    public void In_progress_chapter_wins_and_progress_counts_the_chapters_before_it()
    {
        // Ch1 finished, ch2 15 minutes in (of 30-minute chapters).
        var chapters = new List<MediaItem>
        {
            Chapter(0, played: true),
            Chapter(1, positionMs: (long)TimeSpan.FromMinutes(15).TotalMilliseconds, played: true),
            Chapter(2),
        };

        Assert.Equal(1, AudiobookLibrary.ResumeChapterIndex(chapters));
        Assert.Equal(0.5, AudiobookLibrary.ListenProgress(chapters), precision: 3);   // 45 of 90 minutes
    }

    [Fact]
    public void Finished_chapter_resumes_at_the_next_one()
    {
        var chapters = new List<MediaItem> { Chapter(0, played: true), Chapter(1), Chapter(2) };

        Assert.Equal(1, AudiobookLibrary.ResumeChapterIndex(chapters));
        Assert.Equal(1.0 / 3.0, AudiobookLibrary.ListenProgress(chapters), precision: 3);
    }

    [Fact]
    public void Fully_finished_book_starts_over_and_reads_as_complete()
    {
        var chapters = new List<MediaItem> { Chapter(0, played: true), Chapter(1, played: true) };

        Assert.Equal(0, AudiobookLibrary.ResumeChapterIndex(chapters));
        Assert.Equal(1.0, AudiobookLibrary.ListenProgress(chapters), precision: 3);
    }

    [Fact]
    public void Barely_started_position_does_not_count_as_progress()
    {
        // Under the 10s resume threshold - same rule ExecutePlayMusic applies.
        var chapters = new List<MediaItem> { Chapter(0, positionMs: 5_000) };

        Assert.Equal(0, AudiobookLibrary.ResumeChapterIndex(chapters));
        Assert.Equal(0, AudiobookLibrary.ListenProgress(chapters));
    }

    [Fact]
    public void Single_file_book_resumes_within_itself()
    {
        var chapters = new List<MediaItem> { Chapter(0, positionMs: (long)TimeSpan.FromMinutes(6).TotalMilliseconds, minutes: 60) };

        Assert.Equal(0, AudiobookLibrary.ResumeChapterIndex(chapters));
        Assert.Equal(0.1, AudiobookLibrary.ListenProgress(chapters), precision: 3);
    }

    [Fact]
    public void Zero_duration_chapters_read_as_unstarted()
    {
        var chapters = new List<MediaItem> { new() { Id = "x", Kind = MediaKind.Audiobook } };

        Assert.Equal(0, AudiobookLibrary.ListenProgress(chapters));
    }
}
