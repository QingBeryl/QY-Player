using QYPlayer.Core.Models;

namespace QYPlayer.Metadata;

/// <summary>
/// 封面写入本地缓存目录，避免每次渲染列表都去解析音频文件。
/// </summary>
public interface ICoverCache
{
    /// <summary>缓存目录的绝对路径。</summary>
    string CacheDirectory { get; }

    /// <summary>
    /// 保存封面并返回缓存文件的绝对路径。
    /// 内容相同的封面复用同一个文件。
    /// </summary>
    /// <param name="data">封面原始字节。</param>
    /// <param name="mimeType">封面 MIME 类型，可为 null。无法判断时按 JPEG 处理。</param>
    string? Save(byte[]? data, string? mimeType);
}
