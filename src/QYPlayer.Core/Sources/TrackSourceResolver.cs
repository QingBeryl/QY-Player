using QYPlayer.Core.Models;
using QYPlayer.Plugin.Abstractions;

namespace QYPlayer.Core.Sources;

/// <summary>
/// 把曲目路径解析为可播放的数据源。
/// 普通文件直接返回本地文件源；加密文件交给已启用的解密插件。
/// </summary>
/// <remarks>
/// 这是「插件化」的接入点：主程序只认识本接口与插件契约，
/// 不知道任何具体解密算法。没有安装插件时，加密文件会得到明确的失败原因，
/// 而不会影响其他文件的播放。
/// </remarks>
public sealed class TrackSourceResolver
{
    private readonly IReadOnlyList<IDecryptorPlugin> _plugins;

    public TrackSourceResolver(IEnumerable<IDecryptorPlugin>? plugins = null)
    {
        _plugins = plugins?.ToList() ?? [];
    }

    /// <summary>当前已加载且启用的解密插件数量。</summary>
    public int EnabledPluginCount => _plugins.Count;

    /// <summary>
    /// 解析数据源。调用方负责释放返回的源。
    /// </summary>
    /// <exception cref="NotSupportedException">格式不受支持，或需要插件但插件未启用。</exception>
    public ITrackSource Resolve(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var format = AudioFormatDetector.FromPath(filePath);

        if (AudioFormatDetector.IsNative(format))
        {
            return new LocalFileSource(filePath, format);
        }

        if (!AudioFormatDetector.IsEncrypted(format))
        {
            throw new NotSupportedException($"无法识别的音频格式：{Path.GetExtension(filePath)}");
        }

        var extension = AudioFormatDetector.NormalizeExtension(filePath);
        var plugin = _plugins.FirstOrDefault(p =>
            p.SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase));

        if (plugin is null)
        {
            throw new NotSupportedException(
                $"该文件是加密格式（{format}），需要安装对应的解密插件后才能播放。");
        }

        return new DecryptedTrackSource(filePath, extension, plugin);
    }
}
