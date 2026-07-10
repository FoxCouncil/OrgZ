// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

// The shared HTTP plumbing (Web) moved into the OrgZ app proper (Services/Web.cs) alongside
// the stream-metadata primitives so the player can use them; this file keeps the tool-only
// path helper.

namespace OrgZ.StationCurator.Services;

/// <summary>Locates repo-anchored paths regardless of where the built exe runs from.</summary>
public static class RepoPaths
{
    private static string? _root;

    /// <summary>Walks up from the exe directory until it finds the directory containing OrgZ.csproj.</summary>
    public static string Root
    {
        get
        {
            if (_root != null)
            {
                return _root;
            }

            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "OrgZ.csproj")))
                {
                    _root = dir.FullName;
                    return _root;
                }
                dir = dir.Parent;
            }

            throw new InvalidOperationException($"Could not locate the OrgZ repo root above {AppContext.BaseDirectory}");
        }
    }

    public static string CuratedJson => Path.Combine(Root, "tools", "station-curator", "curated.json");
    public static string StationsJson => Path.Combine(Root, "Assets", "stations.json");
}
