// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Services;

/// <summary>
/// Where <c>library.db</c> is, for everything that stores tables in it - <see cref="MediaCache"/>,
/// <see cref="Podcast.PodcastCache"/> and <see cref="Media.AcquisitionStore"/>.
///
/// One locator because they must never disagree. Each carried its own copy of the path logic
/// and its own override hook, which broke <see cref="MediaCache.AdoptClientDatabase"/>: the
/// background service runs as LocalSystem, whose %APPDATA% is the empty systemprofile, so a
/// share-start hands it the owner's database - but that only repointed MediaCache. The other
/// two stayed aimed at the service account's empty file.
/// </summary>
public static class LibraryDb
{
    private static readonly string DefaultDirectory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OrgZ");
    private static readonly string DefaultFilePath = System.IO.Path.Combine(DefaultDirectory, "library.db");

    /// <summary>
    /// The database file, held as a full path rather than a directory plus fixed name: the
    /// service adopts whatever file its owner names in the share-start payload, which is
    /// usually but not necessarily library.db.
    /// (FilePath, not Path: a <c>Path</c> member would shadow <see cref="System.IO.Path"/>.)
    /// </summary>
    public static string FilePath { get; private set; } = DefaultFilePath;

    /// <summary>The directory holding the database - for consumers that create it before opening.</summary>
    public static string Directory => System.IO.Path.GetDirectoryName(FilePath) ?? DefaultDirectory;

    /// <summary>The SQLite connection string every consumer opens.</summary>
    public static string ConnectionString => $"Data Source={FilePath}";

    /// <summary>Points every consumer at a specific database file. Null restores the default.</summary>
    public static void OverrideFilePath(string? path) => FilePath = path ?? DefaultFilePath;

    /// <summary>Points every consumer at library.db inside <paramref name="directory"/>. Null restores the default.</summary>
    public static void OverrideDirectory(string? directory)
        => FilePath = directory is null ? DefaultFilePath : System.IO.Path.Combine(directory, "library.db");
}
