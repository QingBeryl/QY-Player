namespace QYPlayer.Plugin.Abstractions;

/// <summary>
/// 解密插件的契约。主程序与插件都只依赖本接口，互不知道对方的实现。
/// </summary>
/// <remarks>
/// 设计约束（对应需求文档 5.4 方案 B）：
/// 插件只做「文件 → 解密流」的转换，不联网、不读取其他程序的数据库或进程内存。
/// 所有密钥必须从文件自身解析，不得依赖外部程序的状态。
/// 本程序集刻意不引用任何其他 QYPlayer 项目，以允许插件独立编译与分发。
/// </remarks>
public interface IDecryptorPlugin
{
    /// <summary>插件显示名称，例如「网易云 NCM 解密」。</summary>
    string Name { get; }

    /// <summary>插件版本号，用于在设置页展示与排错。</summary>
    string Version { get; }

    /// <summary>插件用途说明，会展示在启用确认与设置页中。</summary>
    string Description { get; }

    /// <summary>
    /// 插件能处理的扩展名，小写且带点，例如 ".ncm"。
    /// 宿主据此决定把哪些文件交给该插件。
    /// </summary>
    IReadOnlyCollection<string> SupportedExtensions { get; }

    /// <summary>
    /// 返回解密后的真实音频格式名，例如 "flac"、"mp3"。
    /// 宿主据此决定用哪种方式解码。
    /// </summary>
    /// <param name="extension">源文件的扩展名，小写且带点。</param>
    string GetDecryptedFormatName(string extension);

    /// <summary>
    /// 打开解密后的只读流。实现应当按需解密，不要把整个文件读进内存。
    /// </summary>
    /// <param name="filePath">加密文件的绝对路径。</param>
    /// <param name="extension">源文件的扩展名，小写且带点。</param>
    /// <exception cref="InvalidDataException">文件不是预期的加密格式，或文件已损坏。</exception>
    Stream OpenDecryptedStream(string filePath, string extension);
}
