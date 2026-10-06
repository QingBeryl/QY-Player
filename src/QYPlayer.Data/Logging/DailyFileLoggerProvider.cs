using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace QYPlayer.Data.Logging;

/// <summary>
/// 按天滚动的极简文件日志，落地需求文档 8.2 的结论。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么自己写而不是引入 Serilog。</b> 需要的只有「写文本、按天滚动、只留最近几天、
/// 不拖慢调用方」四件事，成熟库能提供的其余能力（结构化查询、多目标、异步批量落盘）
/// 对当前规模都用不上，而依赖与打包体积是实打实的。自定义的另一个好处是格式完全可控：
/// 日志里会出现用户的文件路径，属于本地数据，级别、时间格式与是否输出异常栈都由这里决定。
/// </para>
/// <para>
/// <b>为什么不做异步队列。</b> 日志量本身很小（一次扫描几百行），而队列会引入
/// 「进程退出时还在队列里的日志丢失」与「队列满了怎么办」两个新问题。
/// 改为同步写、但把每条记录压缩成一行、用 <see cref="StreamWriter"/> 常开并设置了
/// <c>AutoFlush</c>，代价是一次几百字节的写入，换来的是崩溃前最后一条日志一定在盘上——
/// 而这一条通常正是排查崩溃时最想看的那条。
/// </para>
/// </remarks>
public sealed class DailyFileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly int _retentionDays;
    private readonly LogLevel _minimumLevel;

    /// <summary>当前这一天的写入器。跨天时替换。</summary>
    private readonly ConcurrentDictionary<string, StreamWriter> _writers = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _rollGate = new();

    private DateTime _currentDate;
    private bool _disposed;

    /// <param name="directory">日志目录。</param>
    /// <param name="minimumLevel">低于此级别的日志直接丢弃。</param>
    /// <param name="retentionDays">保留天数，超出的旧文件在滚动时删除。</param>
    public DailyFileLoggerProvider(
        string directory,
        LogLevel minimumLevel = LogLevel.Information,
        int retentionDays = 7)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        _directory = directory;
        _retentionDays = Math.Max(retentionDays, 1);
        _minimumLevel = minimumLevel;

        _currentDate = DateTime.Now.Date;

        Directory.CreateDirectory(_directory);
        CleanupExpiredFiles();
    }

    /// <summary>
    /// 日志文件名前缀。曲目扫描相关的日志按类别分文件读起来更清楚，
    /// 但保留完整类别名会让文件名过长（有路径长度与非法字符两个问题），
    /// 因此只用类别名的最后一段。
    /// </summary>
    public ILogger CreateLogger(string categoryName) =>
        new DailyFileLogger(this, categoryName, _minimumLevel);

    internal void Write(string categoryName, LogLevel level, EventId eventId, string message, Exception? exception)
    {
        if (_disposed || level < _minimumLevel)
        {
            return;
        }

        var writer = GetWriter();
        if (writer is null)
        {
            return;
        }

        var builder = new StringBuilder(message.Length + 128);
        builder.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
            .Append(' ')
            .Append(FormatLevel(level))
            .Append(' ')
            .Append(ShortCategory(categoryName));

        if (eventId.Id != 0)
        {
            builder.Append(" [").Append(eventId.Id).Append(']');
        }

        // 换行会被替换掉：日志按行读取时，一条记录跨多行会让排查变得很难受，
        // 而日志库自己的换行恰好是为了「好看」而不是为了承载信息。
        builder.Append(": ").Append(message.ReplaceLineEndings(" "));

        if (exception is not null)
        {
            builder.Append(" | ").Append(exception.ToString().ReplaceLineEndings(" | "));
        }

        try
        {
            lock (_rollGate)
            {
                writer.WriteLine(builder.ToString());
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException)
        {
            // 写日志失败绝不能再抛：调用点遍布各处，抛出去会把正常业务逻辑一起带崩。
            // 这里静默丢弃这一条，是最不容易造成二次伤害的选择。
        }
    }

    private StreamWriter? GetWriter()
    {
        var today = DateTime.Now.Date;

        lock (_rollGate)
        {
            if (today != _currentDate)
            {
                // 跨天：关掉旧的，开新的，顺手清一次过期文件。
                CloseWriters();
                _currentDate = today;
                CleanupExpiredFiles();
            }

            var path = Path.Combine(_directory, $"qyplayer-{today:yyyyMMdd}.log");

            if (_writers.TryGetValue(path, out var existing))
            {
                return existing;
            }

            try
            {
                var writer = new StreamWriter(
                    // FileShare.ReadWrite 而不是 FileShare.Read：写入器在整个进程生命周期内
                    // 一直持有这个文件，共享模式若是只读，用户在程序运行时用编辑器打开当天日志
                    // 会被系统的共享冲突挡掉。日志只追加，放宽共享不会带来一致性问题。
                    new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                {
                    // 每次写入就落盘。日志的价值在崩溃之后，缓冲住的那些行恰好会丢掉。
                    AutoFlush = true,
                };

                _writers[path] = writer;
                return writer;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// 删除超过保留期的日志文件。只处理本程序生成的文件名模式，
    /// 不会误删用户放进这个目录的其他东西。
    /// </summary>
    private void CleanupExpiredFiles()
    {
        try
        {
            var threshold = DateTime.Now.Date.AddDays(-_retentionDays + 1);

            foreach (var file in Directory.EnumerateFiles(_directory, "qyplayer-*.log"))
            {
                var name = Path.GetFileNameWithoutExtension(file);

                // qyplayer-yyyyMMdd
                if (name.Length != "qyplayer-".Length + 8)
                {
                    continue;
                }

                if (!DateTime.TryParseExact(
                        name["qyplayer-".Length..],
                        "yyyyMMdd",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None,
                        out var date))
                {
                    continue;
                }

                if (date < threshold)
                {
                    TryDelete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // 清理失败不影响写日志。
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 文件被占用（用户正开着看）时跳过，下次启动再清。
        }
    }

    private void CloseWriters()
    {
        foreach (var entry in _writers)
        {
            try
            {
                entry.Value.Dispose();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // 关闭失败也继续，反正进程还活着，下次写入会创建新文件。
            }
        }

        _writers.Clear();
    }

    private static string FormatLevel(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???",
    };

    /// <summary>类别名只留最后一段，让日志行不至于被命名空间挤满。</summary>
    private static string ShortCategory(string categoryName)
    {
        if (string.IsNullOrEmpty(categoryName))
        {
            return "-";
        }

        var index = categoryName.LastIndexOf('.');
        var name = index >= 0 && index < categoryName.Length - 1
            ? categoryName[(index + 1)..]
            : categoryName;

        // 泛型类别的名字里会带上类型参数的全名（例如 MainViewModel`1[XXX]），
        // 截到反引号之前，保住可读性。
        var tick = name.IndexOf('`');
        return tick > 0 ? name[..tick] : name;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_rollGate)
        {
            _disposed = true;
            CloseWriters();
        }
    }
}
