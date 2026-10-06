using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
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

        // 单击进度条跳转必须在「按下」这一刻就提交，不能等到「抬起」。
        //
        // 原因：点下进度条后，Slider 会把 Value 设成点击处的位置，绑定随即写入
        // PositionSeconds；但引擎的位置回传（约每 250ms 一次）也在往 UI 线程投递，
        // 若其中一条「跳转生效前」的旧位置在按下与抬起之间被处理，它会把
        // PositionSeconds 覆盖回原处，抬起时提交的就成了旧位置——表现就是
        // 「点了没反应」。按住的时间越长越容易踩中，这正是「时而不灵」的来源。
        // 改成按下即提交，这个竞态窗口就不存在了。
        //
        // 之所以要用 AddHandler 而不是在 XAML 上写 PreviewMouseLeftButtonDown：
        // Slider 在 IsMoveToPointEnabled 生效时会自行把该事件置为 Handled，
        // XAML 挂上去的处理器（handledEventsToo 默认 false）会被直接跳过。
        // 传 true 才能收到，而此时 Slider 已经算好了新位置，正好接着提交。
        SeekSlider.AddHandler(
            PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(OnSeekMouseLeftButtonDown),
            handledEventsToo: true);
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
    /// 在进度条上按下鼠标时立即提交跳转。
    /// </summary>
    /// <remarks>
    /// 由构造函数用 AddHandler(handledEventsToo: true) 注册，理由见那里的注释。
    /// 本处理器在 Slider 的类处理器之后运行，因此触发时新位置已经写好，
    /// 这里接着把它交给引擎即可。
    ///
    /// 读 <see cref="Slider.Value"/> 而不是 ViewModel 的 PositionSeconds：
    /// 两者之间隔着一层绑定，写值与读值并非同一时刻完成，直接取控件当前值最可靠。
    /// </remarks>
    private void OnSeekMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is not { } viewModel || viewModel.IsSeekDragging)
        {
            return;
        }

        // 按在滑块圆点上时 Slider 不走 move-to-point 分支，位置没有变，
        // 这一按是拖动的起点，交给 DragStarted → DragCompleted 收尾即可。
        // 若这里也提交一次，会与随后的拖动叠加出一次多余的跳转。
        if (IsFromThumb(e.OriginalSource))
        {
            return;
        }

        viewModel.PositionSeconds = SeekSlider.Value;
        viewModel.CommitSeekCommand.Execute(null);
    }

    /// <summary>
    /// 判断鼠标是否按在滑块圆点（Thumb）上。
    /// </summary>
    /// <remarks>
    /// 沿视觉树从事件源往上找，遇到 Thumb 即命中，遇到 Slider 说明已经走到头。
    /// 之所以从事件源而非控件属性判断：Thumb 藏在模板内部，
    /// Slider 的 Track / Thumb 属性对外不可见，取不到。
    /// </remarks>
    private static bool IsFromThumb(object originalSource)
    {
        var node = originalSource as DependencyObject;

        while (node is not null)
        {
            if (node is Thumb)
            {
                return true;
            }

            if (node is Slider)
            {
                return false;
            }

            // 只有 Visual 才有父级可走；其它类型（如文档内的 Run）到此为止。
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : null;
        }

        return false;
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
