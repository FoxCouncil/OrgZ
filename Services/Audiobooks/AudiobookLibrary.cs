// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Text.Json;
using OrgZ.Models;
using OrgZ.Services.Media;

namespace OrgZ.Services.Audiobooks;

/// <summary>
/// The audiobook record layer on top of <see cref="AcquisitionStore"/> - the piece that makes a
/// book behave like a podcast: acquiring it (downloading from a store) leaves a durable record, so
/// deleting the file downgrades it to "acquired, not downloaded" and re-download instead of erasing
/// it. A file the user simply drops into <c>.audiobooks</c> is adopted as a <em>user-provided</em>
/// acquisition with no source - and when such a file disappears the record is forgotten too, since
/// there is nothing to re-fetch (a real delete, matching the file's own semantics).
///
/// The join between a scanned file and its acquisition is the book directory
/// (<c>{root}/.audiobooks/{Author}/{Title}</c>): <see cref="AudiobookDownloadService.TargetDirectoryFor"/>
/// reproduces it from a record's Creator/Title, and <see cref="AudiobookDetector.BookFolderFor"/>
/// derives it from any file inside the book.
/// </summary>
public static class AudiobookLibrary
{
    /// <summary>The re-fetch hint persisted on a store acquisition (which store, and its identity there).</summary>
    private sealed record SourceRef(string Source, string Id);

    private static string Serialize(SourceRef r) => JsonSerializer.Serialize(r);

    /// <summary>Reads a persisted source hint; null when the record carries none (user-provided).</summary>
    public static (string Source, string Id)? SourceOf(AcquiredMedia media)
    {
        if (string.IsNullOrWhiteSpace(media.SourceRefJson))
        {
            return null;
        }
        try
        {
            var r = JsonSerializer.Deserialize<SourceRef>(media.SourceRefJson);
            return r is null ? null : (r.Source, r.Id);
        }
        catch
        {
            return null;
        }
    }

    // ── recording an acquisition (called when a download starts) ────────────────

    /// <summary>Records that the user acquired an archive.org book - re-fetchable by its identifier.</summary>
    public static void RecordArchiveAcquisition(AudiobookListing book) => AcquisitionStore.Acquire(new AcquiredMedia
    {
        Kind          = AcquiredMediaKind.Audiobook,
        SourceKey     = book.Identifier,
        Title         = book.Title,
        Creator       = book.Creator,
        ImageUrl      = book.CoverUrl,
        SourceRefJson = Serialize(new SourceRef("archive", book.Identifier)),
    });

    /// <summary>
    /// Records a Libro.fm purchase. Re-download needs a live session token (not stored), so the
    /// source hint carries only the ISBN - the caller checks for a token when offering re-download.
    /// </summary>
    public static void RecordLibroAcquisition(LibroBook book)
    {
        var listing = AudiobookDownloadService.ListingFor(book);
        AcquisitionStore.Acquire(new AcquiredMedia
        {
            Kind          = AcquiredMediaKind.Audiobook,
            SourceKey     = listing.Identifier,          // "libro:{isbn}"
            Title         = listing.Title,
            Creator       = listing.Creator,
            ImageUrl      = book.CoverUrl,
            SourceRefJson = Serialize(new SourceRef("libro", book.Isbn)),
        });
    }

    // ── reconciliation ──────────────────────────────────────────────────────────

    /// <summary>
    /// Store-sourced acquisitions whose bytes are NOT currently on disk - the set the library should
    /// surface as "acquired, not downloaded" with a re-download action. User-provided records are
    /// excluded: they have no source to re-fetch.
    /// </summary>
    public static List<AcquiredMedia> ReDownloadable(string? libraryRoot)
    {
        var result = new List<AcquiredMedia>();
        foreach (var a in AcquisitionStore.GetAll(AcquiredMediaKind.Audiobook))
        {
            if (a.IsUserProvided)
            {
                continue;
            }
            if (AudiobookDownloadService.Instance.GetState(ListingFrom(a), libraryRoot) != AudiobookDownloadState.Downloaded)
            {
                result.Add(a);
            }
        }
        return result;
    }

    /// <summary>
    /// Folds the scanned <c>.audiobooks</c> files against the acquisition records: a book folder with
    /// no record is adopted as a user-provided acquisition (dropping a file in IS the acquire gesture),
    /// and a user-provided record whose files have vanished is forgotten (nothing to re-fetch, so its
    /// delete is real). Store-sourced records are left untouched here - a missing store download stays
    /// as a re-downloadable record, which is the whole point.
    /// </summary>
    public static void ReconcileUserFiles(string? libraryRoot, IEnumerable<string> scannedAudiobookFiles)
    {
        if (string.IsNullOrWhiteSpace(libraryRoot))
        {
            return;
        }

        // Book directories present on disk, from the scanned files. Files too shallow to belong to a
        // book folder (directly in .audiobooks or an author folder) have no BookFolderFor and are skipped.
        var onDisk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in scannedAudiobookFiles)
        {
            if (AudiobookDetector.BookFolderFor(file) is { } dir)
            {
                onDisk.Add(dir);
            }
        }

        // Every known acquisition keyed by the book directory it maps to.
        var knownByDir = new Dictionary<string, AcquiredMedia>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in AcquisitionStore.GetAll(AcquiredMediaKind.Audiobook))
        {
            knownByDir[AudiobookDownloadService.TargetDirectoryFor(libraryRoot, ListingFrom(a))] = a;
        }

        // Adopt: a book on disk that no record claims becomes a user-provided acquisition.
        foreach (var dir in onDisk)
        {
            if (!knownByDir.ContainsKey(dir))
            {
                AcquisitionStore.Acquire(new AcquiredMedia
                {
                    Kind           = AcquiredMediaKind.Audiobook,
                    SourceKey      = "local:" + Path.GetRelativePath(libraryRoot, dir),
                    Title          = Path.GetFileName(dir),
                    Creator        = Path.GetFileName(Path.GetDirectoryName(dir) ?? "") is { Length: > 0 } author ? author : "Unknown Author",
                    IsUserProvided = true,
                });
            }
        }

        // Prune: a user-provided record whose files are gone is forgotten - a real delete.
        foreach (var (dir, a) in knownByDir)
        {
            if (a.IsUserProvided && !onDisk.Contains(dir))
            {
                AcquisitionStore.Release(AcquiredMediaKind.Audiobook, a.SourceKey);
            }
        }
    }

    /// <summary>The store listing an acquisition maps to - enough to locate its folder and re-fetch it.</summary>
    public static AudiobookListing ListingFrom(AcquiredMedia media) => new()
    {
        Identifier = media.SourceKey,
        Title      = media.Title,
        Creator    = media.Creator,
    };
}
