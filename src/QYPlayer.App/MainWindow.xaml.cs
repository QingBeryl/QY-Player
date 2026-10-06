using System.ComponentModel;
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
    /// 窗口关闭过程中解除主题监听，避免钩子悬挂在已销毁的窗口上。
    /// </summary>
    /// <remarks>
    /// 必须是 OnClosing 而不是 OnClosed。窗口的关闭顺序是
    /// Closing → 销毁 HWND → Closed，而 OnClosed 是由 WmDestroy 触发的，
    /// 此时窗口句柄已经没了，SystemThemeWatcher.UnWatch 内部取句柄会失败，
    /// 抛出 "Could not get window handle." 并在退出时弹出错误框。
    /// 放到 OnClosing 里，句柄尚在，解除钩子才能正常完成。
    /// </remarks>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (IsLoaded)
        {
            SystemThemeWatcher.UnWatch(this);
        }

        base.OnClosing(e);
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
    /// 单击进度条后把位置提交给引擎。
    /// </summary>
    /// <remarks>
    /// 为什么需要这一条：进度条开了 IsMoveToPointEnabled，单击轨道时
    /// Slider 会在 PreviewMouseLeftButtonDown 里算好新值并置 Handled = true，
    /// Thumb 的拖拽因此根本不会启动，Thumb.DragCompleted 也就不会触发。
    /// 只靠 DragCompleted 提交的话，新值只停在绑定层，引擎下一帧回传的
    /// 播放位置又会把它拽回去，表现就是「点了没反应，要双击双击才行」。
    ///
    /// 用 Preview 而非冒泡：隧道事件先于 Thumb 的 DragCompleted 到达，
    /// 于是拖动滑块松手时这里仍能看到 IsSeekDragging 为 true，
    /// 可以干净地让给 OnSeekDragCompleted 收尾，避免同一位置提交两次。
    /// </remarks>
    private void OnSeekClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is not { } viewModel || viewModel.IsSeekDragging)
        {
            return;
        }

        viewModel.CommitSeekCommand.Execute(null);
    }

    /// <summary>
    /// 用键盘微调进度（方向键、PageUp / PageDown、Home / End）之后提交给引擎。
    /// </summary>
    /// <remarks>
    /// 这些按键只改 Slider 的值，不产生拖拽，同样需要一条显式提交的路径，
    /// 否则按完键位置会被引擎回传的进度覆盖掉。
    /// 只认会改变值的按键：空格之类落在进度条上时不该触发跳转。
    /// </remarks>
    private void OnSeekKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down
            or Key.PageUp or Key.PageDown or Key.Home or Key.End))
        {
            return;
        }

        if (ViewModel is { } viewModel)
        {
            viewModel.CommitSeekCommand.Execute(null);
        }
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
