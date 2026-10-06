using QYPlayer.Core.Library;

namespace QYPlayer.Tests;

/// <summary>
/// <see cref="LibraryWatcher"/> 的防抖与容错行为。
/// </summary>
/// <remarks>
/// <para>
/// 用真实的临时目录与真实写入来驱动，而不是伪造事件源：
/// 防抖的价值恰恰在于「操作系统一次保存动作会投出多条事件」这个现实，
/// 把事件源换成假的就测不到它。
/// </para>
/// <para>
/// 因此断言一律配合轮询等待，并且给足超时余量：
/// 文件系统通知是异步投递的，固定的 <c>Task.Delay</c> 在繁忙的机器上会随机失败。
/// </para>
/// </remarks>
public sealed class LibraryWatcherTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("qyplayer-watch-");

    private string WriteFile(string name, string content = "a")
    {
        var path = Path.Combine(_directory.FullName, name);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>轮询等待条件成立，超时则返回 false。</summary>
    private static async Task<bool> WaitAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;

        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(25);
        }

        return condition();
    }

    /// <summary>
    /// 短时间内连续写入多个文件时，只应在事件停下来之后通知一次。
    /// </summary>
    /// <remarks>
    /// 这是监听器存在的意义：复制一个文件夹会瞬间产生成百上千条
    /// Created / Changed，若逐条转发，扫描会被连续触发，界面一直在转圈。
    /// </remarks>
    [Fact]
    public async Task 连续变动只通知一次()
    {
        using var watcher = new LibraryWatcher(TimeSpan.FromMilliseconds(600));

        var count = 0;
        watcher.Changed += (_, _) => Interlocked.Increment(ref count);

        watcher.Watch([_directory.FullName]);

        for (var index = 0; index < 5; index++)
        {
            WriteFile($"burst-{index}.mp3");
        }

        // 等通知发出来。
        Assert.True(await WaitAsync(() => Volatile.Read(ref count) > 0), "防抖窗口结束后应当收到一次通知");

        // 再等一个完整窗口，确认这一轮没有再补发第二条。
        await Task.Delay(800);

        Assert.Equal(1, Volatile.Read(ref count));
    }

    /// <summary>一轮稳定之后再次发生变动，应当重新通知。</summary>
    [Fact]
    public async Task 静默期结束后再次变动会重新通知()
    {
        using var watcher = new LibraryWatcher(TimeSpan.FromMilliseconds(200));

        var count = 0;
        watcher.Changed += (_, _) => Interlocked.Increment(ref count);

        watcher.Watch([_directory.FullName]);

        WriteFile("first.mp3");
        Assert.True(await WaitAsync(() => Volatile.Read(ref count) >= 1));

        WriteFile("second.mp3");
        Assert.True(await WaitAsync(() => Volatile.Read(ref count) >= 2), "第二次变动应当再通知一次");
    }

    /// <summary>释放之后不再触发通知，避免回调打到已清理的订阅方。</summary>
    [Fact]
    public async Task 释放后不再通知()
    {
        var watcher = new LibraryWatcher(TimeSpan.FromMilliseconds(150));

        var count = 0;
        watcher.Changed += (_, _) => Interlocked.Increment(ref count);

        watcher.Watch([_directory.FullName]);
        watcher.Dispose();

        WriteFile("after-dispose.mp3");
        await Task.Delay(700);

        Assert.Equal(0, Volatile.Read(ref count));
    }

    /// <summary>
    /// 监听一个不存在的目录时静默跳过，不抛异常。
    /// </summary>
    /// <remarks>
    /// 用户的曲库目录可能在被拔掉的移动硬盘上；这种时候程序必须照常启动，
    /// 只是这一段暂时不被监听，等 8.2 的启动兜底在盘接回来后再补齐。
    /// </remarks>
    [Fact]
    public void 不存在的目录不会抛异常()
    {
        using var watcher = new LibraryWatcher();

        var missing = Path.Combine(_directory.FullName, "not-exist");

        watcher.Watch([missing]);

        Assert.Empty(watcher.Roots);
    }

    /// <summary>重复调用 Watch 不会累积多份监听。</summary>
    [Fact]
    public void 重复监听同一目录只保留一份()
    {
        using var watcher = new LibraryWatcher();

        watcher.Watch([_directory.FullName]);
        watcher.Watch([_directory.FullName]);

        Assert.Single(watcher.Roots);
    }

    /// <summary>重新设置监听目录时，旧目录必须被停掉。</summary>
    [Fact]
    public void 重新设置监听会替换掉旧目录()
    {
        using var watcher = new LibraryWatcher();

        var other = Directory.CreateTempSubdirectory("qyplayer-watch-other-");

        try
        {
            watcher.Watch([_directory.FullName]);
            watcher.Watch([other.FullName]);

            var root = Assert.Single(watcher.Roots);
            Assert.Equal(other.FullName, root);
        }
        finally
        {
            other.Delete(recursive: true);
        }
    }

    public void Dispose()
    {
        try
        {
            _directory.Delete(recursive: true);
        }
        catch (Exception)
        {
            // 临时目录清理失败不影响测试结论。
        }
    }
}
