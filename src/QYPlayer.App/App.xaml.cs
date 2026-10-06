using System.IO;
using System.Windows;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QYPlayer.App.ViewModels;
using QYPlayer.Audio;
using QYPlayer.Core.Playback;
using QYPlayer.Core.Plugins;
using QYPlayer.Core.Sources;
using QYPlayer.Metadata;
using Wpf.Ui.Appearance;

namespace QYPlayer.App;

/// <summary>
/// 应用入口。负责搭建依赖注入宿主、初始化日志与全局异常兜底，
/// 并把主窗口的 ViewModel 注册进容器。
/// </summary>
/// <remarks>
/// 播放引擎是重资源（libVLC 实例 + 原生库），注册为单例；
/// libVLC 的初始化失败会直接终止启动，因为此时应用没有任何可用能力。
/// </remarks>
public partial class App : Application
{
    private IHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppPaths.EnsureCreated();

        try
        {
            _host = BuildHost();
            _host.Start();
        }
        catch (Exception ex)
        {
            // 走到这里通常是 libVLC 原生库缺失或架构不匹配。
            // 给出可操作的提示，而不是让进程无声退出。
            ShowFatalError(ex);
            Shutdown(1);
            return;
        }

        AttachGlobalExceptionHandlers();

        // 启动时先按系统主题刷新一次字典。
        // 不能只依赖窗口里的 SystemThemeWatcher.Watch：那个调用发生在 Loaded 之后，
        // 此时窗口已处于已加载状态，其内部的「首次注册才应用系统主题」分支不会走到，
        // 结果就是 App.xaml 里的初始主题（Dark）一直生效，不跟随系统。
        ApplicationThemeManager.ApplySystemTheme();

        var mainWindow = new MainWindow
        {
            DataContext = _host.Services.GetRequiredService<MainViewModel>(),
        };

        MainWindow = mainWindow;
        mainWindow.Show();
    }

    private static IHost BuildHost()
    {
        var builder = Host.CreateApplicationBuilder();

        builder.Logging.ClearProviders();
        builder.Logging.AddDebug();

        // 插件宿主：M1 阶段解密插件目录为空，因此不会加载任何解密能力。
        builder.Services.AddSingleton<IPluginHost>(_ =>
        {
            var host = new PluginHost(AppPaths.PluginDirectory);
            host.LoadPlugins();
            return host;
        });

        builder.Services.AddSingleton<ICoverCache>(_ => new CoverCache(AppPaths.CoverCacheDirectory));
        builder.Services.AddSingleton<IMetadataReader>(sp =>
            new TagLibMetadataReader(sp.GetService<ICoverCache>()));

        builder.Services.AddSingleton<TrackSourceResolver>(sp =>
            new TrackSourceResolver(sp.GetRequiredService<IPluginHost>().LoadedPlugins));

        builder.Services.AddSingleton<PlaybackOptions>(_ => new PlaybackOptions
        {
            // 显式指向视频库的原生目录，避免打包后探测失败。
            NativeLibraryDirectory = Path.Combine(AppPaths.BaseDirectory, "libvlc", "win-x64"),

            // 安装到 Program Files 时程序目录不可写，无法建立 portable 目录，
            // 此时只能让 libVLC 用它的默认位置，否则初始化会因权限失败。
            UsePortableMode = AppPaths.PortableVlcDirectory is not null,
            PortableModeDirectory = AppPaths.PortableVlcDirectory,
        });

        builder.Services.AddSingleton<IPlaybackService, VlcPlaybackService>();

        builder.Services.AddSingleton<MainViewModel>();

        return builder.Build();
    }

    /// <summary>
    /// 兜底处理未捕获异常。播放失败已经在上层转成可见提示，
    /// 这里主要防止后台线程的异常直接终止进程。
    /// </summary>
    private void AttachGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            LogFatal(args.Exception);
            MessageBox.Show(
                $"发生未预期的错误：\n\n{args.Exception.Message}",
                "QY Player",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogFatal(args.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogFatal(args.Exception);
            args.SetObserved();
        };
    }

    private void LogFatal(Exception? exception)
    {
        if (exception is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(AppPaths.LogDirectory);
            var path = Path.Combine(
                AppPaths.LogDirectory,
                $"crash-{DateTime.Now:yyyyMMdd}.log");

            File.AppendAllText(
                path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // 记录日志本身失败时不再做任何事，避免二次异常。
        }
    }

    private static void ShowFatalError(Exception ex)
    {
        var hint = ex switch
        {
            DllNotFoundException or TypeInitializationException =>
                "libVLC 原生库加载失败。请确认输出目录下存在 libvlc\\win-x64，"
                + "且程序以 x64 运行。",
            _ => "应用初始化失败。",
        };

        MessageBox.Show(
            $"{hint}\n\n详细信息：\n{ex.Message}",
            "QY Player 启动失败",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            // 宿主释放会级联释放播放引擎，进而释放 libVLC。
            _host.StopAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            _host.Dispose();
        }

        base.OnExit(e);
    }
}
