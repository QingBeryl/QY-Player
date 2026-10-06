using System.Runtime.CompilerServices;
using QYPlayer.Core.Metadata;
using QYPlayer.Core.Models;
using QYPlayer.Core.Sources;

namespace QYPlayer.Core.Library;

/// <summary>
/// 曲库扫描器，对应需求文档 4.2 的「两阶段扫描」。
/// </summary>
/// <remarks>
/// <para>
/// <b>第一阶段</b>（<see cref="ScanDirectories"/>）只遍历目录树，拿路径、大小、修改时间和按扩展名判定的格式，
/// 不读任何文件内容，因此可以很快地把列表填出来。
/// </para>
/// <para>
/// <b>第二阶段</b>（<see cref="ReadTracksAsync"/>）逐个调 <see cref="IMetadataReader"/> 解析标签、时长与封面。
/// 这一步慢，所以做成异步流：每解析出一条就交出去一条，调用方可以边收边更新界面，
/// 而不是等整批解析完才看到结果。
/// </para>
/// <para>
/// 格式判定的分工见 8.1：这里只用扩展名，文件头校验留给播放与解析元数据这两个必然要读文件的时机。
/// 因此本类不做魔数校验，扫描一万个文件也不会因此多出一次 I/O。
/// </para>
/// </remarks>
public sealed class LibraryScanner
{
    /// <summary>
    /// 参与扫描的扩展名。含加密格式——加密文件同样属于曲库，
    /// 只是没有插件时无法播放，这一点由播放链路负责提示，扫描器不做筛选。
    /// </summary>
    private static readonly HashSet<string> SupportedExtensions =
        new(AudioFormatDetector.SupportedExtensions, StringComparer.OrdinalIgnoreCase);

    private readonly IMetadataReader _metadataReader;

    public LibraryScanner(IMetadataReader metadataReader)
    {
        ArgumentNullException.ThrowIfNull(metadataReader);

        _metadataReader = metadataReader;
    }

    /// <summary>
    /// 把「用户交进来的一批路径」展开成有序的音频文件列表。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 服务于「右键用本播放器打开」「双击文件」「拖进来一批东西」这三类入口：
    /// 它们拿到的都是一串路径，其中可能混着单个文件与整个文件夹，
    /// 而播放需要的是一份确定的顺序。
    /// </para>
    /// <para>
    /// <b>顺序规则</b>：用户给的顺序优先。显式给出的文件按给出的先后排；
    /// 目录则就地在它被给出的位置上展开（内部按名称升序，与曲库扫描一致），
    /// 因此「先拖 A.mp3 再拖一个文件夹」永远先播 A.mp3。
    /// </para>
    /// <para>
    /// 与曲库扫描的差别：这里不做「子目录被父目录覆盖就丢弃」的收敛，
    /// 而是按最终路径去重——用户同时给了某个文件夹和它里面的一个文件时，
    /// 那一首只应出现一次，且保留它先出现的位置。
    /// </para>
    /// </remarks>
    /// <param name="paths">文件或目录路径，顺序即播放顺序。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>去重后的音频文件绝对路径，顺序稳定。</returns>
    public IReadOnlyList<string> ExpandPaths(
        IEnumerable<string> paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (Directory.Exists(fullPath))
            {
                foreach (var file in EnumerateFiles(TrimSeparator(fullPath), cancellationToken))
                {
                    if (seen.Add(file.FilePath))
                    {
                        result.Add(file.FilePath);
                    }
                }

                continue;
            }

            // 单个文件不做扩展名筛选：用户明确点开的就照播，
            // 能不能解码交给引擎去判断并给出提示，拦在这里只会让人困惑。
            if (File.Exists(fullPath) && seen.Add(fullPath))
            {
                result.Add(fullPath);
            }
        }

        return result;
    }

    /// <summary>
    /// 第一阶段：遍历给定目录，列出所有受支持的音频文件。
    /// </summary>
    /// <param name="directories">要扫描的根目录。不存在的目录会被跳过而非报错。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>按路径去重后的文件列表，顺序稳定（同级按名称升序）。</returns>
    public IReadOnlyList<ScannedFile> ScanDirectories(
        IEnumerable<string> directories,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directories);

        var found = new List<ScannedFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in NormalizeRoots(directories))
        {
            foreach (var file in EnumerateFiles(root, cancellationToken))
            {
                // 去重兜底：同一个文件仍可能通过符号链接等路径二次进入。
                if (seen.Add(file.FilePath))
                {
                    found.Add(file);
                }
            }
        }

        return found;
    }

    /// <summary>
    /// 第二阶段：逐个解析元数据，边解析边产出。
    /// </summary>
    /// <param name="files">第一阶段的结果。</param>
    /// <param name="progress">进度回调，可为 null。回调在调用方线程上执行。</param>
    /// <param name="cancellationToken">取消标记。</param>
    public async IAsyncEnumerable<Track> ReadTracksAsync(
        IReadOnlyList<ScannedFile> files,
        IProgress<ScanProgress>? progress = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);

        progress?.Report(new ScanProgress(0, files.Count));

        for (var index = 0; index < files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var file = files[index];

            // 解析失败由 IMetadataReader 自行吞掉并返回 Empty，此处不需要额外容错分支；
            // 真要出现抛异常的实现，也只应中断这一个文件。
            TrackMetadata metadata;
            try
            {
                metadata = await _metadataReader
                    .ReadAsync(file.FilePath, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                metadata = TrackMetadata.Empty;
            }

            progress?.Report(new ScanProgress(index + 1, files.Count, file.FilePath));

            yield return ToTrack(file, metadata);
        }
    }

    /// <summary>
    /// 一次性完成两阶段扫描并返回完整结果。供无需增量展示的场景使用
    /// （例如后台重建曲库、测试），界面应改用 <see cref="ReadTracksAsync"/> 以便边扫边显示。
    /// </summary>
    public async Task<IReadOnlyList<Track>> ScanAsync(
        IEnumerable<string> directories,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var files = ScanDirectories(directories, cancellationToken);
        var tracks = new List<Track>(files.Count);

        await foreach (var track in ReadTracksAsync(files, progress, cancellationToken).ConfigureAwait(false))
        {
            tracks.Add(track);
        }

        return tracks;
    }

    /// <summary>
    /// 把「文件事实」与「解析结果」合成曲库实体。
    /// </summary>
    /// <remarks>
    /// 格式取扩展名判定值而非解析结果：<c>TrackMetadata</c> 不携带格式，
    /// 而扩展名判定在快速阶段已经算好。若日后发现改名文件导致格式错判影响播放，
    /// 再让 <see cref="IMetadataReader"/> 回传真实格式，此处是唯一的改动点。
    /// </remarks>
    public static Track ToTrack(ScannedFile file, TrackMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(metadata);

        return new Track
        {
            FilePath = file.FilePath,
            FileSize = file.FileSize,
            LastWriteTimeUtc = file.LastWriteTimeUtc,
            Title = metadata.Title,
            Artist = metadata.Artist,
            Album = metadata.Album,
            Duration = metadata.Duration,
            Format = file.Format,
            SampleRate = metadata.SampleRate,
            BitsPerSample = metadata.BitsPerSample,
            Bitrate = metadata.Bitrate,
            CoverCachePath = metadata.CoverCachePath,
        };
    }

    /// <summary>
    /// 判断已入库的曲目是否与新扫到的文件一致。
    /// </summary>
    /// <remarks>
    /// 比对口径按 8.2 的结论：只看文件大小与最后写入时间。
    /// 不用哈希——哈希要读完整文件，一万首曲子就是几十 GB 的 I/O，
    /// 而「大小或时间变了」已经足够触发重新解析。
    /// </remarks>
    public static bool IsUnchanged(Track track, ScannedFile file)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(file);

        return track.FileSize == file.FileSize
               && track.LastWriteTimeUtc == file.LastWriteTimeUtc;
    }

    /// <summary>
    /// 把目录字符串规范化为绝对路径：去空白、去尾部反斜杠、忽略大小写去重。
    /// </summary>
    /// <remarks>
    /// 不做存在性检查，因此调用方可以拿它来对照「用户声明了哪些目录」
    /// 与「实际能扫到哪些目录」——曲库同步需要这个差别来判断
    /// 「是文件真的被删了」还是「盘没接上」。
    /// </remarks>
    public static IReadOnlyList<string> NormalizePaths(IEnumerable<string> directories)
    {
        ArgumentNullException.ThrowIfNull(directories);

        var candidates = new List<string>();

        foreach (var directory in directories)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(directory);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            candidates.Add(TrimSeparator(fullPath));
        }

        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>判断文件是否位于给定根目录之下（含根目录本身）。</summary>
    public static bool IsUnderAnyRoot(string filePath, IReadOnlyList<string> roots)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(roots);

        foreach (var root in roots)
        {
            if (string.Equals(filePath, root, StringComparison.OrdinalIgnoreCase)
                || IsInside(filePath, root))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 规范化根目录：取绝对路径、去掉不存在的、去掉重复的，
    /// 并丢掉被别的根目录覆盖的子目录——否则子目录里的文件会被扫两遍。
    /// </summary>
    private static List<string> NormalizeRoots(IEnumerable<string> directories)
    {
        var candidates = NormalizePaths(directories)
            .Where(Directory.Exists)
            .ToList();

        var roots = new List<string>();

        // 先短后长：父目录一定比子目录短，于是父目录先入列，
        // 子目录在后面被 IsInside 判定出来而丢弃。
        candidates.Sort((left, right) => left.Length.CompareTo(right.Length));

        foreach (var candidate in candidates)
        {
            var covered = roots.Any(root =>
                string.Equals(root, candidate, StringComparison.OrdinalIgnoreCase)
                || IsInside(candidate, root));

            if (!covered)
            {
                roots.Add(candidate);
            }
        }

        return roots;
    }

    /// <summary>判断 <paramref name="candidate"/> 是否位于 <paramref name="parent"/> 之下。</summary>
    private static bool IsInside(string candidate, string parent) =>
        candidate.StartsWith(
            parent + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static string TrimSeparator(string path) =>
        path.Length > 1 && (path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar))
            ? path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : path;

    /// <summary>
    /// 深度优先遍历目录树，逐层容忍读不了的子目录。
    /// </summary>
    /// <remarks>
    /// 没有用 <c>Directory.EnumerateFiles(..., AllDirectories)</c>，原因是它一遇到
    /// 无权限目录或符号链接成环就会整条枚举抛异常，而曲库扫描里这两种情况都常见；
    /// 手写遍历可以对单个目录降级，也能显式跳过重解析点。
    /// </remarks>
    private static IEnumerable<ScannedFile> EnumerateFiles(string root, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var directory = pending.Pop();

            string[] files;
            string[] subdirectories;
            try
            {
                files = Directory.GetFiles(directory);
                subdirectories = Directory.GetDirectories(directory);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException
                                          or IOException
                                          or DirectoryNotFoundException
                                          or PathTooLongException)
            {
                // 单个目录读不到不该中断整批扫描，跳过继续。
                continue;
            }

            // 排序是为了让结果稳定：同一棵目录树每次扫出的顺序一致，
            // 列表不会无故跳动，测试也能断言顺序。
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            Array.Sort(subdirectories, StringComparer.OrdinalIgnoreCase);

            foreach (var path in files)
            {
                if (!SupportedExtensions.Contains(Path.GetExtension(path)))
                {
                    continue;
                }

                FileInfo info;
                try
                {
                    info = new FileInfo(path);
                    if (!info.Exists)
                    {
                        continue;
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PathTooLongException)
                {
                    continue;
                }

                yield return new ScannedFile(
                    info.FullName,
                    info.Length,
                    info.LastWriteTimeUtc,
                    AudioFormatDetector.FromPath(path));
            }

            // 逆序压栈，配合上面的升序排序，使实际访问顺序与排序一致。
            for (var index = subdirectories.Length - 1; index >= 0; index--)
            {
                if (IsReparsePoint(subdirectories[index]))
                {
                    // 符号链接与联接点可能指回上层目录形成环，也可能指向已被扫描过的位置。
                    // 曲库不需要「同一份文件通过两条路径出现两次」，直接跳过。
                    continue;
                }

                pending.Push(subdirectories[index]);
            }
        }
    }

    private static bool IsReparsePoint(string directory)
    {
        try
        {
            return File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or FileNotFoundException)
        {
            // 判断不了时按「不是」处理，让后续读取去失败并降级，
            // 总好过凭猜测丢掉一个正常目录。
            return false;
        }
    }
}
