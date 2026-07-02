// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Models;

/// <summary>Which vertical an <see cref="AcquiredMedia"/> row belongs to.</summary>
public enum AcquiredMediaKind
{
    Podcast,
    Audiobook,
}

/// <summary>
/// A persisted "I acquired this" record - a podcast subscription or an audiobook acquisition -
/// that survives the deletion of any downloaded file. It is the record layer that lets a book/show
/// remember you got it even when its bytes are gone: the item stays visible as "acquired, not
/// downloaded" and offers a re-download instead of vanishing.
///
/// Deliberately separate from the downloaded bytes: presence on disk is a runtime probe
/// (<see cref="Services.Media.MediaDownloadState"/>), this is the durable intent. A store-sourced
/// acquisition carries a <see cref="SourceRefJson"/> so it can be re-fetched; a file the user
/// simply dropped into the vertical's folder is <see cref="IsUserProvided"/> with no source - for
/// those, deleting the file is a real delete, there being nothing to re-download.
/// </summary>
public sealed record AcquiredMedia
{
    /// <summary>Which vertical this belongs to. With <see cref="SourceKey"/>, the composite identity.</summary>
    public required AcquiredMediaKind Kind { get; init; }

    /// <summary>
    /// Stable identity within <see cref="Kind"/>: a podcast feed id as text, an audiobook store
    /// identifier (archive.org id or <c>libro:{isbn}</c>), or a synthesized key for a user-dropped
    /// item. Unique per kind - the store's primary key is (Kind, SourceKey).
    /// </summary>
    public required string SourceKey { get; init; }

    public string? Title       { get; init; }
    public string? Creator     { get; init; }
    public string? ImageUrl    { get; init; }
    public string? HomepageUrl { get; init; }

    /// <summary>
    /// Serialized hint for re-fetching this item from its source (the store identifier / manifest
    /// pointer the download service needs). Null when there is nothing to re-fetch - i.e. a
    /// user-provided file (<see cref="IsUserProvided"/>).
    /// </summary>
    public string? SourceRefJson { get; init; }

    /// <summary>
    /// True when this record was created from a file the user dropped into the vertical's folder
    /// rather than acquired from a store. Such an item has no re-download source, so removing its
    /// file removes it for good.
    /// </summary>
    public bool IsUserProvided { get; init; }

    public DateTime AcquiredAt { get; init; }
}
