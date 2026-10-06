using Microsoft.Extensions.Logging;

namespace QYPlayer.Data.Logging;

/// <summary>
/// 写日志的 <see cref="ILogger"/> 实现，把记录转发给 provider。
/// </summary>
/// <remarks>
/// 只实现 <see cref="ILogger"/>，不实现 <c>ILogger&lt;T&gt;</c>：
/// 泛型接口由 <c>LoggerFactory</c> 负责包装，provider 只需要处理「写入」这一件事。
/// <see cref="IsEnabled"/> 如实反映级别，让调用方在拼字符串之前就能跳过，
/// 否则被丢掉的 Debug 日志仍会付出格式化字符串的代价。
/// </remarks>
internal sealed class DailyFileLogger : ILogger
{
    private readonly DailyFileLoggerProvider _provider;
    private readonly string _categoryName;
    private readonly LogLevel _minimumLevel;

    public DailyFileLogger(DailyFileLoggerProvider provider, string categoryName, LogLevel minimumLevel)
    {
        _provider = provider;
        _categoryName = categoryName;
        _minimumLevel = minimumLevel;
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull =>
        // 不实现作用域：文件日志是给人肉眼看的，加了作用域前缀只会让每行更长。
        // 需要上下文时在消息里写明（现有代码的日志消息都是这样写的）。
        null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= _minimumLevel && logLevel != LogLevel.None;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        if (!IsEnabled(logLevel))
        {
            return;
        }

        _provider.Write(
            _categoryName,
            logLevel,
            eventId,
            formatter(state, exception),
            exception);
    }
}
