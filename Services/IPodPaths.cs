// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Services;

/// <summary>
/// The iPod on-disk layout, defined once. The <c>iPod_Control/...</c> tree was spelled out inline
/// at dozens of call sites; a single typo in one of them (writing the DB to the wrong path)
/// silently breaks a device, so the structure lives here and nowhere else.
/// </summary>
internal static class IPodPaths
{
    public static string Control(string mount) => Path.Combine(mount, "iPod_Control");
    public static string Music(string mount) => Path.Combine(mount, "iPod_Control", "Music");
    public static string Artwork(string mount) => Path.Combine(mount, "iPod_Control", "Artwork");
    public static string ITunesDir(string mount) => Path.Combine(mount, "iPod_Control", "iTunes");

    /// <summary>The classic/hash58 flat binary database.</summary>
    public static string ITunesDb(string mount) => Path.Combine(mount, "iPod_Control", "iTunes", "iTunesDB");

    /// <summary>The Nano 5G+ SQLite library bundle directory.</summary>
    public static string Itlp(string mount) => Path.Combine(mount, "iPod_Control", "iTunes", "iTunes Library.itlp");

    public static string ArtworkDb(string mount) => Path.Combine(mount, "iPod_Control", "Artwork", "ArtworkDB");
}
