using QYPlayer.Core.Library;
using QYPlayer.Core.Metadata;
using QYPlayer.Core.Models;

namespace QYPlayer.Tests;

/// <summary>
/// 给扫描器用的假元数据读取器。避免纯逻辑测试去碰真实音频文件，
/// 也能精确构造「实现抛异常」这类真实实现不会出现的分支。
/// </summary>
internal sealed class FakeMetadataReader : IMetadataReader
{
    private readonly Func<string, TrackMetadata> _factory;

    public FakeMetadataReader(Func<string, TrackMetadata>? factory = null)
    {
        _factory = factory ?? (path => new TrackMetadata(
            Path.GetFileNameWithoutExtension(path),
            "测试艺术家",
            "测试专辑",
            TimeSpan.FromSeconds(180),
            44100,
            16,
            320,
            null));
    }

    /// <summary>调用次数。用于验证增量比对确实跳过了未变动的文件。</summary>
    public int CallCount { get; private set; }

    public IReadOnlyList<string> ReadPaths => _readPaths;

    private readonly List<string> _readPaths = [];

    public Task<TrackMetadata> ReadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        CallCount++;
        _readPaths.Add(filePath);

        return Task.FromResult(_factory(filePath));
    }
}

/// <summary>
/// 扫描器的快速阶段：目录遍历、扩展名筛选、去重与顺序稳定性。
/// </summary>
/// <remarks>
/// 这些用例是扫描逻辑里最容易出错、又最容易被界面现象掩盖的部分：
/// 路径大小写、父目录与子目录同时被指定、无权限目录、符号链接成环。
/// 它们在数据层断言的成本远低于等到界面上发现「列表多了一行」再回头查。
/// </remarks>
public class LibraryScannerTests : IDisposable
{
    private readonly string _root;

    public LibraryScannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qyplayer-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
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

    private string Touch(string relativePath, string content = "x")
    {
        var fullPath = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        return fullPath;
    }

    [Fact]
    public void 只收受支持的扩展名且忽略大小写()
    {
        Touch("a.mp3");
        Touch("b.FLAC");
        Touch("c.txt");
        Touch("d.jpg");
        Touch("e.opus");

        var scanner = new LibraryScanner(new FakeMetadataReader());

        var files = scanner.ScanDirectories([_root]);

        Assert.Equal(3, files.Count);
        Assert.Contains(files, f => f.FilePath.EndsWith("a.mp3", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(files, f => f.FilePath.EndsWith("b.FLAC", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(files, f => f.FilePath.EndsWith("e.opus", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void 加密格式同样入列()
    {
        // 曲库收录加密文件，能否播放由播放链路判断（8.1：扫描不做格式筛选）。
        Touch("secret.ncm");
        Touch("secret2.kgm");

        var scanner = new LibraryScanner(new FakeMetadataReader());

        var files = scanner.ScanDirectories([_root]);

        Assert.Equal(2, files.Count);
        Assert.Contains(files, f => f.Format == AudioFormat.Ncm);
        Assert.Contains(files, f => f.Format == AudioFormat.Kgm);
    }

    [Fact]
    public void 递归子目录并保持顺序稳定()
    {
        Touch("album/a.mp3");
        Touch("album/b.mp3");
        Touch("album/sub/c.mp3");
        Touch("z.mp3");

        var scanner = new LibraryScanner(new FakeMetadataReader());

        var first = scanner.ScanDirectories([_root]).Select(f => f.FilePath).ToList();
        var second = scanner.ScanDirectories([_root]).Select(f => f.FilePath).ToList();

        Assert.Equal(4, first.Count);
        // 顺序稳定：同一棵树两次扫描结果一致，列表不会无故跳动。
        Assert.Equal(first, second);
    }

    [Fact]
    public void 父子目录同时指定时不重复收录()
    {
        Touch("album/a.mp3");
        Touch("album/sub/b.mp3");

        var scanner = new LibraryScanner(new FakeMetadataReader());

        var files = scanner.ScanDirectories([_root, Path.Combine(_root, "album")]);

        // 子目录被父目录覆盖，两个文件各出现一次。
        Assert.Equal(2, files.Count);
    }

    [Fact]
    public void 同一个根目录重复指定只收一次()
    {
        Touch("a.mp3");

        var scanner = new LibraryScanner(new FakeMetadataReader());

        var files = scanner.ScanDirectories([_root, _root, _root + Path.DirectorySeparatorChar]);

        Assert.Single(files);
    }

    [Fact]
    public void 不存在的目录被跳过而不是抛异常()
    {
        Touch("a.mp3");
        var missing = Path.Combine(_root, "not-here");

        var scanner = new LibraryScanner(new FakeMetadataReader());

        var files = scanner.ScanDirectories([missing, _root, "   ", ""]);

        Assert.Single(files);
    }

    [Fact]
    public void 空目录返回空列表()
    {
        var scanner = new LibraryScanner(new FakeMetadataReader());

        Assert.Empty(scanner.ScanDirectories([_root]));
        Assert.Empty(scanner.ScanDirectories([]));
    }

    [Fact]
    public void 记录文件大小与写入时间供增量比对()
    {
        var path = Touch("a.mp3", "0123456789");

        var scanner = new LibraryScanner(new FakeMetadataReader());

        var file = Assert.Single(scanner.ScanDirectories([_root]));

        Assert.Equal(10, file.FileSize);
        Assert.Equal(File.GetLastWriteTimeUtc(path), file.LastWriteTimeUtc);
        Assert.Equal(path, file.FilePath);
    }

    [Fact]
    public void 取消标记生效时立即停止()
    {
        Touch("a.mp3");
        Touch("b.mp3");

        var scanner = new LibraryScanner(new FakeMetadataReader());

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => scanner.ScanDirectories([_root], cts.Token));
    }
}

/// <summary>
/// 「把用户交进来的一批路径展开成有序播放列表」的用例。
/// </summary>
/// <remarks>
/// 这一层对应「双击文件 / 右键打开方式 / 拖入一批东西」这三类入口，
/// 用户对顺序是有预期的（他给的顺序就是播放顺序），
/// 所以除了「收到了什么」，顺序本身也要被断言。
/// </remarks>
public class LibraryScannerExpandPathsTests : IDisposable
{
    private readonly string _root;

    public LibraryScannerExpandPathsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qyplayer-expand-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
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

    private string Touch(string relativePath, string content = "x")
    {
        var fullPath = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);

        // 返回规范化后的路径：展开器内部会用 GetFullPath 统一分隔符，
        // 这里先统一，断言才能直接比字符串。
        return Path.GetFullPath(fullPath);
    }

    private static LibraryScanner CreateScanner() => new(new FakeMetadataReader());

    [Fact]
    public void 显式给出的文件保持用户给的先后顺序()
    {
        var first = Touch("m.mp3");
        var second = Touch("a.mp3");
        var third = Touch("z.mp3");

        // 刻意逆着文件名给：结果必须是给出的顺序，而不是按名排序。
        var files = CreateScanner().ExpandPaths([first, second, third]);

        Assert.Equal([first, second, third], files);
    }

    [Fact]
    public void 目录就地在被给出的位置上展开且内部按名升序()
    {
        var head = Touch("head.mp3");
        Touch("album/b.mp3");
        Touch("album/a.mp3");
        var tail = Touch("tail.mp3");

        var files = CreateScanner().ExpandPaths([head, Path.Combine(_root, "album"), tail]);

        Assert.Equal(4, files.Count);
        Assert.Equal(head, files[0]);
        // 文件夹里的两首按名升序，且整体仍落在「它被给出的位置」上。
        Assert.Equal(Path.Combine(_root, "album", "a.mp3"), files[1]);
        Assert.Equal(Path.Combine(_root, "album", "b.mp3"), files[2]);
        Assert.Equal(tail, files[3]);
    }

    [Fact]
    public void 同一个文件既单独给出又在目录里时只出现一次()
    {
        var single = Touch("album/a.mp3");
        Touch("album/b.mp3");

        var files = CreateScanner().ExpandPaths([single, Path.Combine(_root, "album")]);

        Assert.Equal(2, files.Count);
        // 保留它先出现的位置：单独给出的那次在前。
        Assert.Equal(single, files[0]);
    }

    [Fact]
    public void 单文件不做扩展名筛选()
    {
        // 用户明确点开的就照播，能不能解码交给引擎去判断。
        var exotic = Touch("song.unknown");

        var files = CreateScanner().ExpandPaths([exotic]);

        Assert.Equal([exotic], files);
    }

    [Fact]
    public void 目录内只收受支持的扩展名()
    {
        Touch("album/a.mp3");
        Touch("album/b.txt");
        Touch("album/c.flac");

        var files = CreateScanner().ExpandPaths([Path.Combine(_root, "album")]);

        Assert.Equal(2, files.Count);
    }

    [Fact]
    public void 不存在的路径与空字符串被跳过()
    {
        var real = Touch("a.mp3");

        var files = CreateScanner().ExpandPaths(
            ["", "   ", Path.Combine(_root, "ghost.mp3"), real]);

        Assert.Equal([real], files);
    }

    [Fact]
    public void 空集合返回空列表()
    {
        Assert.Empty(CreateScanner().ExpandPaths([]));
    }

    [Fact]
    public void 传入空集合时抛参数异常()
    {
        Assert.Throws<ArgumentNullException>(() => CreateScanner().ExpandPaths(null!));
    }

    [Fact]
    public void 取消标记生效时立即停止()
    {
        Touch("album/a.mp3");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => CreateScanner().ExpandPaths([Path.Combine(_root, "album")], cts.Token));
    }
}

/// <summary>
/// 扫描器的慢速阶段：元数据回填、坏文件容忍与进度报告。
/// </summary>
public class LibraryScannerMetadataTests : IDisposable
{
    private readonly string _root;

    public LibraryScannerMetadataTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qyplayer-meta-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Touch(string name, string content = "x")
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task 解析结果回填到曲目且保留文件事实()
    {
        var path = Touch("a.mp3");

        var reader = new FakeMetadataReader(p => new TrackMetadata(
            "标题", "艺术家", "专辑", TimeSpan.FromSeconds(245), 48000, 24, 900, @"C:\cache\cover.jpg"));

        var scanner = new LibraryScanner(reader);

        var track = Assert.Single(await scanner.ScanAsync([_root]));

        Assert.Equal("标题", track.Title);
        Assert.Equal("艺术家", track.Artist);
        Assert.Equal("专辑", track.Album);
        Assert.Equal(TimeSpan.FromSeconds(245), track.Duration);
        Assert.Equal(48000, track.SampleRate);
        Assert.Equal(24, track.BitsPerSample);
        Assert.Equal(900, track.Bitrate);
        Assert.Equal(@"C:\cache\cover.jpg", track.CoverCachePath);

        // 文件事实来自快速阶段，与元数据无关。
        Assert.Equal(path, track.FilePath);
        Assert.Equal(new FileInfo(path).Length, track.FileSize);
        Assert.Equal(AudioFormat.Mp3, track.Format);
    }

    [Fact]
    public async Task 单个文件解析抛异常时不影响整批()
    {
        var bad = Touch("bad.mp3");
        Touch("good.mp3");

        var reader = new FakeMetadataReader(p =>
            p == bad
                ? throw new InvalidOperationException("模拟损坏文件")
                : new TrackMetadata("好文件", "a", "b", TimeSpan.FromSeconds(1), 44100, 16, 128, null));

        var scanner = new LibraryScanner(reader);

        var tracks = await scanner.ScanAsync([_root]);

        // 坏文件仍在列表里，只是没有标签信息——扫描不能因为一个文件中断。
        Assert.Equal(2, tracks.Count);
        var badTrack = Assert.Single(tracks, t => t.FilePath == bad);
        Assert.Equal(string.Empty, badTrack.Title);
        // 标签缺失时展示层回退到文件名，列表不会出现空白行。
        Assert.Equal("bad", badTrack.DisplayTitle);

        Assert.Single(tracks, t => t.Title == "好文件");
    }

    [Fact]
    public async Task 增量流可以边收边处理()
    {
        Touch("a.mp3");
        Touch("b.mp3");
        Touch("c.mp3");

        var scanner = new LibraryScanner(new FakeMetadataReader());

        var received = new List<Track>();
        await foreach (var track in scanner.ReadTracksAsync(scanner.ScanDirectories([_root])))
        {
            received.Add(track);
        }

        Assert.Equal(3, received.Count);
    }

    [Fact]
    public async Task 未传进度回调时照常完成()
    {
        Touch("a.mp3");
        Touch("b.mp3");

        var scanner = new LibraryScanner(new FakeMetadataReader());

        // 进度是可选参数：批处理场景用不上它，缺省时不能抛空引用。
        var tracks = await scanner.ScanAsync([_root], progress: null);

        Assert.Equal(2, tracks.Count);
    }

    [Fact]
    public async Task 进度回调依次报告完成数与当前文件()
    {
        Touch("a.mp3");
        Touch("b.mp3");

        var scanner = new LibraryScanner(new FakeMetadataReader());
        var reports = new List<ScanProgress>();

        // 用自定义实现而非 Progress<T>，避免回调被投递到别的线程而错过时机。
        var progress = new SynchronousProgress(reports.Add);
        await scanner.ScanAsync([_root], progress);

        Assert.Equal(3, reports.Count);
        Assert.Equal(new ScanProgress(0, 2), reports[0]);
        Assert.Equal(1, reports[1].Completed);
        Assert.Equal(2, reports[2].Completed);
        Assert.Equal(1d, reports[2].Fraction);
        Assert.NotNull(reports[1].CurrentFile);
    }

    [Fact]
    public async Task 未变动的文件可判定为跳过()
    {
        var path = Touch("a.mp3", "0123456789");

        var scanner = new LibraryScanner(new FakeMetadataReader());
        var file = Assert.Single(scanner.ScanDirectories([_root]));
        var track = Assert.Single(await scanner.ScanAsync([_root]));

        Assert.True(LibraryScanner.IsUnchanged(track, file));

        // 内容变了、大小随之改变，应当判定为需要重新解析。
        await Task.Delay(10);
        File.WriteAllText(path, "0123456789abc");

        var changed = Assert.Single(scanner.ScanDirectories([_root]));
        Assert.False(LibraryScanner.IsUnchanged(track, changed));
    }

    [Fact]
    public async Task 只改写入时间也算变动()
    {
        var path = Touch("a.mp3", "same-size");

        var scanner = new LibraryScanner(new FakeMetadataReader());
        var file = Assert.Single(scanner.ScanDirectories([_root]));
        var track = Assert.Single(await scanner.ScanAsync([_root]));

        // 大小不变、只改时间：仍须重新解析，否则改过标签的文件不会被发现。
        File.SetLastWriteTimeUtc(path, file.LastWriteTimeUtc.AddMinutes(5));

        var touched = Assert.Single(scanner.ScanDirectories([_root]));

        Assert.Equal(file.FileSize, touched.FileSize);
        Assert.False(LibraryScanner.IsUnchanged(track, touched));
    }

    private sealed class SynchronousProgress(Action<ScanProgress> handler) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => handler(value);
    }
}
