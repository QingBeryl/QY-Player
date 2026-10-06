using QYPlayer.Core.Library;
using QYPlayer.Core.Models;

namespace QYPlayer.Tests;

/// <summary>
/// 曲库同步的增量比对。
/// </summary>
/// <remarks>
/// 这是需求文档 8.2 的核心承诺所在：启动时兜底与运行期监听走同一条比对路径，
/// 未变化的文件绝不重新解析。这里的用例用 <see cref="FakeMetadataReader"/> 的
/// 调用次数直接锁住「跳过」这件事——否则它只会在真实曲库上表现为
/// 「每次启动都卡几秒」，很难在功能层面察觉。
/// </remarks>
public class LibrarySynchronizerTests : IDisposable
{
    private readonly string _root;

    public LibrarySynchronizerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qyplayer-sync-" + Guid.NewGuid().ToString("N"));
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

    private static LibrarySynchronizer Synchronizer(FakeMetadataReader reader) =>
        new(new LibraryScanner(reader));

    /// <summary>把上次同步的结果当作「库里已有的记录」。</summary>
    private static Track[] AsKnown(LibrarySyncResult result) => [.. result.Tracks];

    [Fact]
    public async Task 首次同步解析全部文件()
    {
        Touch("a.mp3");
        Touch("b.flac");

        var reader = new FakeMetadataReader();

        var result = await Synchronizer(reader).SyncAsync([], [_root]);

        Assert.Equal(2, result.Tracks.Count);
        Assert.Equal(2, result.ParsedCount);
        Assert.Equal(2, result.NeedUpsert.Count);
        Assert.Empty(result.RemovedPaths);
    }

    [Fact]
    public async Task 未变化的文件不重新解析()
    {
        Touch("a.mp3");
        Touch("b.mp3");

        var reader = new FakeMetadataReader();
        var synchronizer = Synchronizer(reader);

        var first = await synchronizer.SyncAsync([], [_root]);
        Assert.Equal(2, reader.CallCount);

        var second = await synchronizer.SyncAsync(AsKnown(first), [_root]);

        // 第二次一次元数据都不该读——这就是「启动时兜底」的代价所在。
        Assert.Equal(2, reader.CallCount);
        Assert.Equal(0, second.ParsedCount);
        Assert.Empty(second.NeedUpsert);
        Assert.Equal(2, second.Tracks.Count);
    }

    [Fact]
    public async Task 内容变化后只重新解析那一首()
    {
        Touch("a.mp3", "short");
        Touch("b.mp3", "short");

        var reader = new FakeMetadataReader();
        var synchronizer = Synchronizer(reader);
        var first = await synchronizer.SyncAsync([], [_root]);

        // 改长度会同时改大小与最后写入时间，两条比对口径都命中。
        Touch("a.mp3", "much longer content");

        var second = await synchronizer.SyncAsync(AsKnown(first), [_root]);

        Assert.Equal(3, reader.CallCount);
        Assert.Equal(1, second.ParsedCount);
        var upserted = Assert.Single(second.NeedUpsert);
        Assert.EndsWith("a.mp3", upserted.FilePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task 新增文件被收进结果并标记需要写库()
    {
        Touch("a.mp3");

        var reader = new FakeMetadataReader();
        var synchronizer = Synchronizer(reader);
        var first = await synchronizer.SyncAsync([], [_root]);

        Touch("b.mp3");

        var second = await synchronizer.SyncAsync(AsKnown(first), [_root]);

        Assert.Equal(2, second.Tracks.Count);
        Assert.Equal(1, second.ParsedCount);
        Assert.Single(second.NeedUpsert);
        Assert.EndsWith("b.mp3", second.NeedUpsert[0].FilePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task 消失的文件汇报为待删除()
    {
        var kept = Touch("a.mp3");
        var deleted = Touch("b.mp3");

        var reader = new FakeMetadataReader();
        var synchronizer = Synchronizer(reader);
        var first = await synchronizer.SyncAsync([], [_root]);

        File.Delete(deleted);

        var second = await synchronizer.SyncAsync(AsKnown(first), [_root]);

        Assert.Single(second.Tracks);
        Assert.Equal(kept, second.Tracks[0].FilePath);
        var removed = Assert.Single(second.RemovedPaths);
        Assert.Equal(deleted, removed);

        // 删除不需要解析任何文件，只是比对结果里少了一条。
        Assert.Equal(0, second.ParsedCount);
    }

    [Fact]
    public async Task 结果与曲库读取一样按路径升序()
    {
        // 顺序必须与 SqliteLibraryStore.LoadAsync 的 ORDER BY FilePath 一致，
        // 否则每次同步都会让列表整块重排，用户滚动的位置会跳掉。
        Touch("z.mp3");
        Touch("a.mp3");
        Touch("m.mp3");

        var result = await Synchronizer(new FakeMetadataReader()).SyncAsync([], [_root]);

        var paths = result.Tracks.Select(track => Path.GetFileName(track.FilePath)).ToArray();
        Assert.Equal(["a.mp3", "m.mp3", "z.mp3"], paths);
    }

    [Fact]
    public async Task 目录为空时清空曲库()
    {
        var reader = new FakeMetadataReader();
        var synchronizer = Synchronizer(reader);
        var first = await synchronizer.SyncAsync([], [_root]);
        Assert.Empty(first.Tracks);

        var second = await synchronizer.SyncAsync(
            [new Track { FilePath = Path.Combine(_root, "ghost.mp3") }],
            [_root]);

        Assert.Empty(second.Tracks);
        Assert.Single(second.RemovedPaths);
    }

    [Fact]
    public async Task 根目录不存在时不报错也不删库()
    {
        // 用户可能把整个音乐文件夹删了，或者插着的外置盘没接上。
        // 此时按 8.2 的兜底口径，扫描结果为空即「全都不可见」，
        // 但绝不能因此把库清空——那样接回磁盘后所有元数据都要重解析。
        var missing = Path.Combine(_root, "not-created");

        var result = await Synchronizer(new FakeMetadataReader()).SyncAsync([], [missing]);

        Assert.Empty(result.Tracks);
        Assert.Empty(result.RemovedPaths);
        Assert.Equal(0, result.ParsedCount);
    }

    [Fact]
    public async Task 元数据读取失败的文件仍然入列()
    {
        // 损坏文件必须留在列表里：用户需要看见它才能决定怎么处理，
        // 悄悄丢掉会让「明明有这首歌却找不到」变得无从排查。
        var broken = Touch("broken.mp3");

        var reader = new FakeMetadataReader(_ => throw new InvalidDataException("标签损坏"));
        var result = await Synchronizer(reader).SyncAsync([], [_root]);

        Assert.Single(result.Tracks);
        Assert.Equal(broken, result.Tracks[0].FilePath);

        // 标题回退为文件名，列表不会出现空白行。
        Assert.Equal("broken", result.Tracks[0].DisplayTitle);
    }

    [Fact]
    public async Task 进度回调覆盖到每个待解析的文件()
    {
        Touch("a.mp3");
        Touch("b.mp3");
        Touch("c.mp3");

        var reports = new List<ScanProgress>();
        // 直接用同步实现，避免 Progress<T> 把回调投递到别的线程导致断言竞态。
        var progress = new SynchronousProgress(reports.Add);

        await Synchronizer(new FakeMetadataReader()).SyncAsync([], [_root], progress);

        Assert.Equal(3, reports[^1].Completed);
        Assert.Equal(3, reports[^1].Total);
    }

    private sealed class SynchronousProgress(Action<ScanProgress> handler) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => handler(value);
    }
}
