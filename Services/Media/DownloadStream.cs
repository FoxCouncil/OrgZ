// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Buffers;

namespace OrgZ.Services.Media;

/// <summary>
/// The download read-loop the podcast and audiobook services share. It was three verbatim
/// copies - two in the audiobook service alone - each with its own progress reporting.
/// </summary>
internal static class DownloadStream
{
    private const int BufferBytes = 64 * 1024;

    /// <summary>
    /// Copies <paramref name="source"/> into <paramref name="destinationPath"/>, reporting
    /// bytes received. Returns the total written.
    ///
    /// <paramref name="onProgress"/> is throttled to ~10 reports a second, with a final
    /// report guaranteed. Unthrottled it fired once per 64 KB - about 8,000 events for a
    /// 500 MB audiobook, each one marshalled to the UI thread by the view models.
    /// </summary>
    public static async Task<long> CopyToFileAsync(
        Stream source,
        string destinationPath,
        Action<long>? onProgress,
        CancellationToken ct)
    {
        using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferBytes);
        try
        {
            long received = 0;
            long lastReportTicks = 0;
            int read;

            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                received += read;

                var now = Environment.TickCount64;
                if (onProgress is not null && now - lastReportTicks >= 100)
                {
                    lastReportTicks = now;
                    onProgress(received);
                }
            }

            // The last chunk rarely lands on a throttle boundary, so report the final total
            // unconditionally - otherwise a progress bar stops just short of full.
            onProgress?.Invoke(received);
            return received;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
