using System.IO;

namespace QYPlayer.Core.Library;

/// <summary>
/// 曲库目录的变动监听，对应需求文档 8.2 的「监听负责运行期」。
/// </summary>
/// <remarks>
/// <para>
/// 用 <see cref="FileSystemWatcher"/> 而不是轮询：轮询一万首歌的目录树很贵，
/// 而运行期只需要知道「刚刚有东西变了」，具体变了什么交给重扫去判断——
/// 因此本类只负责合并事件并发出一次通知，不做任何增量计算。
/// </para>
/// <para>
/// <b>为什么必须防抖：</b>复制一个文件夹会瞬间产生成百上千条 Created/Changed，
/// 若每条都触发一次重扫，界面会被连续的扫描淹没。因此这里用一个可重置的定时器，
/// 事件停下来若干毫秒后才通知一次。
/// </para>
/// <para>
/// <b>监听器本身不可靠：</b>缓冲区溢出、网络盘断开、目录被删都会让监听悄悄失效，
/// 所以监听只是「即时反应」的优化，启动时的增量比对才是兜底（8.2 的结论）。
/// 这里的实现因此把所有异常都吞掉并降级为「不再通知」，绝不因为监听失败而影响曲库。
/// </para>
/// </remarks>
public sealed class LibraryWatcher : IDisposable
{
    /// <summary>
    /// 事件静默多久之后才算「稳定下来」。
    /// </summary>
    /// <remarks>
    /// 1.5 秒是权衡后的取值：太短会在复制大文件夹时反复触发重扫，
    /// 太长会让用户觉得「删掉文件后列表半天才更新」。
    /// </remarks>
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromSeconds(1.5);

    private readonly TimeSpan _debounce;
    private readonly Lock _gate = new();
    private readonly List<FileSystemWatcher> _watchers = [];

    private Timer? _timer;
    private bool _disposed;

    public LibraryWatcher(TimeSpan? debounce = null) =>
        _debounce = debounce ?? DefaultDebounce;

    /// <summary>
    /// 曲库可能已发生变化。在防抖窗口结束后于线程池线程上触发，
    /// 订阅方需要自行切回 UI 线程。
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>当前正在监听的目录。</summary>
    public IReadOnlyList<string> Roots
    {
        get
        {
            lock (_gate)
            {
                return _watchers.Select(watcher => watcher.Path).ToList();
            }
        }
    }

    /// <summary>
    /// 重新设置要监听的目录。重复调用会先停掉旧的监听。
    /// </summary>
    /// <remarks>
    /// 不存在的目录会被跳过：用户可能删掉了整个音乐文件夹，
    /// 此时应当安静地不监听，而不是抛异常打断启动。
    /// </remarks>
    public void Watch(IEnumerable<string> directories)
    {
        ArgumentNullException.ThrowIfNull(directories);
        ObjectDisposedException.ThrowIf(_disposed, this);

        StopWatching();

        lock (_gate)
        {
            foreach (var directory in directories)
            {
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                {
                    continue;
                }

                var watcher = TryCreateWatcher(directory);
                if (watcher is not null)
                {
                    _watchers.Add(watcher);
                }
            }
        }
    }

    private FileSystemWatcher? TryCreateWatcher(string directory)
    {
        try
        {
            var watcher = new FileSystemWatcher(directory)
            {
                // 递归：曲库是整棵目录树，只盯根目录会漏掉子目录里的改动。
                IncludeSubdirectories = true,

                // 只认文件名与最后写入时间：曲库只关心「哪些文件在、内容变没变」，
                // 权限、属性、大小的变化都不影响要不要重新解析
                //（大小变化一定伴随最后写入时间变化，见 8.2 的比对口径）。
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            };

            // 文件重命名会同时产生 Renamed 与 Created/Deleted 中的一条，
            // 这里统一只订阅「变了」这一件事，避免同一动作触发两次重扫。
            watcher.Created += OnFileSystemChanged;
            watcher.Changed += OnFileSystemChanged;
            watcher.Deleted += OnFileSystemChanged;
            watcher.Renamed += OnFileSystemChanged;

            // 缓冲区溢出意味着有事件被丢弃，曲库可能已经与磁盘不一致，
            // 此时最稳妥的做法是当作「发生了变化」并交由重扫兜底。
            watcher.Error += OnWatcherError;

            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception ex) when (ex is ArgumentException
                                      or IOException
                                      or UnauthorizedAccessException
                                      or PlatformNotSupportedException)
        {
            // 目录不可访问或路径过长时放弃这个目录，其余目录继续监听。
            return null;
        }
    }

    private void OnFileSystemChanged(object sender, FileSystemEventArgs e) => ScheduleNotify();

    private void OnWatcherError(object sender, ErrorEventArgs e) => ScheduleNotify();

    /// <summary>
    /// 重置防抖窗口。每次事件都把定时器往后推，
    /// 因此只有在事件真正停下来之后才会通知一次。
    /// </summary>
    private void ScheduleNotify()
    {
        if (_disposed)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _timer ??= new Timer(_ => RaiseChanged(), null, Timeout.Infinite, Timeout.Infinite);

            // Change 会把到期时间重新算起，等于「再等一个窗口」。
            _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void RaiseChanged()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception)
        {
            // 订阅方的异常不应把定时器线程带崩。
        }
    }

    private void StopWatching()
    {
        lock (_gate)
        {
            foreach (var watcher in _watchers)
            {
                try
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Dispose();
                }
                catch (Exception)
                {
                    // 释放失败没有补救手段，忽略即可。
                }
            }

            _watchers.Clear();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_gate)
        {
            _disposed = true;

            _timer?.Dispose();
            _timer = null;
        }

        StopWatching();
    }
}
