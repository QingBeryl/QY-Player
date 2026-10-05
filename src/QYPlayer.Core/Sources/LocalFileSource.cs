using QYPlayer.Core.Models;

namespace QYPlayer.Core.Sources;

/// <summary>
/// 普通未加密文件的数据源。直接暴露路径，交给引擎自己读。
/// </summary>
public sealed class LocalFileSource : ITrackSource
{
    public LocalFileSource(string filePath, AudioFormat format = AudioFormat.Unknown)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        FilePath = filePath;
        Format = format;
    }

    public string? FilePath { get; }

    public AudioFormat Format { get; }

    public bool RequiresDecryption => false;

    public Stream OpenRead() => File.OpenRead(FilePath!);

    public void Dispose()
    {
        // 无可释放的资源：文件句柄由 OpenRead 的调用方负责。
    }
}
