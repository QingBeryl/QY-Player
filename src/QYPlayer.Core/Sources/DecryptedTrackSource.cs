using QYPlayer.Core.Models;
using QYPlayer.Plugin.Abstractions;

namespace QYPlayer.Core.Sources;

/// <summary>
/// 加密文件的数据源。内部委托给解密插件，对外表现为一个普通的音频流。
/// </summary>
/// <remarks>
/// 为了保护内存，这里不做整文件预解密：<see cref="OpenRead"/> 返回的流由插件按需解密。
/// 大文件（无损整轨可达 1 GB）因此不会一次性占用等量内存。
/// </remarks>
public sealed class DecryptedTrackSource : ITrackSource
{
    private readonly string _filePath;
    private readonly string _extension;
    private readonly IDecryptorPlugin _plugin;

    public DecryptedTrackSource(string filePath, string extension, IDecryptorPlugin plugin)
    {
        _filePath = filePath;
        _extension = extension;
        _plugin = plugin;
    }

    /// <summary>
    /// 加密文件需要经过解密，引擎无法直接打开，因此没有可用路径。
    /// </summary>
    public string? FilePath => null;

    /// <summary>
    /// 解密后的真实格式。由插件根据文件内容与扩展名判定。
    /// </summary>
    public AudioFormat Format =>
        AudioFormatDetector.FromName(_plugin.GetDecryptedFormatName(_extension));

    public bool RequiresDecryption => true;

    public Stream OpenRead() => _plugin.OpenDecryptedStream(_filePath, _extension);

    public void Dispose()
    {
        // 插件实例由插件宿主管理生命周期，此处不释放。
    }
}
