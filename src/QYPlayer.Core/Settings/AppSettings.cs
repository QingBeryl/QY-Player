using QYPlayer.Core.Playback;

namespace QYPlayer.Core.Settings;

/// <summary>
/// 用户偏好。整体作为一个可序列化对象存盘，见需求文档 8.1 的结论：
/// 设置与曲库分开，前者是启动早期就要用的少量数据，后者是启动后异步加载的大块数据。
/// </summary>
/// <remarks>
/// 这里只放「跨会话需要记住」的东西。曲库曲目本身走 SQLite，不混进来。
/// 所有字段都有合理默认值：设置文件不存在、损坏或来自旧版本时，
/// 缺字段不会变成 0 或 null 引发的诡异行为，而是退回默认。
/// </remarks>
public sealed class AppSettings
{
    /// <summary>首次启动时的音量。与原 <c>PlaybackOptions.InitialVolume</c> 的写死值保持一致。</summary>
    public const int DefaultVolume = 60;

    /// <summary>主音量，取值 0–100。</summary>
    public int Volume { get; set; } = DefaultVolume;

    /// <summary>是否静音。与音量分开记忆，恢复静音前的音量才有着落。</summary>
    public bool IsMuted { get; set; }

    /// <summary>主题偏好。</summary>
    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>上次使用的播放模式。</summary>
    public RepeatMode RepeatMode { get; set; } = RepeatMode.Sequential;

    /// <summary>
    /// 曲库根目录。用户选的音乐文件夹，扫描时递归遍历。
    /// 放在设置里而不是数据库里：它是用户的配置输入，不是扫描的产物。
    /// </summary>
    public List<string> LibraryFolders { get; set; } = [];

    /// <summary>上次关闭时的窗口位置与尺寸。</summary>
    public WindowPlacement Window { get; set; } = new();

    /// <summary>
    /// 左侧曲库栏是否收起。
    /// </summary>
    /// <remarks>
    /// 收起是为了把整窗宽度让给播放区（例如只放歌不看列表时），
    /// 属于「用户摆好的界面状态」，因此要跨会话记住，
    /// 否则每次启动都要再收一次。
    /// </remarks>
    public bool IsLibraryCollapsed { get; set; }

    /// <summary>
    /// 把字段修正到合法范围。反序列化之后、使用之前必须调用一次。
    /// </summary>
    /// <remarks>
    /// 设置文件是用户可以直接编辑的文本，也可能来自旧版本或被人手工改坏，
    /// 因此不能假定里面的值可信：非法枚举值、越界音量、空白路径都要在这里收口，
    /// 否则问题会在界面上以「音量滑块拖不动」「列表里有一条空路径」这类现象暴露出来，
    /// 排查时很难联想到是配置文件的问题。
    /// </remarks>
    public void Normalize()
    {
        Volume = Math.Clamp(Volume, 0, 100);

        if (!Enum.IsDefined(Theme))
        {
            Theme = AppTheme.System;
        }

        if (!Enum.IsDefined(RepeatMode))
        {
            RepeatMode = RepeatMode.Sequential;
        }

        LibraryFolders = LibraryFolders
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Window ??= new WindowPlacement();

        // 尺寸为 0 或负数说明记录无意义（例如窗口还没显示就被关闭），
        // 与其让窗口以 0 宽启动，不如当作没记忆过。
        if (!Window.HasSize)
        {
            Window.Width = null;
            Window.Height = null;
        }

        if (!Window.HasPosition)
        {
            Window.Left = null;
            Window.Top = null;
        }
    }
}
