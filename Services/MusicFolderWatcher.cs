// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Threading.Channels;

namespace OrgZ.Services;

public sealed class MusicFolderWatcher : IDisposable
{
    private static readonly HashSet<string> TempSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        ".tmp", ".part", ".crdownload", ".partial"
    };

    private FileSystemWatcher? _watcher;
    private Channel<FsEvent>? _channel;
    private Task? _consumerTask;
    private CancellationTokenSource? _cts;

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
        _channel = Channel.CreateBounded<FsEvent>(new BoundedChannelOptions(4096)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });

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
            Enqueue(FsChangeKind.Created, e.FullPath);
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

        // For Created/Changed, only accept supported audio extensions.
        // For Deleted, accept all — the file is gone so we can't check,
        // and the consumer will only act if the path was tracked.
        if (kind != FsChangeKind.Deleted && !FileScanner.IsSupportedExtension(path))
        {
            return;
        }

        _channel?.Writer.TryWrite(new FsEvent(kind, path));
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

            // Debounce: drain everything that arrives within a sliding 500ms window,
            // up to a 5-second ceiling from the first event.
            var ceiling = DateTime.UtcNow.AddSeconds(5);

            while (DateTime.UtcNow < ceiling)
            {
                try
                {
                    using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    delayCts.CancelAfter(500);

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

    private static void Coalesce(Dictionary<string, FsChangeKind> pending, FsEvent evt)
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

    private static bool IsTempFile(string path)
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

    private enum FsChangeKind { Created, Deleted, Changed }

    private readonly record struct FsEvent(FsChangeKind Kind, string Path);
}

public sealed record WatcherChangeSet(
    List<string> Created,
    List<string> Deleted,
    List<string> Changed
);
