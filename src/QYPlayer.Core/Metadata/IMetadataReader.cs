using QYPlayer.Core.Models;

namespace QYPlayer.Core.Metadata;

/// <summary>
/// 音频元数据读取器。读取失败应当返回 <see cref="TrackMetadata.Empty"/> 而非抛异常，
/// 因为曲库扫描必须能容忍单个损坏文件。
/// </summary>
/// <remarks>
/// 接口定义在领域层而非实现所在的 <c>QYPlayer.Metadata</c>，与 <c>ITrackSource</c>、
/// <c>IPlaybackService</c> 同一处理方式。曲库扫描是领域能力，只应依赖抽象：
/// 若接口留在实现程序集，扫描器就得反过来依赖 TagLib 这条实现链，
/// 持久化层也会被一并拖上。
/// </remarks>
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
