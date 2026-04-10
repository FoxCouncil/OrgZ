// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Xml.Linq;

namespace OrgZ.Services;

public class PlaylistImportResult
{
    public string Name { get; init; } = string.Empty;
    public List<string> TrackPaths { get; init; } = [];
}

public static class PlaylistImporter
{
    public static PlaylistImportResult Import(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".m3u" or ".m3u8" => ParseM3U(filePath),
            ".pls" => ParsePLS(filePath),
            ".xspf" => ParseXSPF(filePath),
            _ => new PlaylistImportResult { Name = Path.GetFileNameWithoutExtension(filePath) }
        };
    }

    private static PlaylistImportResult ParseM3U(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath) ?? ".";
        var name = Path.GetFileNameWithoutExtension(filePath);
        var paths = new List<string>();

        foreach (var line in File.ReadLines(filePath))
        {
            var trimmed = line.Trim();

            if (string.IsNullOrEmpty(trimmed))
            {
                continue;
            }

            if (trimmed.StartsWith("#PLAYLIST:", StringComparison.OrdinalIgnoreCase))
            {
                name = trimmed[10..].Trim();
                continue;
            }

            if (trimmed.StartsWith('#'))
            {
                continue;
            }

            // Resolve relative paths against the playlist file's directory
            var resolved = Path.IsPathRooted(trimmed)
                ? trimmed
                : Path.GetFullPath(trimmed, dir);

            paths.Add(resolved);
        }

        return new PlaylistImportResult { Name = name, TrackPaths = paths };
    }

    private static PlaylistImportResult ParsePLS(string filePath)
    {
        var name = Path.GetFileNameWithoutExtension(filePath);
        var dir = Path.GetDirectoryName(filePath) ?? ".";
        var entries = new SortedDictionary<int, string>();

        foreach (var line in File.ReadLines(filePath))
        {
            var trimmed = line.Trim();

            if (!trimmed.StartsWith("File", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var eqIdx = trimmed.IndexOf('=');
            if (eqIdx < 0)
            {
                continue;
            }

            var numStr = trimmed[4..eqIdx];
            if (!int.TryParse(numStr, out var num))
            {
                continue;
            }

            var path = trimmed[(eqIdx + 1)..].Trim();
            var resolved = Path.IsPathRooted(path)
                ? path
                : Path.GetFullPath(path, dir);

            entries[num] = resolved;
        }

        return new PlaylistImportResult { Name = name, TrackPaths = [.. entries.Values] };
    }

    private static PlaylistImportResult ParseXSPF(string filePath)
    {
        var name = Path.GetFileNameWithoutExtension(filePath);
        var paths = new List<string>();

        try
        {
            XNamespace ns = "http://xspf.org/ns/0/";
            var doc = XDocument.Load(filePath);
            var root = doc.Root;

            var titleEl = root?.Element(ns + "title");
            if (!string.IsNullOrWhiteSpace(titleEl?.Value))
            {
                name = titleEl.Value;
            }

            var trackList = root?.Element(ns + "trackList");
            if (trackList != null)
            {
                foreach (var track in trackList.Elements(ns + "track"))
                {
                    var location = track.Element(ns + "location")?.Value;
                    if (string.IsNullOrWhiteSpace(location))
                    {
                        continue;
                    }

                    try
                    {
                        var uri = new Uri(location);
                        if (uri.IsFile)
                        {
                            paths.Add(uri.LocalPath);
                        }
                    }
                    catch
                    {
                        // If it's not a valid URI, treat it as a raw path
                        paths.Add(location);
                    }
                }
            }
        }
        catch
        {
            // Malformed XML
        }

        return new PlaylistImportResult { Name = name, TrackPaths = paths };
    }
}
