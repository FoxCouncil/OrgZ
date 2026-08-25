// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Text;
using System.Xml.Linq;

namespace OrgZ.Services;

public static class PlaylistExporter
{
    /// <summary>
    /// <paramref name="relativeTo"/> writes track paths relative to that folder. Tracks outside
    /// it stay absolute.
    /// </summary>
    public static void ExportM3U8(string outputPath, string playlistName, IReadOnlyList<MediaItem> tracks, string? relativeTo = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");
        sb.AppendLine($"#PLAYLIST:{playlistName}");

        foreach (var track in tracks)
        {
            if (string.IsNullOrEmpty(track.FilePath))
            {
                continue;
            }

            var duration = (int)(track.Duration?.TotalSeconds ?? -1);
            var display = !string.IsNullOrWhiteSpace(track.Artist)
                ? $"{track.Artist} - {track.Title ?? "Unknown"}"
                : track.Title ?? track.FileName ?? "Unknown";

            sb.AppendLine($"#EXTINF:{duration},{display}");
            sb.AppendLine(RelativeOrAbsolute(track.FilePath, relativeTo));
        }

        File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
    }

    /// <summary>
    /// Path relative to <paramref name="root"/>, or the original when it falls outside -
    /// GetRelativePath would otherwise return an unportable ".." chain.
    /// </summary>
    private static string RelativeOrAbsolute(string filePath, string? root)
    {
        if (string.IsNullOrEmpty(root))
        {
            return filePath;
        }

        try
        {
            var relative = Path.GetRelativePath(root, filePath);
            return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)
                ? filePath
                : relative;
        }
        catch (Exception)
        {
            // Different volumes, or a malformed path - absolute is still correct, just longer.
            return filePath;
        }
    }

    public static void ExportPLS(string outputPath, string playlistName, IReadOnlyList<MediaItem> tracks)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[playlist]");

        int idx = 0;
        foreach (var track in tracks)
        {
            if (string.IsNullOrEmpty(track.FilePath))
            {
                continue;
            }

            idx++;
            var display = !string.IsNullOrWhiteSpace(track.Artist)
                ? $"{track.Artist} - {track.Title ?? "Unknown"}"
                : track.Title ?? track.FileName ?? "Unknown";

            sb.AppendLine($"File{idx}={track.FilePath}");
            sb.AppendLine($"Title{idx}={display}");
            sb.AppendLine($"Length{idx}={(int)(track.Duration?.TotalSeconds ?? -1)}");
        }

        sb.AppendLine($"NumberOfEntries={idx}");
        sb.AppendLine("Version=2");

        File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
    }

    public static void ExportXSPF(string outputPath, string playlistName, IReadOnlyList<MediaItem> tracks)
    {
        XNamespace ns = "http://xspf.org/ns/0/";

        var trackList = new XElement(ns + "trackList");

        foreach (var track in tracks)
        {
            if (string.IsNullOrEmpty(track.FilePath))
            {
                continue;
            }

            var trackEl = new XElement(ns + "track",
                new XElement(ns + "location", new Uri(track.FilePath).AbsoluteUri));

            if (!string.IsNullOrWhiteSpace(track.Title))
            {
                trackEl.Add(new XElement(ns + "title", track.Title));
            }

            if (!string.IsNullOrWhiteSpace(track.Artist))
            {
                trackEl.Add(new XElement(ns + "creator", track.Artist));
            }

            if (!string.IsNullOrWhiteSpace(track.Album))
            {
                trackEl.Add(new XElement(ns + "album", track.Album));
            }

            if (track.Duration.HasValue)
            {
                trackEl.Add(new XElement(ns + "duration", (long)track.Duration.Value.TotalMilliseconds));
            }

            trackList.Add(trackEl);
        }

        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(ns + "playlist",
                new XAttribute("version", "1"),
                new XElement(ns + "title", playlistName),
                trackList));

        doc.Save(outputPath);
    }
}
