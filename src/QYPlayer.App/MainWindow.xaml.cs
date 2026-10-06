using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using QYPlayer.App.ViewModels;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace QYPlayer.App;

/// <summary>
/// 主窗口。除了把界面事件转交给 ViewModel，还承担两件属于「视图层」的职责：
/// 拖放文件入窗，以及在拖动进度条期间抑制播放位置回写。
/// </summary>
/// <remarks>
/// 这里刻意不写业务逻辑：凡是能放进 ViewModel 的都放进去，
/// 代码隐藏只保留必须有窗口引用才能做的事。
/// </remarks>
public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    /// <summary>
    /// 窗口显示后把焦点交给窗口本身，否则空格快捷键会被初始聚焦的按钮吃掉。
    /// </summary>
    /// <remarks>
    /// 同时开启系统主题跟随：系统在深色与浅色之间切换时，
    /// WPF UI 会重刷主题字典与窗口材质，界面无需自己监听。
    /// </remarks>
    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        Focus();

        // 必须在窗口 Loaded 之后调用，否则取不到窗口句柄。
        // 传入的材质与 XAML 上的 WindowBackdropType 保持一致。
        SystemThemeWatcher.Watch(this, WindowBackdropType.Mica);
    }

    /// <summary>
    /// 窗口关闭前解除主题监听，避免钩子悬挂在已销毁的窗口上。
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        if (IsLoaded)
        {
            SystemThemeWatcher.UnWatch(this);
        }

        base.OnClosed(e);
    }

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetDroppedFile(e) is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    private async void OnWindowDrop(object sender, DragEventArgs e)
    {
        var filePath = TryGetDroppedFile(e);
        if (filePath is null || ViewModel is not { } viewModel)
        {
            return;
        }

        await viewModel.LoadAndPlayAsync(filePath);
    }

    /// <summary>
    /// 拖动开始时打上标记，让 ViewModel 忽略引擎回传的中间位置，
    /// 否则播放进度会不断把滑块拽回原处，手感明显发飘。
    /// </summary>
    private void OnSeekDragStarted(object sender, DragStartedEventArgs e)
    {
        if (ViewModel is { } viewModel)
        {
            viewModel.IsSeekDragging = true;
        }
    }

    private void OnSeekDragCompleted(object sender, DragCompletedEventArgs e)
    {
        // 松手后把最终值提交给引擎。绑定已经把值写进 PositionSeconds，
        // 因此这里直接调用命令即可。
        ViewModel?.CommitSeekCommand.Execute(null);
    }

    /// <summary>
    /// 取拖放进来的第一个音频文件。只接受单个文件，多选时以第一个为准。
    /// </summary>
    private static string? TryGetDroppedFile(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return null;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths)
        {
            return null;
        }

        var path = paths[0];
        return File.Exists(path) ? path : null;
    }
}
