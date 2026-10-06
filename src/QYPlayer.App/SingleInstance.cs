using System.IO;
using System.IO.Pipes;
using System.Text;

namespace QYPlayer.App;

/// <summary>
/// 单实例闸门与命令行转发。
/// </summary>
/// <remarks>
/// <para>
/// 存在的理由：Windows 外壳在「右键 → 打开方式」「双击文件」「一次选中多个文件」
/// 这几种操作下，可能为每一个文件各启动一个进程。不做合并的话，
/// 用户一次打开十首歌就会看到十个窗口，而且只有最后一个在响。
/// </para>
/// <para>
/// 做法是经典的两段式：先用命名互斥体判断自己是不是第一个实例；
/// 不是的话，就把命令行里的路径通过命名管道交给已经在跑的那个实例，然后自己退出。
/// 第一个实例在后台常驻一个管道监听，收到路径就交给主窗口去播放。
/// </para>
/// <para>
/// 管道与互斥体都限定在本机、当前用户、当前登录会话内（<c>Local\</c> 前缀），
/// 因此不需要额外的访问控制，也不会被别的会话里的同名对象干扰。
/// </para>
/// <para>
/// 名字里的 GUID 取自安装脚本里的 <c>AppId</c>：它终身不变，
/// 改名或换目录都不会导致旧实例与新实例互相看不见。
/// </para>
/// </remarks>
internal sealed class SingleInstance : IDisposable
{
    /// <summary>互斥体名。<c>Local\</c> 前缀把作用域限定在登录会话内。</summary>
    private const string MutexName = @"Local\QYPlayer.SingleInstance.7C4A1E92";

    /// <summary>管道名。与互斥体同源，便于在调试时辨认。</summary>
    private const string PipeName = "QYPlayer.SingleInstance.7C4A1E92";

    /// <summary>
    /// 第二个实例等待第一个实例开始监听的最长时间。
    /// </summary>
    /// <remarks>
    /// 第一个实例启动时要先搭宿主、初始化 libVLC，耗时以秒计。
    /// 用户在这期间就从资源管理器里点开一个文件的话，
    /// 监听还没起来，因此客户端要给一点耐心，必要时由调用方重试。
    /// </remarks>
    private static readonly TimeSpan ForwardTimeout = TimeSpan.FromSeconds(3);

    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cancellation = new();

    private bool _ownsMutex;

    public SingleInstance()
    {
        _mutex = new Mutex(initiallyOwned: false, MutexName);

        try
        {
            _ownsMutex = _mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            // 上一个实例是被强杀的（任务管理器结束进程、崩溃退出）。
            // 此时互斥体归本进程所有，可以照常当第一个实例用。
            _ownsMutex = true;
        }
        catch (UnauthorizedAccessException)
        {
            // 会话里已有同名互斥体但当前用户打不开它。
            // 极少见，退化为「不转发、各自独立运行」——比启动失败好。
            _ownsMutex = false;
        }
    }

    /// <summary>本进程是不是第一个实例。为 false 时应把参数转发出去并立即退出。</summary>
    public bool IsFirstInstance => _ownsMutex;

    /// <summary>
    /// 开始监听后续实例转发过来的路径。
    /// </summary>
    /// <param name="onPathsReceived">
    /// 收到路径时调用。回调发生在后台线程，调用方需自行切回 UI 线程。
    /// </param>
    public void StartListening(Action<IReadOnlyList<string>> onPathsReceived)
    {
        ArgumentNullException.ThrowIfNull(onPathsReceived);

        if (!_ownsMutex)
        {
            return;
        }

        _ = Task.Run(() => ListenAsync(onPathsReceived, _cancellation.Token));
    }

    /// <summary>
    /// 把本次启动的命令行路径交给已经在跑的那个实例。
    /// </summary>
    /// <remarks>
    /// 失败一律吞掉：转发不成就意味着这一次「打开」没生效，
    /// 但让第二个实例再弹一个错误框只会更吵，不如静默。
    /// </remarks>
    public void ForwardToRunningInstance(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var payload = paths.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();

        if (payload.Length == 0)
        {
            return;
        }

        try
        {
            using var client = new NamedPipeClientStream(
                serverName: ".",
                PipeName,
                PipeDirection.Out,
                PipeOptions.None);

            client.Connect((int)ForwardTimeout.TotalMilliseconds);

            // 先写的先播：第一个实例按读到顺序展开，因此顺序就是用户点选的顺序。
            using var writer = new StreamWriter(client, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true,
            };

            foreach (var path in payload)
            {
                writer.WriteLine(path);
            }
        }
        catch (Exception)
        {
            // 第一个实例可能尚未开始监听（正在启动），也可能刚好退出了。
        }
    }

    private async Task ListenAsync(Action<IReadOnlyList<string>> onPathsReceived, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // 每轮新建一个服务端：一次连接对应一个第二实例的转发。
                // 限制为单个实例，避免同会话里出现两个互斥体都不持有的进程时互相抢连接。
                await using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(cancellationToken);

                using var reader = new StreamReader(server, Encoding.UTF8);

                var paths = new List<string>();

                while (await reader.ReadLineAsync(cancellationToken) is { } line)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        paths.Add(line);
                    }
                }

                if (paths.Count > 0)
                {
                    onPathsReceived(paths);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // 连接中途被对方掐断等。短暂退避后继续等下一个连接，
                // 不能立刻重试，否则在持续出错时会变成忙等。
                try
                {
                    await Task.Delay(100, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    public void Dispose()
    {
        try
        {
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已释放过，忽略。
        }

        if (_ownsMutex)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // 释放mutex要求在同一线程上；若不在（例如异常退出路径），
                // 忽略即可，进程结束时系统自会回收。
            }

            _ownsMutex = false;
        }

        _mutex.Dispose();
        _cancellation.Dispose();
    }
}
