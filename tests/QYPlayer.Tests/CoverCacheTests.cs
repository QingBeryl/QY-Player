using System.Security.Cryptography;
using QYPlayer.Metadata;

namespace QYPlayer.Tests;

/// <summary>
/// 封面缓存的容量淘汰测试，只依赖文件系统。
/// </summary>
/// <remarks>
/// 淘汰逻辑不碰数据库、不碰界面，因此可以完全在临时目录上验证：
/// 直接摆出若干个「形状合法」的缓存文件并用明确的时间戳定死先后，
/// 再断言淘汰的是哪几个、收敛到什么体积。这样用例不受执行速度影响。
/// </remarks>
public sealed class CoverCacheTests : IDisposable
{
    /// <summary>PNG 文件头（89 50 4E 47），后接填充字节凑够长度。</summary>
    private static readonly byte[] PngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>JPEG 文件头（FF D8 FF）。</summary>
    private static readonly byte[] JpegHeader = [0xFF, 0xD8, 0xFF, 0xE0];

    private readonly string _root;
    private readonly string _cacheDirectory;

    public CoverCacheTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qyplayer-cover-" + Guid.NewGuid().ToString("N"));
        _cacheDirectory = Path.Combine(_root, "covers");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不影响用例结论。
        }
    }

    // ── 基本写入行为 ──────────────────────────────────────────────

    [Fact]
    public void Save_空数据返回_null()
    {
        var cache = new CoverCache(_cacheDirectory);

        Assert.Null(cache.Save(null, "image/jpeg"));
        Assert.Null(cache.Save([], "image/jpeg"));

        // 空数据不该顺手把目录建出来。
        Assert.False(Directory.Exists(_cacheDirectory));
    }

    [Fact]
    public void Save_相同内容复用同一个文件()
    {
        var cache = new CoverCache(_cacheDirectory);
        var data = Cover(PngHeader, 64);

        var first = cache.Save(data, "image/png");
        var second = cache.Save(data, "image/png");

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.Single(Directory.GetFiles(_cacheDirectory));

        // 返回值必须是能直接交给界面加载的绝对路径。
        Assert.True(Path.IsPathFullyQualified(first));
        Assert.True(File.Exists(first));
    }

    [Fact]
    public void Save_不同内容落成不同文件()
    {
        var cache = new CoverCache(_cacheDirectory);

        var first = cache.Save(Cover(PngHeader, 64), "image/png");
        var second = cache.Save(Cover(PngHeader, 80), "image/png");

        Assert.NotEqual(first, second);
        Assert.Equal(2, Directory.GetFiles(_cacheDirectory).Length);
    }

    [Fact]
    public void Save_目录不存在时会自动创建()
    {
        Assert.False(Directory.Exists(_cacheDirectory));

        var cache = new CoverCache(_cacheDirectory);
        var path = cache.Save(Cover(PngHeader, 64), "image/png");

        Assert.NotNull(path);
        Assert.True(Directory.Exists(_cacheDirectory));
    }

    [Fact]
    public void Save_不留下临时文件()
    {
        var cache = new CoverCache(_cacheDirectory);

        var path = cache.Save(Cover(PngHeader, 64), "image/png");

        Assert.NotNull(path);
        Assert.Empty(Directory.GetFiles(_cacheDirectory, "*.tmp"));
    }

    [Theory]
    [InlineData("image/png", ".png")]
    [InlineData("image/jpeg", ".jpg")]
    [InlineData("image/bmp", ".bmp")]
    [InlineData("image/gif", ".gif")]
    [InlineData("image/webp", ".webp")]
    [InlineData(null, ".jpg")]
    [InlineData("application/octet-stream", ".jpg")]
    public void Save_无法判断文件头时按_MIME_定扩展名(string? mimeType, string expected)
    {
        var cache = new CoverCache(_cacheDirectory);

        // 既不是 PNG 也不是 JPEG 的字节，只能靠 MIME。
        var path = cache.Save(Cover([0x00, 0x01, 0x02, 0x03], 64), mimeType);

        Assert.NotNull(path);
        Assert.Equal(expected, Path.GetExtension(path));
    }

    [Fact]
    public void Save_文件头优先于_MIME()
    {
        var cache = new CoverCache(_cacheDirectory);

        // 部分软件的标签把 PNG 写成 image/jpg，此时必须信文件头。
        var pngPath = cache.Save(Cover(PngHeader, 64), "image/jpg");
        var jpegPath = cache.Save(Cover(JpegHeader, 64), "image/png");

        Assert.Equal(".png", Path.GetExtension(pngPath));
        Assert.Equal(".jpg", Path.GetExtension(jpegPath));
    }

    [Fact]
    public void Save_命中缓存会刷新时间戳()
    {
        var cache = new CoverCache(_cacheDirectory);
        var data = Cover(PngHeader, 64);

        var path = cache.Save(data, "image/png");
        Assert.NotNull(path);

        // 把时间戳退到很久以前，模拟「这张封面很久没被用到」。
        var stale = DateTime.UtcNow.AddDays(-30);
        File.SetLastWriteTimeUtc(path, stale);

        cache.Save(data, "image/png");

        // 命中即算一次使用，否则还在用的封面会在淘汰时被排到最前面。
        Assert.True(File.GetLastWriteTimeUtc(path) > stale);
    }

    // ── 淘汰 ──────────────────────────────────────────────────────

    [Fact]
    public void Trim_未超上限时不动任何文件()
    {
        var cache = new CoverCache(_cacheDirectory, maxTotalBytes: 10_000);
        PutCacheFile("a", size: 1_000, age: TimeSpan.FromDays(10));
        PutCacheFile("b", size: 1_000, age: TimeSpan.FromDays(5));
        PutCacheFile("c", size: 1_000, age: TimeSpan.Zero);

        cache.Trim();

        Assert.Equal(3, Directory.GetFiles(_cacheDirectory).Length);
    }

    [Fact]
    public void Trim_超过上限时先淘汰最久未使用的()
    {
        var cache = new CoverCache(_cacheDirectory, maxTotalBytes: 250);

        // 五张各 100 字节，合计 500；上限 250 意味着必须淘汰掉三张。
        var oldest = PutCacheFile("oldest", size: 100, age: TimeSpan.FromDays(5));
        var second = PutCacheFile("second", size: 100, age: TimeSpan.FromDays(4));
        var third = PutCacheFile("third", size: 100, age: TimeSpan.FromDays(3));
        var newest = PutCacheFile("newest", size: 100, age: TimeSpan.FromDays(1));
        var newer = PutCacheFile("newer", size: 100, age: TimeSpan.FromDays(2));

        cache.Trim();

        // 剩下的是时间戳最新的两张。
        Assert.False(File.Exists(oldest));
        Assert.False(File.Exists(second));
        Assert.False(File.Exists(third));
        Assert.True(File.Exists(newest));
        Assert.True(File.Exists(newer));
    }

    [Fact]
    public void Trim_把占用收敛到上限以内()
    {
        const long limit = 1_000;
        var cache = new CoverCache(_cacheDirectory, maxTotalBytes: limit);
        PutCacheFile("a", size: 900, age: TimeSpan.FromDays(5));
        PutCacheFile("b", size: 900, age: TimeSpan.FromDays(4));

        cache.Trim();

        Assert.True(TotalBytes() <= limit, $"淘汰后仍占用 {TotalBytes()} 字节，上限是 {limit}。");
    }

    [Fact]
    public void Trim_上限为_0_表示不淘汰()
    {
        var cache = new CoverCache(_cacheDirectory, maxTotalBytes: 0);
        PutCacheFile("a", size: 100_000, age: TimeSpan.FromDays(5));
        PutCacheFile("b", size: 100_000, age: TimeSpan.FromDays(4));

        cache.Trim();

        Assert.Equal(2, Directory.GetFiles(_cacheDirectory).Length);
    }

    [Fact]
    public void Trim_取一次即可收敛_可重复调用()
    {
        var cache = new CoverCache(_cacheDirectory, maxTotalBytes: 500);
        for (var index = 0; index < 10; index++)
        {
            PutCacheFile($"file-{index}", size: 200, age: TimeSpan.FromDays(10 - index));
        }

        cache.Trim();
        var afterFirst = Directory.GetFiles(_cacheDirectory).Length;

        cache.Trim();
        var afterSecond = Directory.GetFiles(_cacheDirectory).Length;

        Assert.Equal(afterFirst, afterSecond);
        Assert.True(TotalBytes() <= 500);
    }

    [Fact]
    public void Trim_目录不存在时不报错()
    {
        var cache = new CoverCache(_cacheDirectory, maxTotalBytes: 100);

        // 还没写过任何封面就触发淘汰，是启动时的正常情形。
        cache.Trim();

        Assert.False(Directory.Exists(_cacheDirectory));
    }

    [Fact]
    public void Trim_不碰目录里的其他文件()
    {
        var cache = new CoverCache(_cacheDirectory, maxTotalBytes: 100);

        var cover = PutCacheFile("cover", size: 200, age: TimeSpan.FromDays(3));

        // 用户在缓存目录里手放的东西，以及其它工具的产物。
        var note = WriteFile("我的笔记.txt", size: 5_000);
        var userPicture = WriteFile("封面备份.jpg", size: 5_000);

        cache.Trim();

        Assert.False(File.Exists(cover));
        Assert.True(File.Exists(note));
        Assert.True(File.Exists(userPicture));
    }

    [Fact]
    public void Trim_名字不合形状的文件不计入占用()
    {
        var cache = new CoverCache(_cacheDirectory, maxTotalBytes: 1_000);

        // 32 位十六进制哈希 + 图片扩展名才算缓存文件，这两个都不符合。
        WriteFile("short.jpg", size: 5_000);
        WriteFile("ffffffffffffffffffffffffffffffff.abcd", size: 5_000);

        cache.Trim();

        Assert.Equal(2, Directory.GetFiles(_cacheDirectory).Length);
    }

    [Fact]
    public void Trim_清理上次异常退出留下的临时文件()
    {
        var cache = new CoverCache(_cacheDirectory, maxTotalBytes: 10_000);

        var stale = PutTempFile("stale", age: TimeSpan.FromHours(3));
        var fresh = PutTempFile("fresh", age: TimeSpan.Zero);

        cache.Trim();

        // 只删陈旧的：新鲜的可能是另一个线程正在写的那个。
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void Trim_删不掉的缓存文件不会中断整轮淘汰()
    {
        var cache = new CoverCache(_cacheDirectory, maxTotalBytes: 150);

        // 用独占句柄模拟「这张封面正被界面加载，暂时删不掉」。
        var locked = PutCacheFile("locked", size: 100, age: TimeSpan.FromDays(5));
        var deletableA = PutCacheFile("a", size: 100, age: TimeSpan.FromDays(4));
        var deletableB = PutCacheFile("b", size: 100, age: TimeSpan.FromDays(3));

        using (File.Open(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            cache.Trim();
        }

        // 锁定那个幸存，其余照常淘汰；不因一个删不掉就整体放弃。
        Assert.True(File.Exists(locked));
        Assert.False(File.Exists(deletableA));
        Assert.False(File.Exists(deletableB));
    }

    // ── 写入与淘汰的联动 ──────────────────────────────────────────

    [Fact]
    public void Save_在写入过程中把占用收敛到上限以内()
    {
        const long limit = 3_000;
        var cache = new CoverCache(_cacheDirectory, maxTotalBytes: limit);

        // 连写 20 张各 1 KB 的封面，上限 3 KB，必然中途触发淘汰。
        var paths = new List<string>();
        for (var index = 0; index < 20; index++)
        {
            var path = cache.Save(Cover(PngHeader, 1_000, index), "image/png");
            Assert.NotNull(path);
            paths.Add(path);

            Assert.True(
                TotalBytes() <= limit,
                $"存第 {index + 1} 张之后占用 {TotalBytes()} 字节，超过上限 {limit}。");
        }

        Assert.True(Directory.GetFiles(_cacheDirectory).Length < 20);
    }

    [Fact]
    public void Save_返回的路径在淘汰之后依然存在()
    {
        // 上限刻意设得比单张封面还小：此时无论淘汰多少，刚写的那张都必须留住，
        // 否则调用方拿到的路径指向一个已被删掉的文件，界面上就是「封面时有时无」。
        var cache = new CoverCache(_cacheDirectory, maxTotalBytes: 10);

        string? path = null;
        for (var index = 0; index < 5; index++)
        {
            path = cache.Save(Cover(PngHeader, 500, index), "image/png");
            Assert.NotNull(path);
            Assert.True(File.Exists(path), $"第 {index + 1} 张写入后返回的路径不存在。");
        }

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Save_并发写同一内容只落一个文件()
    {
        var cache = new CoverCache(_cacheDirectory);
        var data = Cover(PngHeader, 4_096);

        var paths = new string?[16];

        Parallel.For(0, paths.Length, index => paths[index] = cache.Save(data, "image/png"));

        Assert.All(paths, path => Assert.Equal(paths[0], path));
        Assert.Single(Directory.GetFiles(_cacheDirectory));
    }

    [Fact]
    public void Save_并发写不同内容不会因淘汰互相破坏()
    {
        var cache = new CoverCache(_cacheDirectory, maxTotalBytes: 20_000);

        var paths = new string?[24];
        Parallel.For(0, paths.Length, index => paths[index] = cache.Save(Cover(PngHeader, 900, index), "image/png"));

        // 结果路径各自唯一且都指向真实文件（删不掉的极端竞争下允许个别已被淘汰）。
        Assert.All(paths, Assert.NotNull);
        Assert.Equal(paths.Length, paths.Distinct().Count());
        Assert.True(TotalBytes() <= 20_000);
    }

    // ── 辅助 ──────────────────────────────────────────────────────

    /// <summary>构造一段以给定文件头开头、指定长度的封面数据。</summary>
    private static byte[] Cover(byte[] header, int length, int seed = 0)
    {
        var data = new byte[length];
        header.CopyTo(data, 0);

        // 填充成可区分的内容，避免不同用例之间哈希撞车。
        for (var index = header.Length; index < length; index++)
        {
            data[index] = (byte)((index * 31 + seed) % 251);
        }

        return data;
    }

    /// <summary>
    /// 在缓存目录里放一个形状合法的缓存文件（32 位十六进制哈希 + .jpg），
    /// 并把时间戳退到指定年龄之前，用于定死淘汰顺序。
    /// </summary>
    private string PutCacheFile(string label, int size, TimeSpan age)
    {
        var name = HashName(label) + ".jpg";
        var path = WriteFile(name, size);

        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);

        return path;
    }

    /// <summary>在缓存目录里放一个临时文件，文件名形状与 <c>CoverCache</c> 写出的保持一致。</summary>
    private string PutTempFile(string label, TimeSpan age)
    {
        var path = WriteFile(HashName(label) + ".jpg.tmp", size: 32);

        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);

        return path;
    }

    private string WriteFile(string name, int size)
    {
        Directory.CreateDirectory(_cacheDirectory);

        var path = Path.Combine(_cacheDirectory, name);
        File.WriteAllBytes(path, new byte[size]);

        return path;
    }

    /// <summary>由标签稳定地推出一个 32 位十六进制文件名。</summary>
    private static string HashName(string label)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(label));

        return Convert.ToHexString(bytes)[..32];
    }

    private long TotalBytes() =>
        Directory.Exists(_cacheDirectory)
            ? Directory.GetFiles(_cacheDirectory).Sum(path => new FileInfo(path).Length)
            : 0;
}
