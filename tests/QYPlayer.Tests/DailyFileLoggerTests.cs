using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QYPlayer.Data.Logging;

namespace QYPlayer.Tests;

/// <summary>
/// 文件日志的用例：写得出、格式稳定、旧文件会被清掉、失败不外抛。
/// </summary>
/// <remarks>
/// 日志是所有其他功能的排查依据，它自己坏掉的代价是「出问题时没有任何线索」，
/// 而且这种坏掉通常不会在开发时被发现。因此这里覆盖的是
/// 「写了之后文件里真的有那一行」与「目录里出现奇怪东西时不去碰它」。
/// </remarks>
public sealed class DailyFileLoggerTests : IDisposable
{
    private readonly string _directory;

    public DailyFileLoggerTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "qyplayer-log-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // 清理失败不影响用例结论。
        }
    }

    private string CurrentLogFile => Path.Combine(_directory, $"qyplayer-{DateTime.Now:yyyyMMdd}.log");

    /// <summary>
    /// 读日志内容。刻意不走 <see cref="File.ReadAllLines(string)"/>：
    /// 它以 <c>FileShare.Read</c> 打开，与仍持写句柄的 provider 冲突，
    /// 而真实场景（用户或外部工具在程序运行时打开当天日志）走的是共享读写。
    /// </summary>
    private string[] ReadLogLines()
    {
        using var stream = new FileStream(
            CurrentLogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);

        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return [.. lines];
    }

    [Fact]
    public void 日志目录不存在时会自动创建()
    {
        using var provider = new DailyFileLoggerProvider(_directory);

        var logger = provider.CreateLogger("QYPlayer.Tests");
        logger.LogInformation("启动");

        Assert.True(Directory.Exists(_directory));
        Assert.True(File.Exists(CurrentLogFile));
    }

    [Fact]
    public void 消息按一行一条写出()
    {
        using var provider = new DailyFileLoggerProvider(_directory);
        provider.CreateLogger("QYPlayer.Tests").LogInformation("第一条");

        // AutoFlush 让内容立刻落盘，不需要等待 Dispose。
        var lines = ReadLogLines();

        var line = Assert.Single(lines);
        Assert.Contains("INF", line, StringComparison.Ordinal);
        Assert.Contains("Tests", line, StringComparison.Ordinal);
        Assert.Contains("第一条", line, StringComparison.Ordinal);
    }

    [Fact]
    public void 多行消息被压成一行()
    {
        using var provider = new DailyFileLoggerProvider(_directory);
        provider.CreateLogger("QYPlayer.Tests").LogInformation("第一行{NewLine}第二行", Environment.NewLine);

        // 一条记录跨多行会让按行排查变得很难受：日志库自己的换行是为了好看，
        // 不是为了承载信息。
        Assert.Single(ReadLogLines());
    }

    [Fact]
    public void 异常被附在同行里()
    {
        using var provider = new DailyFileLoggerProvider(_directory);

        try
        {
            throw new InvalidOperationException("故意的");
        }
        catch (InvalidOperationException ex)
        {
            provider.CreateLogger("QYPlayer.Tests").LogError(ex, "出错了");
        }

        var lines = ReadLogLines();

        // 异常栈同样压成一行，但仍然保留栈信息。
        Assert.Single(lines);
        Assert.Contains("ERR", lines[0], StringComparison.Ordinal);
        Assert.Contains("故意的", lines[0], StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void 低于最低级别的日志不落盘()
    {
        using var provider = new DailyFileLoggerProvider(_directory, LogLevel.Warning);

        var logger = provider.CreateLogger("QYPlayer.Tests");
        logger.LogInformation("不该出现");
        logger.LogWarning("应该出现");

        var line = Assert.Single(ReadLogLines());
        Assert.Contains("应该出现", line, StringComparison.Ordinal);
    }

    [Fact]
    public void 类别名只留最后一段()
    {
        using var provider = new DailyFileLoggerProvider(_directory);
        provider.CreateLogger("QYPlayer.Data.SqliteLibraryStore").LogInformation("x");

        var line = Assert.Single(ReadLogLines());

        // 完整命名空间会把日志行挤满，真正的信息反而看不清。
        Assert.Contains("SqliteLibraryStore", line, StringComparison.Ordinal);
        Assert.DoesNotContain("QYPlayer.Data.", line, StringComparison.Ordinal);
    }

    [Fact]
    public void 超过保留期的旧日志被删掉()
    {
        Directory.CreateDirectory(_directory);

        var expired = Path.Combine(_directory, $"qyplayer-{DateTime.Now.Date.AddDays(-30):yyyyMMdd}.log");
        var recent = Path.Combine(_directory, $"qyplayer-{DateTime.Now.Date.AddDays(-1):yyyyMMdd}.log");
        File.WriteAllText(expired, "旧");
        File.WriteAllText(recent, "新");

        using var provider = new DailyFileLoggerProvider(_directory, retentionDays: 7);

        // 只留最近 7 天，否则日志会无限堆积。
        Assert.False(File.Exists(expired));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public void 不碰目录里其他文件()
    {
        Directory.CreateDirectory(_directory);

        var userFile = Path.Combine(_directory, "我的笔记.txt");
        var similarName = Path.Combine(_directory, $"qyplayer-{DateTime.Now.Date.AddDays(-30):yyyyMMdd}.log.bak");
        File.WriteAllText(userFile, "别删我");
        File.WriteAllText(similarName, "也别删我");

        using var provider = new DailyFileLoggerProvider(_directory, retentionDays: 1);

        // 清理只认本程序生成的文件名模式：用户可能往这个目录里放别的东西，
        // 误删的代价远大于多留几个旧日志。
        Assert.True(File.Exists(userFile));
        Assert.True(File.Exists(similarName));
    }

    [Fact]
    public void 目录不可写时写日志不抛异常()
    {
        // 拿一个已存在的文件当目录：打开写入器必然失败。
        var fileAsDirectory = Path.Combine(Path.GetTempPath(), "qyplayer-logfile-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(fileAsDirectory, "x");

        try
        {
            DailyFileLoggerProvider provider;
            try
            {
                provider = new DailyFileLoggerProvider(fileAsDirectory);
            }
            catch (IOException)
            {
                // 连目录都建不出来也算通过：关键在于不能是未捕获的意外异常。
                return;
            }

            using (provider)
            {
                // 日志失败绝不能再抛：调用点遍布各处，抛出去会把正常逻辑一起带崩。
                provider.CreateLogger("QYPlayer.Tests").LogInformation("不该让调用方看到异常");
            }
        }
        finally
        {
            File.Delete(fileAsDirectory);
        }
    }

    [Fact]
    public void Dispose_之后不再写文件()
    {
        var provider = new DailyFileLoggerProvider(_directory);
        var logger = provider.CreateLogger("QYPlayer.Tests");
        logger.LogInformation("关闭前");

        provider.Dispose();
        logger.LogInformation("关闭后");

        var line = Assert.Single(ReadLogLines());
        Assert.Contains("关闭前", line, StringComparison.Ordinal);
    }

    [Fact]
    public void 重复_Dispose_是安全的()
    {
        var provider = new DailyFileLoggerProvider(_directory);

        provider.Dispose();
        provider.Dispose();
    }

    [Fact]
    public void 扩展方法能挂进日志构建器()
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddDailyFile(_directory));

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("QYPlayer.Tests")
            .LogInformation("通过扩展方法写入");

        Assert.Single(ReadLogLines());
    }
}
