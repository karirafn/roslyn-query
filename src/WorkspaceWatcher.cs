namespace RoslynQuery;

/// <summary>
/// Wraps a recursive <see cref="FileSystemWatcher"/> over a solution directory and exposes
/// a structural-dirty flag that the staleness classifier in <see cref="ReloadState"/> consumes.
///
/// <para>
/// The dirty flag is set by Created/Deleted/Renamed events on any <see cref="IsRelevant"/> path,
/// and by Changed events on project/props/lock/sln files (not <c>.cs</c> — content edits on
/// source files are handled by the incremental mtime-polling path; forcing a full reload on every
/// keystroke would defeat that tier). <see cref="ConsumeDirty"/> atomically reads-and-clears the
/// flag so a watcher event between the read and a subsequent reload is never silently dropped.
/// </para>
/// </summary>
public sealed class WorkspaceWatcher : IDisposable
{
    private static readonly HashSet<string> RelevantExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs",
        ".csproj",
        ".props",
        ".slnx",
        ".sln",
    };

    // Exact filename match (case-insensitive) — checked after extension screening.
    private const string PackagesLockJson = "packages.lock.json";

    private readonly FileSystemWatcher _watcher;
    private int _dirty;

    public WorkspaceWatcher(string solutionDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(solutionDirectory);

        _watcher = new FileSystemWatcher(solutionDirectory)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
        };

        _watcher.Created += OnCreatedOrDeleted;
        _watcher.Deleted += OnCreatedOrDeleted;
        _watcher.Renamed += OnRenamed;
        _watcher.Changed += OnChanged;
        _watcher.Error += OnError;

        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>
    /// Returns <see langword="true"/> for files that can structurally affect a Roslyn workspace:
    /// <c>*.cs</c>, <c>*.csproj</c>, <c>*.props</c>, <c>*.slnx</c>, <c>*.sln</c>, and
    /// <c>packages.lock.json</c>. Returns <see langword="false"/> for any path whose file name
    /// matches none of these, or whose path contains a <c>bin</c> or <c>obj</c> directory segment
    /// (exact segment match — <c>binary/</c> and <c>objstore/</c> are not excluded).
    /// </summary>
    internal static bool IsRelevant(string path)
    {
        // Exclude bin/ and obj/ output directories — exact path-segment match, cross-platform.
        if (ContainsBinOrObjSegment(path))
        {
            return false;
        }

        string fileName = Path.GetFileName(path);

        // packages.lock.json is a special case: the extension is .json (not in RelevantExtensions)
        // but the full filename is structurally significant.
        if (fileName.Equals(PackagesLockJson, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string extension = Path.GetExtension(path);
        return RelevantExtensions.Contains(extension);
    }

    /// <summary>
    /// Returns <see langword="true"/> when this path is relevant AND is not a <c>.cs</c> file.
    /// Changed events on <c>.cs</c> files must NOT set structural-dirty — content edits are
    /// handled by the incremental mtime-polling path.
    /// </summary>
    private static bool IsRelevantNonSource(string path)
    {
        if (!IsRelevant(path))
        {
            return false;
        }

        string extension = Path.GetExtension(path);
        return !extension.Equals(".cs", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Atomically reads and clears the dirty flag.
    /// Returns <see langword="true"/> when the flag was set.
    /// </summary>
    public bool ConsumeDirty() => Interlocked.Exchange(ref _dirty, 0) == 1;

    /// <summary>Sets the structural-dirty flag unconditionally.</summary>
    public void MarkDirty() => Interlocked.Exchange(ref _dirty, 1);

    /// <summary>
    /// Internal seam: applies the event-handling classification logic for a given change type and path.
    /// Real <see cref="FileSystemWatcher"/> event handlers delegate here; unit tests call it directly
    /// so the Created/Deleted/Renamed/Changed classification is exercised without OS events.
    /// </summary>
    internal void HandleChange(WatcherChangeTypes changeType, string path)
    {
        switch (changeType)
        {
            case WatcherChangeTypes.Created:
            case WatcherChangeTypes.Deleted:
            case WatcherChangeTypes.Renamed:
                if (IsRelevant(path))
                {
                    MarkDirty();
                }

                break;

            case WatcherChangeTypes.Changed:
                if (IsRelevantNonSource(path))
                {
                    MarkDirty();
                }

                break;
        }
    }

    public void Dispose()
    {
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
    }

    private void OnCreatedOrDeleted(object sender, FileSystemEventArgs e) =>
        HandleChange(e.ChangeType, e.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        // Check both old and new paths — a rename from or to a relevant extension is structural.
        HandleChange(WatcherChangeTypes.Renamed, e.OldFullPath);
        HandleChange(WatcherChangeTypes.Renamed, e.FullPath);
    }

    private void OnChanged(object sender, FileSystemEventArgs e) =>
        HandleChange(e.ChangeType, e.FullPath);

    private void OnError(object sender, ErrorEventArgs e) => MarkDirty();

    private static bool ContainsBinOrObjSegment(string path)
    {
        // Walk path segments and test each one for exact equality with "bin" or "obj".
        // Path.GetDirectoryName returns null at the root, terminating the loop.
        string? dir = Path.GetDirectoryName(path);

        while (dir is not null)
        {
            string segment = Path.GetFileName(dir);

            if (segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("obj", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string? parent = Path.GetDirectoryName(dir);

            if (parent == dir)
            {
                break; // reached the root
            }

            dir = parent;
        }

        return false;
    }
}
