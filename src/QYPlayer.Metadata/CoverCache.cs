using System.Security.Cryptography;

namespace QYPlayer.Metadata;

/// <summary>
/// 基于内容哈希的封面缓存。相同封面只落盘一次，文件名即内容哈希。
/// 缓存目录按总容量上限淘汰，优先淘汰最久未使用的那些。
/// </summary>
/// <remarks>
/// <para>
/// 淘汰口径按需求文档 8.2 的结论取「按缓存总容量上限」，而不是「清理未被曲目引用的
/// 孤儿文件」：后者要遍历整张曲目表、必须等曲库加载完成才能执行，且曲库与缓存的加载
/// 时序一旦错位就会误删仍在用的封面。容量上限与曲库规模、加载时序都无关。
/// </para>
/// <para>
/// 「最久未使用」用文件自身的最后写入时间表达，命中缓存时把时间戳推到当前。
/// 不额外维护索引文件：缓存是可重建数据，为它多一份可能与实际文件对不上的状态不划算。
/// </para>
/// </remarks>
public sealed class CoverCache : ICoverCache
{
    /// <summary>
    /// 默认的缓存总容量上限（2 GB）。
    /// </summary>
    /// <remarks>
    /// 取值依据是曲库规模的上界而不是典型值：2.3 的性能目标按 1 万个音频文件定，
    /// 极端情况下每个文件都带一张各不相同的封面，按每张 200 KB 估算约 2 GB。
    /// 设得比上界小会让仍在被曲目引用的封面被反复淘汰、又在下次扫描时重新解析出来，
    /// 形成无谓的抖动。实际曲库里封面在专辑内大量重复，稳态占用通常只有几百 MB。
    /// </remarks>
    public const long DefaultMaxTotalBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// 淘汰后把占用收敛到上限的这个比例。
    /// </summary>
    /// <remarks>
    /// 留出一截余量是为了让淘汰按批发生而不是每存一张触发一次：
    /// 一次性扫一万首曲子，若每次都精确收敛到上限，等于每写一张就要枚举并排序整个目录。
    /// </remarks>
    private const double TrimTargetRatio = 0.8;

    /// <summary>
    /// 临时文件被认为是废弃的年龄。
    /// </summary>
    /// <remarks>
    /// 正常写入的临时文件存活时间是毫秒级，只有进程异常退出才会留下，
    /// 因此阈值取一个远大于正常写入耗时的值，避免误删正在写的那个。
    /// </remarks>
    private static readonly TimeSpan StaleTempFileAge = TimeSpan.FromHours(1);

    /// <summary>缓存文件可能出现的扩展名，与 <see cref="ResolveExtension"/> 的返回值保持一致。</summary>
    private static readonly string[] CacheExtensions = [".jpg", ".png", ".bmp", ".gif", ".webp"];

    /// <summary>
    /// 串行化「测量占用 + 淘汰 + 写入」。
    /// </summary>
    /// <remarks>
    /// 这几步必须是一个整体：并发的写入会让两边的占用估算都失真，
    /// 结果就是上限被突破或者刚写完的文件立刻被别人淘汰掉。
    /// 用一整把锁而不是细粒度计数，是因为单次写入只是写一张几百 KB 的图、
    /// 扫描器本身也是逐个文件顺序解析的，实际几乎没有竞争，
    /// 换来的是记账精确、不必在几处之间做无锁协调。
    /// </remarks>
    private readonly Lock _gate = new();

    private readonly long _maxTotalBytes;
    private readonly string _cacheDirectory;

    /// <summary>缓存占用的估算值，避免每次存盘都去枚举整个目录。</summary>
    private long _estimatedTotalBytes;

    /// <summary><see cref="_estimatedTotalBytes"/> 是否已经过一次实际测量。</summary>
    private bool _totalBytesMeasured;

    /// <param name="cacheDirectory">缓存目录。不存在时会按需创建。</param>
    /// <param name="maxTotalBytes">
    /// 缓存总容量上限，单位字节。传 0 或负数表示不淘汰。
    /// </param>
    public CoverCache(string cacheDirectory, long maxTotalBytes = DefaultMaxTotalBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);

        _cacheDirectory = cacheDirectory;
        _maxTotalBytes = maxTotalBytes;
    }

    public string CacheDirectory => _cacheDirectory;

    /// <summary>缓存总容量上限，单位字节。0 或负数表示不淘汰。</summary>
    public long MaxTotalBytes => _maxTotalBytes;

    public string? Save(byte[]? data, string? mimeType)
    {
        if (data is null || data.Length == 0)
        {
            return null;
        }

        var extension = ResolveExtension(mimeType, data);
        var hash = Convert.ToHexString(SHA256.HashData(data))[..32];
        var path = Path.Combine(_cacheDirectory, hash + extension);

        lock (_gate)
        {
            // 已经缓存过同内容的封面，直接复用。
            if (File.Exists(path))
            {
                // 命中也算一次使用：把时间戳推到当前，淘汰时才不会把「还在用的」排到前面。
                // 文件名是内容哈希，刷新时间戳不影响任何按名字查找的逻辑。
                Touch(path);
                return path;
            }

            // 先腾空间再落盘。顺序不能反：淘汰是「删最久未使用的」，
            // 若先写新文件再淘汰，刚写的那张会立刻被算进占用，极端配置下
            // （上限比单张封面还小）它会连着自己一起删掉，调用方拿到的路径随即失效。
            MakeRoom(data.Length);

            Directory.CreateDirectory(_cacheDirectory);

            // 先写临时文件再改名，避免并发或异常中断留下半截文件。
            // 临时名带随机段而非固定的 ".tmp" 后缀：同一个封面的哈希相同、
            // 目标路径相同，两个线程会写同一个临时名。
            var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllBytes(tempPath, data);
                File.Move(tempPath, path, overwrite: true);
            }
            catch (IOException)
            {
                // 另一个调用方已经写了同名文件，视为成功。
                if (!File.Exists(path))
                {
                    throw;
                }
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    TryDelete(tempPath);
                }
            }

            // 记账按实际落盘的大小算，而不是本次传入的长度：
            // 上面那个 IOException 分支意味着文件可能是别人写的，长度未必相同。
            _estimatedTotalBytes += new FileInfo(path).Length;

            return path;
        }
    }

    /// <summary>
    /// 把缓存目录收敛到容量上限以内，超出时淘汰最久未使用的封面。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 可以随时调用：占用未超上限时只做一次枚举并记下实测值，目录不存在时什么都不做。
    /// 正常使用下不需要显式调用——<see cref="Save"/> 在写入前会自己腾空间。
    /// 显式调用的场合是「想在有写入之前就把上一次运行留下的超额占用收掉」，
    /// 例如启动时后台跑一次。
    /// </para>
    /// </remarks>
    public void Trim()
    {
        lock (_gate)
        {
            if (!TryMeasure(out var files, out var total))
            {
                return;
            }

            if (_maxTotalBytes <= 0 || total <= _maxTotalBytes)
            {
                return;
            }

            Evict(files, total, (long)(_maxTotalBytes * TrimTargetRatio));
        }
    }

    /// <summary>
    /// 为即将写入的封面腾出空间。调用方必须已持有 <see cref="_gate"/>。
    /// </summary>
    private void MakeRoom(int incomingBytes)
    {
        if (_maxTotalBytes <= 0)
        {
            return;
        }

        // 估算值可信且这次写入装得下时直接返回，不枚举目录。
        // 没有这个快速路径，扫一万首曲子就要做一万次目录枚举。
        if (_totalBytesMeasured && _estimatedTotalBytes + incomingBytes <= _maxTotalBytes)
        {
            return;
        }

        if (!TryMeasure(out var files, out var total))
        {
            return;
        }

        // 目标是「上限减掉这次要写的字节数」，而不是上限的固定比例：
        // 单张封面占上限的比例可能远大于余量，按比例收敛会写超。
        Evict(files, total, Math.Max(0, _maxTotalBytes - incomingBytes));
    }

    /// <summary>
    /// 测量缓存目录的实际占用，并更新估算值。
    /// </summary>
    /// <returns>测量成功返回 true；目录临时读不到返回 false，本次收敛作罢。</returns>
    private bool TryMeasure(out List<CacheFile> files, out long total)
    {
        files = [];
        total = 0;

        try
        {
            DeleteStaleTempFiles();

            files = EnumerateCacheFiles();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 目录被同步或杀毒软件锁住、盘临时不可用等：等下次。
            return false;
        }

        total = files.Sum(static file => file.Length);
        _estimatedTotalBytes = total;
        _totalBytesMeasured = true;

        return true;
    }

    /// <summary>
    /// 按「最久未使用优先」删除文件，直到占用降到 <paramref name="targetBytes"/> 以内。
    /// 调用方必须已持有 <see cref="_gate"/>。
    /// </summary>
    private void Evict(List<CacheFile> files, long total, long targetBytes)
    {
        // 时间戳相同时按路径定序，让淘汰结果可预期（也才能被测试断言）。
        var candidates = files
            .OrderBy(static file => file.LastWriteTimeUtc)
            .ThenBy(static file => file.Path, StringComparer.Ordinal);

        foreach (var file in candidates)
        {
            if (total <= targetBytes)
            {
                break;
            }

            // 删不掉的（正被界面加载、被别的进程占用）跳过继续，不因此中断整轮淘汰。
            if (TryDelete(file.Path))
            {
                total -= file.Length;
                _estimatedTotalBytes = total;
            }
        }
    }

    /// <summary>列出目录里符合缓存命名形状的文件。</summary>
    private List<CacheFile> EnumerateCacheFiles()
    {
        var files = new List<CacheFile>();

        if (!Directory.Exists(_cacheDirectory))
        {
            return files;
        }

        foreach (var path in Directory.EnumerateFiles(_cacheDirectory))
        {
            // 顺手跳过临时文件与用户自己放进来的东西。
            if (!IsCacheFile(Path.GetFileName(path)))
            {
                continue;
            }

            var info = new FileInfo(path);
            files.Add(new CacheFile(path, info.Length, info.LastWriteTimeUtc));
        }

        return files;
    }

    /// <summary>
    /// 判断文件名是否符合缓存命名形状，即「32 位十六进制哈希 + 已知图片扩展名」。
    /// </summary>
    /// <remarks>
    /// 按形状判断而不是按「不是 .tmp 就当封面」，是为了绝不误删用户手动放进缓存目录的图片。
    /// </remarks>
    private static bool IsCacheFile(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return false;
        }

        var extension = Path.GetExtension(fileName);
        if (!CacheExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        return stem.Length == 32 && stem.All(Uri.IsHexDigit);
    }

    /// <summary>
    /// 清理上次异常退出留下的临时文件。正在被写入的那些删不掉，跳过即可。
    /// </summary>
    private void DeleteStaleTempFiles()
    {
        if (!Directory.Exists(_cacheDirectory))
        {
            return;
        }

        var threshold = DateTime.UtcNow - StaleTempFileAge;

        foreach (var path in Directory.EnumerateFiles(_cacheDirectory))
        {
            // 按扩展名精确比对，而不是用 "*.tmp" 通配去匹配。
            if (!string.Equals(Path.GetExtension(path), ".tmp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                if (File.GetLastWriteTimeUtc(path) < threshold)
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 写入中的临时文件，或已被并发清理：两种情况都不需要处理。
            }
        }
    }

    /// <summary>
    /// 把文件的最后写入时间推到当前，用于表达「这张封面最近被用到」。
    /// </summary>
    private static void Touch(string path)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 文件可能正被界面读取（WPF 的图片加载会持有句柄），也可能刚被并发淘汰。
            // 时间戳只用于淘汰排序，刷新失败不影响正确性。
        }
    }

    /// <summary>
    /// MIME 缺失或不可信时，回退到按文件头魔数判断。
    /// 这一步是必要的：部分软件的标签把 image/png 写成 image/jpg。
    /// </summary>
    private static string ResolveExtension(string? mimeType, byte[] data)
    {
        if (LooksLikePng(data))
        {
            return ".png";
        }

        if (LooksLikeJpeg(data))
        {
            return ".jpg";
        }

        // 无法从内容判断时才信任 MIME。
        var normalized = mimeType?.ToLowerInvariant();
        return normalized switch
        {
            "image/png" => ".png",
            "image/bmp" => ".bmp",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            _ => ".jpg",
        };
    }

    private static bool LooksLikePng(byte[] data) =>
        data.Length >= 8
        && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47;

    private static bool LooksLikeJpeg(byte[] data) =>
        data.Length >= 3
        && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;

    /// <summary>删除文件并报告是否真的删掉了。</summary>
    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响主流程。
            return false;
        }
    }

    /// <summary>目录里的一个缓存文件，连同淘汰排序需要的信息。</summary>
    private readonly record struct CacheFile(string Path, long Length, DateTime LastWriteTimeUtc);
}
