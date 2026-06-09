using System.Collections.Concurrent;
using System.Threading.Channels;

namespace SemanticSearch;

/// <summary>
/// Runs ssearch as a long-lived service: it keeps an on-disk index in sync with a watched files
/// directory and answers interactive queries against the live, in-memory index.
/// <para>
/// File changes are coalesced over a short debounce window, then applied incrementally - the records
/// for a changed file are removed and the file is re-read, language-detected, embedded and re-added.
/// The index is persisted atomically after each batch. A <c>relative-path -&gt; record-ids</c> map is
/// maintained so deletes are precise without scanning the whole collection.
/// </para>
/// </summary>
public sealed class IndexService
{
    private enum ChangeKind
    {
        Upsert,
        Delete,
    }

    private readonly SearchIndex _index;
    private readonly string _root;
    private readonly string _indexPath;
    private readonly int _debounceMs;
    private readonly TextWriter _log;
    private readonly ConcurrentDictionary<string, List<Guid>> _fileRecords = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<(string Path, ChangeKind Kind)> _events =
        Channel.CreateUnbounded<(string, ChangeKind)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly SemaphoreSlim _persistLock = new(1, 1);

    /// <param name="log">
    /// Where progress and status messages are written. Defaults to standard output (used by the
    /// interactive <c>serve</c> command); the <c>mcp</c> command passes standard error so it never
    /// corrupts the JSON-RPC protocol stream on standard output.
    /// </param>
    public IndexService(SearchIndex index, string root, string indexPath, int debounceMs, TextWriter? log = null)
    {
        _index = index;
        _root = Path.GetFullPath(root);
        _indexPath = Path.GetFullPath(indexPath);
        _debounceMs = debounceMs;
        _log = log ?? Console.Out;
    }

    /// <summary>The number of distinct files currently represented in the index.</summary>
    public int FileCount => _fileRecords.Count;

    /// <summary>Searches the live, in-memory index. Safe to call while the watcher is re-indexing.</summary>
    public Task<IReadOnlyList<(SearchRecord Record, float Score)>> SearchAsync(string query, int top) =>
        _index.SearchAsync(query, top);

    /// <summary>Counts the passages currently in the index.</summary>
    public Task<int> CountAsync() => _index.CountAsync();

    /// <summary>Loads an existing index (rebuilding the file map) or builds a fresh one from the watched directory.</summary>
    public async Task InitializeAsync()
    {
        if (File.Exists(_indexPath))
        {
            _log.WriteLine($"Loading index {_indexPath} ...");
            using (FileStream stream = File.OpenRead(_indexPath))
            {
                _index.Collection.Load(stream, SearchSerializerContext.Default);
            }

            await RebuildFileMapAsync().ConfigureAwait(false);
            _log.WriteLine($"Loaded {_fileRecords.Count} files. Watching for changes ...");
        }
        else
        {
            _log.WriteLine($"Building index from {_root} ...");
            await IndexAllAsync().ConfigureAwait(false);
            await PersistAsync().ConfigureAwait(false);
            _log.WriteLine($"Indexed {_fileRecords.Count} files to {_indexPath}. Watching for changes ...");
        }
    }

    /// <summary>
    /// Keeps the index in sync with the watched directory until <paramref name="cancellationToken" />
    /// is signaled, then persists. Run this concurrently with a front end (the interactive console in
    /// <c>serve</c>, or the MCP server in <c>mcp</c>) that answers queries against the live index.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using FileSystemWatcher watcher = CreateWatcher();
        Task consumer = Task.Run(() => ConsumeAsync(cancellationToken), CancellationToken.None);

        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown requested.
        }

        _events.Writer.TryComplete();
        await consumer.ConfigureAwait(false);
        await PersistAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the interactive console prompt on a dedicated thread (it blocks on <see cref="Console.ReadLine" />,
    /// so it must never hold a thread-pool thread the background indexer needs). Returns when the user
    /// quits or end-of-input is reached.
    /// </summary>
    public Task RunInteractiveAsync(CancellationToken cancellationToken) =>
        Task.Factory.StartNew(
            () => InteractiveLoop(cancellationToken),
            cancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    private FileSystemWatcher CreateWatcher()
    {
        var watcher = new FileSystemWatcher(_root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024,
        };

        watcher.Created += (_, e) => Enqueue(e.FullPath, ChangeKind.Upsert);
        watcher.Changed += (_, e) => Enqueue(e.FullPath, ChangeKind.Upsert);
        watcher.Deleted += (_, e) => Enqueue(e.FullPath, ChangeKind.Delete);
        watcher.Renamed += (_, e) =>
        {
            Enqueue(e.OldFullPath, ChangeKind.Delete);
            Enqueue(e.FullPath, ChangeKind.Upsert);
        };
        watcher.Error += (_, e) => Console.Error.WriteLine($"[watcher] {e.GetException().Message}");
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    private void Enqueue(string path, ChangeKind kind)
    {
        // Cheap early filter for noisy build/tooling directories; full inclusion is checked when processing.
        if (FileSource.IsInSkippedDirectory(_root, path))
        {
            return;
        }

        _events.Writer.TryWrite((Path.GetFullPath(path), kind));
    }

    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _events.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                // Coalesce a burst of events (e.g. a save that fires multiple notifications, or a bulk
                // checkout) into a single pass keyed by relative path, last event winning.
                var pending = new Dictionary<string, (string Path, ChangeKind Kind)>(StringComparer.OrdinalIgnoreCase);
                Drain(pending);
                await Task.Delay(_debounceMs, cancellationToken).ConfigureAwait(false);
                Drain(pending);

                int changed = 0;
                foreach ((string Path, ChangeKind Kind) item in pending.Values)
                {
                    changed += await ApplyAsync(item.Path, item.Kind).ConfigureAwait(false);
                }

                if (changed > 0)
                {
                    await PersistAsync().ConfigureAwait(false);
                    _log.WriteLine($"\n[index updated: {changed} file(s)]");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown requested.
        }
    }

    private void Drain(Dictionary<string, (string Path, ChangeKind Kind)> pending)
    {
        while (_events.Reader.TryRead(out (string Path, ChangeKind Kind) e))
        {
            pending[FileSource.RelativePath(_root, e.Path)] = e;
        }
    }

    private async Task<int> ApplyAsync(string path, ChangeKind kind)
    {
        string relative = FileSource.RelativePath(_root, path);

        if (kind == ChangeKind.Delete)
        {
            return await RemoveFileAndSubtreeAsync(relative).ConfigureAwait(false);
        }

        // A file we've already indexed passed the git-ignore check once; don't pay for it on every edit.
        // New files are indexed optimistically and verified out of band (see VerifyNotIgnored) so the
        // indexing path never blocks on a git subprocess.
        bool known = _fileRecords.ContainsKey(relative);

        List<Passage> passages = [.. FileSource.EnumerateFile(_root, path, checkGitIgnore: false)];
        if (passages.Count == 0)
        {
            // Not indexable (wrong type or vanished) - drop any prior records for it.
            return await RemoveFileRecordsAsync(relative) ? 1 : 0;
        }

        await RemoveFileRecordsAsync(relative).ConfigureAwait(false);
        IReadOnlyList<Guid> ids = await _index.AddAsync(passages).ConfigureAwait(false);
        _fileRecords[relative] = [.. ids];

        if (!known)
        {
            VerifyNotIgnored(path);
        }

        return 1;
    }

    /// <summary>
    /// Checks a newly indexed file's git-ignore status away from the indexing path. If it turns out to be
    /// ignored, a delete is routed back through the event channel so the prune happens on the single
    /// consumer thread, keeping all index mutations single-writer.
    /// </summary>
    private void VerifyNotIgnored(string path)
    {
        _ = Task.Run(() =>
        {
            if (FileSource.IsPathGitIgnored(_root, path))
            {
                _events.Writer.TryWrite((path, ChangeKind.Delete));
            }
        });
    }

    private async Task<bool> RemoveFileRecordsAsync(string relative)
    {
        if (_fileRecords.TryRemove(relative, out List<Guid>? ids))
        {
            await _index.RemoveAsync(ids).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    private async Task<int> RemoveFileAndSubtreeAsync(string relative)
    {
        int removed = await RemoveFileRecordsAsync(relative) ? 1 : 0;

        string prefix = relative + "/";
        List<string> subtree = _fileRecords.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (string key in subtree)
        {
            removed += await RemoveFileRecordsAsync(key) ? 1 : 0;
        }

        return removed;
    }

    private void InteractiveLoop(CancellationToken cancellationToken)
    {
        Console.WriteLine("Type a query and press Enter. Commands: ':count' for index size, ':quit' to stop.");
        while (!cancellationToken.IsCancellationRequested)
        {
            Console.Write("\nsearch> ");
            string? line = Console.ReadLine();
            if (line is null)
            {
                break;
            }

            line = line.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line is ":quit" or ":q")
            {
                break;
            }

            if (line is ":count")
            {
                long count = _index.CountAsync().GetAwaiter().GetResult();
                Console.WriteLine($"{count} passages across {_fileRecords.Count} files.");
                continue;
            }

            IReadOnlyList<(SearchRecord Record, float Score)> results =
                _index.SearchAsync(line, top: 5).GetAwaiter().GetResult();
            ConsoleResults.Print(results);
        }
    }

    private async Task IndexAllAsync()
    {
        foreach (string file in EnumerateIndexableFiles())
        {
            await ApplyAsync(file, ChangeKind.Upsert).ConfigureAwait(false);
        }
    }

    private IEnumerable<string> EnumerateIndexableFiles()
    {
        // Group the directory walk by file so each file's records are tracked together in the map.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Passage passage in FileSource.Enumerate(_root))
        {
            int colon = passage.Location.LastIndexOf(':');
            string relative = colon > 0 ? passage.Location[..colon] : passage.Location;
            if (seen.Add(relative))
            {
                yield return Path.GetFullPath(Path.Combine(_root, relative));
            }
        }
    }

    private async Task RebuildFileMapAsync()
    {
        _fileRecords.Clear();
        await foreach (SearchRecord record in _index.EnumerateAllAsync().ConfigureAwait(false))
        {
            if (!string.Equals(record.Source, "file", StringComparison.Ordinal))
            {
                continue;
            }

            int colon = record.Location.LastIndexOf(':');
            string relative = colon > 0 ? record.Location[..colon] : record.Location;
            if (!_fileRecords.TryGetValue(relative, out List<Guid>? ids))
            {
                ids = [];
                _fileRecords[relative] = ids;
            }

            ids.Add(record.Id);
        }
    }

    /// <summary>Persists the current index to disk on demand (used for graceful shutdown).</summary>
    public Task FlushAsync() => PersistAsync();

    private async Task PersistAsync()
    {
        await _persistLock.WaitAsync().ConfigureAwait(false);
        try
        {
            string temp = _indexPath + ".tmp";
            using (FileStream stream = File.Create(temp))
            {
                _index.Collection.Save(stream, SearchSerializerContext.Default);
            }

            File.Move(temp, _indexPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[persist] failed to write index: {ex.Message}");
        }
        finally
        {
            _persistLock.Release();
        }
    }
}
