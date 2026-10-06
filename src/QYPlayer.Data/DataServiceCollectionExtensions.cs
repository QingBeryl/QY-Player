using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QYPlayer.Core.Library;
using QYPlayer.Core.Settings;

namespace QYPlayer.Data;

/// <summary>
/// 持久化层的注册入口。宿主只需要调一次，不必知道连接串与文件路径怎么拼。
/// </summary>
public static class DataServiceCollectionExtensions
{
    /// <summary>
    /// 注册曲库持久化（SQLite + EF Core）。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="databasePath">数据库文件路径。</param>
    /// <remarks>
    /// 用 <c>AddDbContextFactory</c> 而不是 <c>AddDbContext</c>：
    /// 曲库的读写发生在后台线程，且同一时刻可能既有扫描在写、又有启动加载在读。
    /// 工厂让每个操作各自拿到一个短生命周期的上下文，从而避开
    /// 「同一个 DbContext 不能并发使用」这一 EF Core 的硬约束，
    /// 也避免长期持有上下文时变更跟踪器不断累积实体。
    /// </remarks>
    public static IServiceCollection AddLibraryPersistence(
        this IServiceCollection services,
        string databasePath)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var connectionString = SqliteLibraryStore.BuildConnectionString(databasePath);

        services.AddDbContextFactory<QYPlayerDbContext>((serviceProvider, options) =>
        {
            options.UseSqlite(connectionString, sqlite =>
                // 大曲库首次入库与整库比对是长查询，默认超时偏紧。
                sqlite.CommandTimeout(30));

            // 把 EF Core 的日志接进应用的日志体系。曲库出问题时（唯一索引冲突、
            // 迁移失败、查询退化成全表扫描），日志里有没有那条 SQL 直接决定能不能定位。
            // 具体级别由宿主侧的日志配置决定，这里只负责把两个体系连起来。
            // 注意不能再叠一个 LogTo：它会覆盖掉这里的 logger factory。
            var loggerFactory = serviceProvider.GetService<Microsoft.Extensions.Logging.ILoggerFactory>();
            if (loggerFactory is not null)
            {
                options.UseLoggerFactory(loggerFactory);
            }
        });

        services.AddSingleton<ILibraryStore, SqliteLibraryStore>();

        return services;
    }

    /// <summary>
    /// 注册设置存储（独立 JSON 文件）。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="filePath">设置文件路径。</param>
    /// <remarks>
    /// 注册为单例并在构造时就把文件读进来：设置的消费者（音量、主题、窗口位置）
    /// 分布在启动流程各处，用的时候再去读文件会把「文件 I/O 可能失败」
    /// 这件事扩散到每一个调用点。
    /// </remarks>
    public static IServiceCollection AddSettingsStore(
        this IServiceCollection services,
        string filePath)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        services.AddSingleton<ISettingsStore>(sp =>
            new JsonSettingsStore(
                filePath,
                sp.GetService<Microsoft.Extensions.Logging.ILogger<JsonSettingsStore>>()));

        return services;
    }
}
