using QYPlayer.Core.Models;

namespace QYPlayer.Audio;

/// <summary>
/// 播放引擎对外的构造参数。把原生库路径等宿主相关配置集中在一处，
/// 避免引擎实现里去猜目录结构。
/// </summary>
public sealed class PlaybackOptions
{
    /// <summary>
    /// libVLC 原生库所在目录。为 null 时由绑定库自行探测（依赖 RuntimeIdentifier 的默认布局）。
    /// 显式指定可以在打包后避免探测失败。
    /// </summary>
    public string? NativeLibraryDirectory { get; init; }

    /// <summary>初始音量，0–100。</summary>
    public int InitialVolume { get; init; } = 60;

    /// <summary>
    /// 是否让 libVLC 走便携模式。默认开启，这是绿色版承诺能否成立的关键开关。
    /// </summary>
    /// <remarks>
    /// libVLC 默认把配置、插件缓存与封面缓存写到 %APPDATA%\vlc，
    /// 这会破坏「整个目录拷走即可运行、不写用户目录」的发布约定。
    /// VLC 在 Windows 上内置了便携模式判定：只要可执行文件同级目录下存在名为
    /// <c>portable</c> 的文件夹，配置/数据/缓存三个目录就全部改指向它。
    /// 因此这里在初始化 libVLC 之前把这个目录建出来即可，无需改动任何注册表或环境变量。
    /// </remarks>
    public bool UsePortableMode { get; init; } = true;

    /// <summary>
    /// 便携模式目录。为 null 时取可执行文件同级的 <c>portable</c> 目录。
    /// </summary>
    /// <remarks>
    /// 名字与位置由 VLC 的判定逻辑写死（见 src/win32/dirs.c 的 config_GetAppDir），
    /// 调用方一般不需要修改，保留可覆盖只是为了便于测试。
    /// </remarks>
    public string? PortableModeDirectory { get; init; }

    /// <summary>
    /// 启动时的额外 libVLC 参数。
    /// 默认关闭视频输出与联网抓取，本地音频播放不需要它们。
    /// </summary>
    public string[]? AdditionalArguments { get; init; }
}
