using System;
using System.IO;

namespace QYPlayer.App;

/// <summary>
/// 应用的本地路径约定。所有可写数据都放在程序目录下的 data 子目录，
/// 以支持绿色免安装的发布形式：整个目录拷走即可，不需要写注册表或 AppData。
/// </summary>
/// <remarks>
/// 这些目录都在 .gitignore 中排除，属于运行时数据，不纳入版本控制。
/// </remarks>
public static class AppPaths
{
    /// <summary>程序所在目录。开发期即输出目录（bin/Debug/...）。</summary>
    public static string BaseDirectory { get; } = AppContext.BaseDirectory;

    /// <summary>运行时数据根目录。</summary>
    public static string DataDirectory { get; } = Path.Combine(BaseDirectory, "data");

    /// <summary>曲库数据库所在目录。M2 使用。</summary>
    public static string DatabaseDirectory { get; } = Path.Combine(DataDirectory, "db");

    /// <summary>封面缓存目录。</summary>
    public static string CoverCacheDirectory { get; } = Path.Combine(DataDirectory, "covers");

    /// <summary>日志目录。</summary>
    public static string LogDirectory { get; } = Path.Combine(DataDirectory, "logs");

    /// <summary>解密插件扫描目录。</summary>
    public static string PluginDirectory { get; } = Path.Combine(BaseDirectory, "plugins");

    /// <summary>
    /// libVLC 便携模式目录。目录名由 VLC 自身写死（见 QYPlayer.Audio 的 PlaybackOptions 说明），
    /// 必须叫 portable 且与可执行文件同级，用来承接 libVLC 的配置与封面缓存，
    /// 避免它往 %APPDATA%\vlc 写入。
    /// </summary>
    public static string PortableVlcDirectory { get; } = Path.Combine(BaseDirectory, "portable");

    /// <summary>
    /// 创建所有必要的目录。启动时调用一次。
    /// </summary>
    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(DatabaseDirectory);
        Directory.CreateDirectory(CoverCacheDirectory);
        Directory.CreateDirectory(LogDirectory);

        // 插件目录也一并建好，方便用户直接往里放插件。
        Directory.CreateDirectory(PluginDirectory);

        // 必须早于播放引擎初始化，否则 libVLC 会先按用户目录布局落盘。
        Directory.CreateDirectory(PortableVlcDirectory);
    }
}
