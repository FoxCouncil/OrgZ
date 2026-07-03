// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Net.Http;

namespace OrgZ.StationCurator.Services;

/// <summary>Shared HTTP plumbing. Every outbound request carries a stock browser UA.</summary>
public static class Web
{
    public const string BrowserUa = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    public static readonly HttpClient Http = Create(TimeSpan.FromSeconds(20));

    public static HttpClient Create(TimeSpan timeout, bool allowRedirects = true)
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = allowRedirects, MaxAutomaticRedirections = 10 })
        {
            Timeout = timeout,
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", BrowserUa);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
        return client;
    }
}

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
