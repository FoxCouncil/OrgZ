// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Net.Http;

namespace OrgZ.Services;

/// <summary>Shared HTTP plumbing for stream/metadata reads. Every outbound request carries a stock browser UA.</summary>
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
