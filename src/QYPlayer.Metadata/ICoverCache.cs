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

    /// <summary>
    /// 把缓存目录收敛到容量上限以内，超出时淘汰最久未使用的封面。
    /// </summary>
    /// <remarks>
    /// 按 8.2 的结论，缓存的增长用总容量上限约束。实现会在每次写入前自行腾空间，
    /// 因此这个方法不是必需的——它的用途是在「还没有写入」的时机先把上一次运行
    /// 留下的超额占用收掉，例如启动时后台跑一次。重复调用是安全的。
    /// </remarks>
    void Trim();
}
