using System;
using System.IO;
using System.Security;

namespace QYPlayer.App;

/// <summary>
/// 应用的本地路径约定。
/// </summary>
/// <remarks>
/// 存在两种部署形态，两者的可写位置不同，因此这里必须先探测再决定数据落点：
///
/// 1. 便捷版（绿色）：整个目录解压到用户可写的位置，数据和 libVLC 缓存都放在
///    程序目录下，拷走目录即可，不写注册表、不写 AppData。
/// 2. 安装版：安装到 Program Files 时，普通用户对该目录没有写权限，
///    若仍往程序目录写数据，启动就会因权限失败。此时退回用户级目录
///    （%LOCALAPPDATA%\QY Player），保证任何安装位置都能正常运行。
///
/// 探测方式是实际尝试创建文件，而不是查 ACL 或路径字符串——
/// 只有真实写入才能覆盖 UAC 虚拟化、只读介质、组策略等所有情况。
/// </remarks>
public static class AppPaths
{
    /// <summary>程序所在目录。开发期即输出目录（bin/Debug/...）。</summary>
    public static string BaseDirectory { get; } = AppContext.BaseDirectory;

    /// <summary>
    /// 程序目录是否可写。决定采用绿色布局还是用户目录布局。
    /// </summary>
    public static bool IsBaseDirectoryWritable { get; } = ProbeWritable(BaseDirectory);

    /// <summary>
    /// 运行时数据根目录。程序目录可写时为程序目录，否则为用户级目录。
    /// </summary>
    public static string DataRoot { get; } = IsBaseDirectoryWritable
        ? BaseDirectory
        : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QY Player");

    /// <summary>运行时数据根目录下的 data 子目录。</summary>
    public static string DataDirectory { get; } = Path.Combine(DataRoot, "data");

    /// <summary>曲库数据库所在目录。M2 使用。</summary>
    public static string DatabaseDirectory { get; } = Path.Combine(DataDirectory, "db");

    /// <summary>
    /// 曲库数据库文件。
    /// </summary>
    /// <remarks>
    /// 只有一个库文件，因此直接把文件名写死，不再多一层「文件名从哪来」的间接。
    /// 放在 <c>data\db\</c> 下而不是 <c>data\</c> 根下，是为了让 SQLite 的
    /// WAL 模式附带产生的 <c>-wal</c> / <c>-shm</c> 两个文件留在专属目录里，
    /// 不会和数据目录下别的文件混在一起。
    /// </remarks>
    public static string LibraryDatabasePath { get; } = Path.Combine(DatabaseDirectory, "library.db");

    /// <summary>
    /// 设置文件。按 8.1 的结论存独立 JSON，与曲库解耦。
    /// </summary>
    /// <remarks>
    /// 放在 <c>data\</c> 根下而不是某个子目录：它是用户可能想直接打开看的文件，
    /// 少一层目录少一次翻找。程序目录可写时它跟着程序目录走，
    /// 用户把整个绿色版目录拷走，音量与曲库路径这些偏好也一并带走。
    /// </remarks>
    public static string SettingsFilePath { get; } = Path.Combine(DataDirectory, "settings.json");

    /// <summary>封面缓存目录。</summary>
    public static string CoverCacheDirectory { get; } = Path.Combine(DataDirectory, "covers");

    /// <summary>日志目录。</summary>
    public static string LogDirectory { get; } = Path.Combine(DataDirectory, "logs");

    /// <summary>
    /// 解密插件扫描目录。程序目录不可写时放在用户数据目录下，
    /// 否则安装到 Program Files 后用户无法放入插件。
    /// </summary>
    public static string PluginDirectory { get; } = IsBaseDirectoryWritable
        ? Path.Combine(BaseDirectory, "plugins")
        : Path.Combine(DataRoot, "plugins");

    /// <summary>
    /// libVLC 便携模式目录。目录名由 VLC 自身写死（见 QYPlayer.Audio 的 PlaybackOptions 说明），
    /// 必须叫 portable 且与可执行文件同级，用来承接 libVLC 的配置与封面缓存，
    /// 避免它往 %APPDATA%\vlc 写入。
    /// 程序目录不可写时为 null：此时无法建立便携目录，只能让 libVLC 用默认位置，
    /// 强行使用会因权限失败。
    /// </summary>
    public static string? PortableVlcDirectory { get; } = IsBaseDirectoryWritable
        ? Path.Combine(BaseDirectory, "portable")
        : null;

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
        if (PortableVlcDirectory is not null)
        {
            Directory.CreateDirectory(PortableVlcDirectory);
        }
    }

    /// <summary>
    /// 实际写一个临时文件来判断目录是否可写。
    /// 任何异常都视为不可写——探测本身不应该让程序启动失败。
    /// </summary>
    private static bool ProbeWritable(string directory)
    {
        var probe = Path.Combine(directory, $".write-probe-{Guid.NewGuid():N}");

        try
        {
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException
                                      or SecurityException
                                      or IOException
                                      or NotSupportedException)
        {
            return false;
        }
    }
}
