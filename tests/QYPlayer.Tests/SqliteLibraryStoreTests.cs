using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using QYPlayer.Core.Models;
using QYPlayer.Data;

namespace QYPlayer.Tests;

/// <summary>
/// 曲库落库的往返测试，落在真实的临时 SQLite 文件上。
/// </summary>
/// <remarks>
/// 刻意不用内存库：内存库不产生 -wal / -shm 文件、不经历迁移脚本建表的完整过程，
/// 也无法验证「迁移生成的表结构与实体映射真的对得上」。
/// 这里最想确认的恰恰是这几件事——库能建起来、字段能原样进出、
/// 大小写不同的路径不会变成两首曲子。
/// </remarks>
public sealed class SqliteLibraryStoreTests : IDisposable
{
    private readonly string _root;
    private readonly string _databasePath;

    public SqliteLibraryStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qyplayer-db-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _databasePath = Path.Combine(_root, "library.db");
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

    private SqliteLibraryStore CreateStore() => new(new TestDbContextFactory(_databasePath));

    /// <summary>构造一首字段齐全的曲目，便于逐项比对往返结果。</summary>
    private static Track NewTrack(string filePath) => new()
    {
        FilePath = filePath,
        FileSize = 5_432_100,
        LastWriteTimeUtc = new DateTime(2026, 10, 6, 8, 12, 33, 456, DateTimeKind.Utc),
        Title = "测试标题",
        Artist = "测试艺术家",
        Album = "测试专辑",
        Duration = TimeSpan.FromMilliseconds(245_678),
        Format = AudioFormat.Flac,
        SampleRate = 96_000,
        BitsPerSample = 24,
        Bitrate = 2_304,
        CoverCachePath = @"C:\cache\cover-1.jpg",
    };

    [Fact]
    public async Task Upsert_之后能逐字段原样读回()
    {
        var store = CreateStore();
        var original = NewTrack(@"C:\Music\a.flac");

        Assert.Equal(1, await store.UpsertAsync([original]));

        var loaded = Assert.Single(await store.LoadAsync());

        Assert.Equal(original.Id, loaded.Id);
        Assert.Equal(original.FilePath, loaded.FilePath);
        Assert.Equal(original.FileSize, loaded.FileSize);
        Assert.Equal(original.LastWriteTimeUtc, loaded.LastWriteTimeUtc);
        Assert.Equal(original.Title, loaded.Title);
        Assert.Equal(original.Artist, loaded.Artist);
        Assert.Equal(original.Album, loaded.Album);
        Assert.Equal(original.Duration, loaded.Duration);
        Assert.Equal(original.Format, loaded.Format);
        Assert.Equal(original.SampleRate, loaded.SampleRate);
        Assert.Equal(original.BitsPerSample, loaded.BitsPerSample);
        Assert.Equal(original.Bitrate, loaded.Bitrate);
        Assert.Equal(original.CoverCachePath, loaded.CoverCachePath);
    }

    [Fact]
    public async Task 时间读回来仍是_Utc_且精确到_tick()
    {
        var store = CreateStore();
        var original = NewTrack(@"C:\Music\a.mp3");

        await store.UpsertAsync([original]);

        var loaded = Assert.Single(await store.LoadAsync());

        // 存 Ticks 而不是文本：既保住 Kind，也避免字符串比较影响排序。
        Assert.Equal(DateTimeKind.Utc, loaded.LastWriteTimeUtc.Kind);
        Assert.Equal(original.LastWriteTimeUtc.Ticks, loaded.LastWriteTimeUtc.Ticks);
        Assert.Equal(original.Duration.Ticks, loaded.Duration.Ticks);
    }

    [Fact]
    public async Task 枚举与时长在库里存成字符串名与整数()
    {
        var store = CreateStore();
        var original = NewTrack(@"C:\Music\a.flac");

        await store.UpsertAsync([original]);

        await using var connection = new SqliteConnection(SqliteLibraryStore.BuildConnectionString(_databasePath));
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Format, Duration, LastWriteTimeUtc, CoverCachePath FROM Tracks;";

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        // 枚举存名字而不是序号：日后往 AudioFormat 中间插一个成员，
        // 存序号的库会把所有老曲子的格式解成别的值。
        Assert.Equal(nameof(AudioFormat.Flac), reader.GetString(0));

        // 时间与时长存整数：存文本会让 ORDER BY 走字符串比较。
        Assert.Equal(typeof(long), reader.GetFieldType(1));
        Assert.Equal(typeof(long), reader.GetFieldType(2));
        Assert.Equal(original.Duration.Ticks, reader.GetInt64(1));
        Assert.Equal(original.LastWriteTimeUtc.Ticks, reader.GetInt64(2));
        Assert.Equal(original.CoverCachePath, reader.GetString(3));
    }

    [Fact]
    public async Task 同一路径再次入库是更新而不是复制()
    {
        var store = CreateStore();
        var first = NewTrack(@"C:\Music\a.mp3");
        await store.UpsertAsync([first]);

        var second = NewTrack(@"C:\Music\a.mp3");
        second.Title = "改过的标题";
        await store.UpsertAsync([second]);

        var loaded = Assert.Single(await store.LoadAsync());

        Assert.Equal("改过的标题", loaded.Title);

        // Id 必须保留：将来播放列表按 Id 引用曲目，
        // 每次重扫都换一批新 Id 会让所有引用失效。
        Assert.Equal(first.Id, loaded.Id);
    }

    [Fact]
    public async Task 路径大小写不同不会变成两首()
    {
        var store = CreateStore();
        await store.UpsertAsync([NewTrack(@"C:\Music\Song.MP3")]);
        await store.UpsertAsync([NewTrack(@"c:\music\song.mp3")]);

        // Windows 上路径大小写不敏感，扫描器也按 OrdinalIgnoreCase 去重，
        // 数据库这一层必须用同一种口径，否则唯一索引与去重逻辑会互相打架。
        Assert.Single(await store.LoadAsync());
    }

    [Fact]
    public async Task RemoveMissing_空集合表示清空曲库()
    {
        var store = CreateStore();
        await store.UpsertAsync([NewTrack(@"C:\Music\a.mp3"), NewTrack(@"C:\Music\b.mp3")]);

        // 「一个文件都没扫到」是删除整库的正常路径（用户把音乐文件夹清空了），
        // 不能因为 SQL 里 IN () 是语法错误就抛异常。
        Assert.Equal(2, await store.RemoveMissingAsync([]));
        Assert.Empty(await store.LoadAsync());
    }

    [Fact]
    public async Task RemoveMissing_只删没被扫到的那些()
    {
        var store = CreateStore();
        await store.UpsertAsync([
            NewTrack(@"C:\Music\a.mp3"),
            NewTrack(@"C:\Music\b.mp3"),
            NewTrack(@"C:\Music\c.mp3"),
        ]);

        Assert.Equal(1, await store.RemoveMissingAsync([@"C:\Music\a.mp3", @"C:\Music\b.mp3"]));

        var remaining = await store.LoadAsync();
        Assert.Equal(2, remaining.Count);
        Assert.DoesNotContain(remaining, t => t.FilePath.EndsWith("c.mp3", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RemoveMissing_全都在时不做任何改动()
    {
        var store = CreateStore();
        await store.UpsertAsync([NewTrack(@"C:\Music\a.mp3")]);

        Assert.Equal(0, await store.RemoveMissingAsync([@"C:\Music\a.mp3"]));
        Assert.Single(await store.LoadAsync());
    }

    [Fact]
    public async Task Clear_之后曲库为空()
    {
        var store = CreateStore();
        await store.UpsertAsync([NewTrack(@"C:\Music\a.mp3"), NewTrack(@"C:\Music\b.mp3")]);

        await store.ClearAsync();

        Assert.Empty(await store.LoadAsync());
    }

    [Fact]
    public async Task 首次使用无需显式初始化()
    {
        var store = CreateStore();

        // 界面路径上不应该有「忘记先 Initialize」这种失败方式：
        // 每个公开方法都自己保证库已就绪。
        Assert.Empty(await store.LoadAsync());
        Assert.True(File.Exists(_databasePath));
    }

    [Fact]
    public async Task 空集合入库不会建库()
    {
        var store = CreateStore();

        Assert.Equal(0, await store.UpsertAsync([]));

        // 没有曲目就没有必要建库文件，避免用户第一次启动就留下一个空库。
        Assert.False(File.Exists(_databasePath));
    }

    [Fact]
    public async Task 并发入库不互相冲突()
    {
        var store = CreateStore();

        // 扫描与启动加载可能同时触发写入，串行化由实现里的信号量保证。
        await Task.WhenAll(Enumerable.Range(0, 10).Select(i =>
            store.UpsertAsync([NewTrack($@"C:\Music\track-{i}.mp3")])));

        Assert.Equal(10, (await store.LoadAsync()).Count);
    }

    [Fact]
    public async Task 读回来的顺序稳定()
    {
        var store = CreateStore();
        await store.UpsertAsync([
            NewTrack(@"C:\Music\c.mp3"),
            NewTrack(@"C:\Music\a.mp3"),
            NewTrack(@"C:\Music\b.mp3"),
        ]);

        var loaded = await store.LoadAsync();

        // 顺序由路径的 NOCASE 排序决定，界面每次打开看到的排列才会一致。
        var expected = loaded.Select(t => t.FilePath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(expected, loaded.Select(t => t.FilePath));
    }
}

/// <summary>
/// 测试用的上下文工厂：直接拿连接串建上下文，绕开 DI 容器。
/// </summary>
/// <remarks>
/// 生产代码走 <c>AddDbContextFactory</c>，但那层注册在测试里只是噪声——
/// 这里要验证的是存储逻辑与映射，不是服务注册。
/// </remarks>
internal sealed class TestDbContextFactory : IDbContextFactory<QYPlayerDbContext>
{
    private readonly DbContextOptions<QYPlayerDbContext> _options;

    public TestDbContextFactory(string databasePath)
    {
        _options = new DbContextOptionsBuilder<QYPlayerDbContext>()
            .UseSqlite(SqliteLibraryStore.BuildConnectionString(databasePath))
            .Options;
    }

    public QYPlayerDbContext CreateDbContext() => new(_options);
}
