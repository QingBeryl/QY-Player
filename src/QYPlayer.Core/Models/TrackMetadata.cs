namespace QYPlayer.Core.Models;

/// <summary>
/// 从音频文件解析出的元数据。与 <see cref="Track"/> 分开，是因为解析层不应该知道曲库实体的存在。
/// </summary>
/// <param name="Title">标题。标签缺失时为空字符串。</param>
/// <param name="Artist">艺术家。标签缺失时为空字符串。</param>
/// <param name="Album">专辑。标签缺失时为空字符串。</param>
/// <param name="Duration">时长。未知时为 <see cref="TimeSpan.Zero"/>。</param>
/// <param name="SampleRate">采样率（Hz）。未知时为 0。</param>
/// <param name="BitsPerSample">位深。未知时为 0。</param>
/// <param name="Bitrate">比特率（kbps）。未知时为 0。</param>
/// <param name="CoverCachePath">
/// 封面在本机缓存目录中的路径。无封面或未配置缓存时为 null。
/// 返回路径而非原始字节，是为了让曲库只持久化一个短字符串，
/// 避免把每张封面都复制进数据库。
/// </param>
public sealed record TrackMetadata(
    string Title,
    string Artist,
    string Album,
    TimeSpan Duration,
    int SampleRate,
    int BitsPerSample,
    int Bitrate,
    string? CoverCachePath)
{
    public static TrackMetadata Empty { get; } =
        new(string.Empty, string.Empty, string.Empty, TimeSpan.Zero, 0, 0, 0, null);
}
