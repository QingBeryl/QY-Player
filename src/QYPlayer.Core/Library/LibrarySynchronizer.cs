using QYPlayer.Core.Metadata;
using QYPlayer.Core.Models;

namespace QYPlayer.Core.Library;

/// <summary>
/// 一次曲库同步的结果。
/// </summary>
/// <param name="Tracks">同步后的完整曲目列表，已按路径升序排列。</param>
/// <param name="NeedUpsert">需要写回数据库的曲目（新增的，或文件已变化的）。</param>
/// <param name="RemovedPaths">已从磁盘消失、需要从库里删除的路径。</param>
/// <param name="ParsedCount">本次真正重新解析元数据的条数。</param>
public sealed record LibrarySyncResult(
    IReadOnlyList<Track> Tracks,
    IReadOnlyList<Track> NeedUpsert,
    IReadOnlyList<string> RemovedPaths,
    int ParsedCount);

/// <summary>
/// 曲库同步：把磁盘现状与库里的记录对齐。
/// </summary>
/// <remarks>
/// <para>
/// 对应需求文档 8.2 的结论——「监听负责运行期的即时反应，启动时的增量比对负责兜底，
/// 两者统一按大小加修改时间判断」。因此本类只有一个入口：
/// 拿一份「已知的曲目」和一组根目录，产出一份「应当成为的曲目」。
/// 启动时的全量兜底与运行期收到文件变动后的重扫走的是同一条路径，
/// 判定口径不会出现两套实现。
/// </para>
/// <para>
/// 之所以把这段逻辑从 ViewModel 里提出来：增量比对的正确性（尤其是
/// 「没变的不重新解析」）是 8.2 的核心承诺，必须能被单测直接断言，
/// 而放在 ViewModel 里就得连 WPF 一起启动才能测。
/// </para>
/// </remarks>
public sealed class LibrarySynchronizer
{
    private readonly LibraryScanner _scanner;

    public LibrarySynchronizer(LibraryScanner scanner)
    {
        ArgumentNullException.ThrowIfNull(scanner);

        _scanner = scanner;
    }

    /// <summary>
    /// 按「大小 + 最后写入时间」做增量比对，只重新解析真正变化过的文件。
    /// </summary>
    /// <param name="known">库里已有的曲目，用于跳过未变化的文件。可为空集合。</param>
    /// <param name="roots">曲库根目录。</param>
    /// <param name="progress">进度回调，可为 null。</param>
    /// <param name="cancellationToken">取消标记。</param>
    public async Task<LibrarySyncResult> SyncAsync(
        IReadOnlyCollection<Track> known,
        IEnumerable<string> roots,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(known);
        ArgumentNullException.ThrowIfNull(roots);

        var files = _scanner.ScanDirectories(roots, cancellationToken);

        // 只有「确实存在、能被扫到」的根目录才算「我检查过了」。
        // 盘没接上、目录被删这类情况必须排除在外，否则一次拔盘就会
        // 把整个曲库判成「文件已消失」而清空，重接后所有标签都要重解析。
        var reachableRoots = LibraryScanner
            .NormalizePaths(roots)
            .Where(Directory.Exists)
            .ToList();

        // 路径是曲目在磁盘上的身份，比对与查表都按它走，且忽略大小写。
        var knownByPath = new Dictionary<string, Track>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in known)
        {
            knownByPath[track.FilePath] = track;
        }

        var reused = new Dictionary<string, Track>(StringComparer.OrdinalIgnoreCase);
        var changed = new List<ScannedFile>();

        foreach (var file in files)
        {
            if (knownByPath.TryGetValue(file.FilePath, out var existing)
                && LibraryScanner.IsUnchanged(existing, file))
            {
                // 文件事实与库里一致，直接复用旧记录，不读文件内容。
                reused[file.FilePath] = existing;
                continue;
            }

            changed.Add(file);
        }

        // 只有变化过的那些文件才会走到元数据解析这一步，这就是增量的落点：
        // 一万首曲子里改了 3 首，就只解析 3 首。
        var refreshed = new Dictionary<string, Track>(StringComparer.OrdinalIgnoreCase);
        var needUpsert = new List<Track>(changed.Count);

        await foreach (var track in _scanner
                           .ReadTracksAsync(changed, progress, cancellationToken)
                           .ConfigureAwait(false))
        {
            refreshed[track.FilePath] = track;
            needUpsert.Add(track);
        }

        // 顺序与 SqliteLibraryStore.LoadAsync 保持一致（按路径升序），
        // 否则同步一次列表就会整块重排，用户滚动的位置会跳掉。
        var tracks = new List<Track>(files.Count);
        foreach (var file in files)
        {
            var track = refreshed.TryGetValue(file.FilePath, out var fresh)
                ? fresh
                : reused.GetValueOrDefault(file.FilePath);

            if (track is not null)
            {
                tracks.Add(track);
            }
        }

        tracks.Sort(static (left, right) =>
            string.Compare(left.FilePath, right.FilePath, StringComparison.OrdinalIgnoreCase));

        // 只有在「可扫到的根目录」之下的记录才会被判为消失。
        // 不在这之下的（盘没接上）原样保留，等它回来。
        var removed = known
            .Select(track => track.FilePath)
            .Where(path => IsGone(path, reachableRoots, refreshed, reused))
            .ToList();

        return new LibrarySyncResult(tracks, needUpsert, removed, changed.Count);
    }

    /// <summary>判断一条已有记录是否确实已经从磁盘上消失。</summary>
    private static bool IsGone(
        string path,
        IReadOnlyList<string> reachableRoots,
        Dictionary<string, Track> refreshed,
        Dictionary<string, Track> reused)
    {
        // 这次扫到了就不算消失，无论它是新解析的还是一直没变的。
        if (refreshed.ContainsKey(path) || reused.ContainsKey(path))
        {
            return false;
        }

        // 不在可扫到的根目录之下，说明该目录这次没参与扫描（例如盘没接上），
        // 不能据此认定文件被删。
        return LibraryScanner.IsUnderAnyRoot(path, reachableRoots);
    }
}
