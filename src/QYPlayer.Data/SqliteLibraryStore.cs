using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using QYPlayer.Core.Library;
using QYPlayer.Core.Models;

namespace QYPlayer.Data;

/// <summary>
/// SQLite 上的曲库实现。
/// </summary>
/// <remarks>
/// <para>
/// 每次操作新建一个 <see cref="QYPlayerDbContext"/>，用完即弃，而不是长期持有一个实例。
/// 曲库的读写都发生在后台线程（扫描、启动加载），共用同一个上下文会让 EF Core 的
/// 变更跟踪器不断累积实体（十万首曲子的跟踪开销不可忽略），也会把并发访问
/// 变成「必须先加锁」的问题。工厂模式代价是每次重建模型映射，换来的是无共享状态。
/// </para>
/// <para>
/// 连接开启 WAL：曲库写入只发生在扫描期间，但此时界面可能正在读列表，
/// 默认的回滚日志模式在读写并发时会直接抛「database is locked」。
/// </para>
/// </remarks>
public sealed class SqliteLibraryStore : ILibraryStore
{
    private readonly IDbContextFactory<QYPlayerDbContext> _contextFactory;
    private readonly ILogger<SqliteLibraryStore>? _logger;

    /// <summary>迁移与批量写入串行化。曲库只有一个写入者，但扫描与启动加载可能撞车。</summary>
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private bool _initialized;

    public SqliteLibraryStore(
        IDbContextFactory<QYPlayerDbContext> contextFactory,
        ILogger<SqliteLibraryStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);

        _contextFactory = contextFactory;
        _logger = logger;
    }

    /// <summary>
    /// 由数据库文件路径构造连接字符串。
    /// </summary>
    /// <remarks>
    /// 单独抽出这个方法是为了把「连接怎么拼」收在一处：
    /// <c>Cache=Shared</c> 让同一进程内的多个上下文共用连接池，
    /// 避免 WAL 模式下每个上下文各开一条连接把文件句柄耗光。
    /// </remarks>
    public static string BuildConnectionString(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        return new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate,
            Cache = Microsoft.Data.Sqlite.SqliteCacheMode.Shared,
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            await using var context = await _contextFactory
                .CreateDbContextAsync(cancellationToken)
                .ConfigureAwait(false);

            // 用 Migrate 而不是 EnsureCreated：EnsureCreated 建出来的库没有迁移历史，
            // 日后加一个字段就只能删库重建，用户曲库里的曲目与播放次数全丢。
            await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);

            await EnableWalAsync(context, cancellationToken).ConfigureAwait(false);

            _initialized = true;
            _logger?.LogInformation("曲库数据库已就绪");
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<IReadOnlyList<Track>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var context = await _contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        // AsNoTracking：读出来的曲目只用于展示与播放，不会被写回，
        // 让 EF Core 省掉为每条记录建立跟踪快照的开销。
        return await context.Tracks
            .AsNoTracking()
            .OrderBy(t => t.FilePath)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<int> UpsertAsync(
        IReadOnlyCollection<Track> tracks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tracks);

        if (tracks.Count == 0)
        {
            return 0;
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var context = await _contextFactory
                .CreateDbContextAsync(cancellationToken)
                .ConfigureAwait(false);

            // 按路径对齐：一次性把涉及的路径查出来，再决定谁插入谁更新。
            // 逐条 Exists 查询会产生 N 次往返，一万首曲子就是一万次查询。
            var paths = tracks.Select(t => t.FilePath).ToList();
            var existing = await context.Tracks
                .Where(t => paths.Contains(t.FilePath))
                .ToDictionaryAsync(t => t.FilePath, StringComparer.OrdinalIgnoreCase, cancellationToken)
                .ConfigureAwait(false);

            var affected = 0;

            foreach (var incoming in tracks)
            {
                if (existing.TryGetValue(incoming.FilePath, out var current))
                {
                    // 原地更新，保留原 Id：Id 将来会被播放列表引用，
                    // 每次扫描换一批新 Id 会让引用全部失效。
                    current.FileSize = incoming.FileSize;
                    current.LastWriteTimeUtc = incoming.LastWriteTimeUtc;
                    current.Title = incoming.Title;
                    current.Artist = incoming.Artist;
                    current.Album = incoming.Album;
                    current.Duration = incoming.Duration;
                    current.Format = incoming.Format;
                    current.SampleRate = incoming.SampleRate;
                    current.BitsPerSample = incoming.BitsPerSample;
                    current.Bitrate = incoming.Bitrate;
                    current.CoverCachePath = incoming.CoverCachePath;
                }
                else
                {
                    context.Tracks.Add(incoming);
                }

                affected++;
            }

            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return affected;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<int> RemoveMissingAsync(
        IReadOnlyCollection<string> keepPaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keepPaths);

        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var context = await _contextFactory
                .CreateDbContextAsync(cancellationToken)
                .ConfigureAwait(false);

            // 注意 keepPaths 为空时不能走 Contains 分支：SQL 里 IN () 是语法错误，
            // 而「一个文件都没扫到」正对应「清空曲库」，是必须支持的情形。
            if (keepPaths.Count == 0)
            {
                var all = await context.Tracks.CountAsync(cancellationToken).ConfigureAwait(false);
                if (all > 0)
                {
                    context.Tracks.RemoveRange(context.Tracks);
                    await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }

                return all;
            }

            var keep = keepPaths.ToList();
            var stale = await context.Tracks
                .Where(t => !keep.Contains(t.FilePath))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (stale.Count == 0)
            {
                return 0;
            }

            context.Tracks.RemoveRange(stale);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return stale.Count;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var context = await _contextFactory
                .CreateDbContextAsync(cancellationToken)
                .ConfigureAwait(false);

            await context.Tracks.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// 把日志模式切到 WAL。失败只记日志不抛——老旧的 FAT32 分区或网络盘
    /// 不支持 WAL，此时退回默认模式仍可正常工作，不该因此挡住启动。
    /// </summary>
    private static async Task EnableWalAsync(
        QYPlayerDbContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            await context.Database
                .ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            // 不支持就算了，默认模式在单写入者的用法下也够用。
        }
    }
}
