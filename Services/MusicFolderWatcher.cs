// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Threading.Channels;

namespace OrgZ.Services;

public sealed class MusicFolderWatcher : IDisposable
{
    private static readonly HashSet<string> TempSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        ".tmp", ".part", ".crdownload", ".partial", ".partial-rip", ".orgztmp"
    };

    private FileSystemWatcher? _watcher;
    private Channel<FsEvent>? _channel;
    private Task? _consumerTask;
    private CancellationTokenSource? _cts;
    private long _lastOverflowTicks;

    public event Action<WatcherChangeSet>? ChangesDetected;
    public event Action? FullRescanNeeded;

    public void Start(string folderPath)
    {
        Stop();

        if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
        {
            return;
        }

        _cts = new CancellationTokenSource();
        // TryWrite never reports a drop in a drop-mode channel, so the queue overflow is caught
        // through the itemDropped callback instead: a dropped event is a change the library would
        // never hear about, which only a full rescan can reconcile.
        _channel = Channel.CreateBounded<FsEvent>(new BoundedChannelOptions(4096)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        }, _ => SignalQueueOverflow());

        _watcher = new FileSystemWatcher(folderPath)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 65536,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.DirectoryName
        };

        _watcher.Created += (_, e) => Enqueue(FsChangeKind.Created, e.FullPath);
        _watcher.Deleted += (_, e) => Enqueue(FsChangeKind.Deleted, e.FullPath);
        _watcher.Changed += (_, e) => Enqueue(FsChangeKind.Changed, e.FullPath);
        _watcher.Renamed += (_, e) =>
        {
            Enqueue(FsChangeKind.Deleted, e.OldFullPath);

            // A renamed/moved DIRECTORY is reported once, for the folder itself - the files inside
            // it get no events at all. The Deleted above sweeps every tracked track under the old
            // folder out of the library, and a folder path can't survive Enqueue's audio-extension
            // filter, so the tracks have to be re-added by walking the new location.
            if (Directory.Exists(e.FullPath))
            {
                EnqueueDirectoryContents(e.FullPath);
            }
            else
            {
                Enqueue(FsChangeKind.Created, e.FullPath);
            }
        };
        _watcher.Error += (_, e) =>
        {
            FullRescanNeeded?.Invoke();
        };

        _consumerTask = Task.Run(() => ConsumeLoop(_cts.Token));

        _watcher.EnableRaisingEvents = true;
    }

    public void Stop()
    {
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }

        if (_cts != null)
        {
            _cts.Cancel();
            _cts.Dispose();
            _cts = null;
        }

        _channel = null;
        _consumerTask = null;
    }

    public void Dispose()
    {
        Stop();
    }

    private void Enqueue(FsChangeKind kind, string path)
    {
        if (IsTempFile(path))
        {
            return;
        }

        // Skip dot-prefixed subdirectories (.podcasts/, .disc-images/) - those belong to
        // sibling subsystems and would otherwise show up in Music. EXCEPT .audiobooks:
        // FileScanner walks it (audiobooks are library content), so the watcher must see
        // it too - a book dropped in used to need a full manual rescan to appear.
        if (IsInDotSubdirectory(path))
        {
            return;
        }

        // For Created/Changed, only accept supported audio extensions.
        // For Deleted, accept all - the file is gone so we can't check,
        // and the consumer will only act if the path was tracked.
        if (kind != FsChangeKind.Deleted && !FileScanner.IsSupportedExtension(path))
        {
            return;
        }

        _channel?.Writer.TryWrite(new FsEvent(kind, path));
    }

    /// <summary>
    /// Enqueues Created for every supported audio file under <paramref name="directory"/>, so a
    /// folder that arrived under a new name is re-added without waiting for a manual rescan. If the
    /// folder can't be read (it moved again, or vanished) the library is out of step with the disk,
    /// which is exactly what a full rescan is for.
    /// </summary>
    private void EnqueueDirectoryContents(string directory)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
        };

        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*", options))
            {
                Enqueue(FsChangeKind.Created, path);
            }
        }
        catch (Exception)
        {
            FullRescanNeeded?.Invoke();
        }
    }

    /// <summary>
    /// The queue overflowed and events were discarded, so the library's picture of the folder is no
    /// longer trustworthy - take the same recovery a native buffer overflow does. Throttled, because
    /// a long burst drops many events and each one lands here.
    /// </summary>
    private void SignalQueueOverflow()
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastOverflowTicks);

        if (now - last < TimeSpan.TicksPerMinute)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastOverflowTicks, now, last) != last)
        {
            return;
        }

        FullRescanNeeded?.Invoke();
    }

    private static bool IsInDotSubdirectory(string path)
    {
        var dir = Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(dir))
        {
            var name = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(name)) break;
            if (name.StartsWith('.') && !name.Equals(".audiobooks", StringComparison.OrdinalIgnoreCase)) return true;
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir) break;
            dir = parent;
        }
        return false;
    }

    private async Task ConsumeLoop(CancellationToken ct)
    {
        var reader = _channel!.Reader;
        var pending = new Dictionary<string, FsChangeKind>(StringComparer.OrdinalIgnoreCase);

        while (!ct.IsCancellationRequested)
        {
            pending.Clear();

            // Wait for the first event (blocks until something arrives or cancelled)
            try
            {
                var first = await reader.ReadAsync(ct);
                Coalesce(pending, first);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            // Debounce: drain everything that arrives within a sliding 250ms window,
            // up to a 2-second ceiling from the first event. Kept short so the library
            // tracks the filesystem snappily - ApplyFilter is only a few ms even at 30k
            // items - while still coalescing the rapid bursts of a bulk copy/delete.
            var ceiling = DateTime.UtcNow.AddSeconds(2);

            while (DateTime.UtcNow < ceiling)
            {
                try
                {
                    using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    delayCts.CancelAfter(250);

                    var next = await reader.ReadAsync(delayCts.Token);
                    Coalesce(pending, next);
                }
                catch (OperationCanceledException)
                {
                    // Either the 500ms window expired (debounce done) or the watcher was stopped.
                    break;
                }
            }

            if (ct.IsCancellationRequested)
            {
                break;
            }

            // Build change set
            var created = new List<string>();
            var deleted = new List<string>();
            var changed = new List<string>();

            foreach (var (path, kind) in pending)
            {
                switch (kind)
                {
                    case FsChangeKind.Created:
                    {
                        created.Add(path);
                    }
                    break;

                    case FsChangeKind.Deleted:
                    {
                        deleted.Add(path);
                    }
                    break;

                    case FsChangeKind.Changed:
                    {
                        changed.Add(path);
                    }
                    break;
                }
            }

            if (created.Count > 0 || deleted.Count > 0 || changed.Count > 0)
            {
                ChangesDetected?.Invoke(new WatcherChangeSet(created, deleted, changed));
            }
        }
    }

    internal static void Coalesce(Dictionary<string, FsChangeKind> pending, FsEvent evt)
    {
        if (pending.TryGetValue(evt.Path, out var existing))
        {
            // Deleted then Created = Changed (file replaced)
            if (existing == FsChangeKind.Deleted && evt.Kind == FsChangeKind.Created)
            {
                pending[evt.Path] = FsChangeKind.Changed;
            }
            // Created then Deleted = cancel out (net no-op)
            else if (existing == FsChangeKind.Created && evt.Kind == FsChangeKind.Deleted)
            {
                pending.Remove(evt.Path);
            }
            // Any other combo, latest wins
            else
            {
                pending[evt.Path] = evt.Kind;
            }
        }
        else
        {
            pending[evt.Path] = evt.Kind;
        }
    }

    internal static bool IsTempFile(string path)
    {
        var name = Path.GetFileName(path);

        if (string.IsNullOrEmpty(name))
        {
            return true;
        }

        if (name.StartsWith('~') || name.StartsWith('.'))
        {
            return true;
        }

        var ext = Path.GetExtension(name);

        return TempSuffixes.Contains(ext);
    }

    internal enum FsChangeKind { Created, Deleted, Changed }

    internal readonly record struct FsEvent(FsChangeKind Kind, string Path);
}

public sealed record WatcherChangeSet(
    List<string> Created,
    List<string> Deleted,
    List<string> Changed
);
