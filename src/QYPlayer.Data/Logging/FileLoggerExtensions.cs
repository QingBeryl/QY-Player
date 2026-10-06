using Microsoft.Extensions.Logging;

namespace QYPlayer.Data.Logging;

/// <summary>
/// 把文件日志挂到 <see cref="ILoggingBuilder"/> 上的入口。
/// </summary>
/// <remarks>
/// 做成扩展方法与 <c>AddDebug</c> 这类内置方法保持同样的用法，
/// 宿主侧只需要一行，不必知道 provider 的构造细节。
/// </remarks>
public static class FileLoggerExtensions
{
    /// <summary>
    /// 添加按天滚动的文件日志。
    /// </summary>
    /// <param name="builder">日志构建器。</param>
    /// <param name="directory">日志目录。</param>
    /// <param name="minimumLevel">最低级别，默认 Information。</param>
    /// <param name="retentionDays">保留天数，默认 7 天。</param>
    public static ILoggingBuilder AddDailyFile(
        this ILoggingBuilder builder,
        string directory,
        LogLevel minimumLevel = LogLevel.Information,
        int retentionDays = 7)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        builder.AddProvider(new DailyFileLoggerProvider(directory, minimumLevel, retentionDays));

        return builder;
    }
}
