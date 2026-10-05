using QYPlayer.Core.Models;

namespace QYPlayer.Metadata;

/// <summary>
/// 音频元数据读取器。读取失败应当返回 <see cref="TrackMetadata.Empty"/> 而非抛异常，
/// 因为曲库扫描必须能容忍单个损坏文件。
/// </summary>
public interface IMetadataReader
{
    /// <summary>
    /// 读取指定文件的元数据与封面。
    /// </summary>
    /// <param name="filePath">音频文件的绝对路径。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>解析出的元数据；无法解析时返回 <see cref="TrackMetadata.Empty"/>。</returns>
    Task<TrackMetadata> ReadAsync(string filePath, CancellationToken cancellationToken = default);
}
