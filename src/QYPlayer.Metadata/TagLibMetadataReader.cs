using QYPlayer.Core.Metadata;
using QYPlayer.Core.Models;

namespace QYPlayer.Metadata;

/// <summary>
/// 基于 TagLib# 的元数据读取器，覆盖 MP3 / FLAC / M4A / OGG / WAV / WMA 等主流格式。
/// </summary>
/// <remarks>
/// 曲库扫描会遇到残缺、损坏或非音频文件，因此这里对解析异常一律吞掉并返回
/// <see cref="TrackMetadata.Empty"/>：宁可列表里少一列信息，也不能让扫描中断。
/// 解析失败的具体原因由曲库层在 M2 记录到诊断日志。
/// </remarks>
public sealed class TagLibMetadataReader : IMetadataReader
{
    private readonly ICoverCache? _coverCache;

    public TagLibMetadataReader(ICoverCache? coverCache = null)
    {
        _coverCache = coverCache;
    }

    public Task<TrackMetadata> ReadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        // TagLib 是同步阻塞的 IO，放到线程池执行，避免拖住调用方线程。
        return Task.Run(() => Read(filePath, cancellationToken), cancellationToken);
    }

    private TrackMetadata Read(string filePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(filePath))
        {
            return TrackMetadata.Empty;
        }

        try
        {
            using var file = TagLib.File.Create(filePath);

            var tag = file.Tag;
            var properties = file.Properties;

            var coverData = ExtractCoverData(tag, out var coverMimeType);
            var coverPath = _coverCache?.Save(coverData, coverMimeType);

            return new TrackMetadata(
                Title: tag.Title ?? string.Empty,
                Artist: ResolveArtist(tag),
                Album: tag.Album ?? string.Empty,
                Duration: SafeDuration(properties),
                SampleRate: SafeInt(() => properties.AudioSampleRate),
                BitsPerSample: SafeInt(() => properties.BitsPerSample),
                Bitrate: SafeInt(() => properties.AudioBitrate),
                CoverCachePath: coverPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // 损坏文件、不支持的编码、权限不足等，均视为无元数据。
            return TrackMetadata.Empty;
        }
    }

    /// <summary>
    /// 艺术家优先取「表演者」，缺失时回退到「专辑艺术家」。
    /// 中文曲库里大量文件只填了后者的一个。
    /// </summary>
    private static string ResolveArtist(TagLib.Tag tag)
    {
        if (tag.Performers is { Length: > 0 } performers && !string.IsNullOrWhiteSpace(performers[0]))
        {
            return string.Join(" / ", performers.Where(p => !string.IsNullOrWhiteSpace(p)));
        }

        return tag.FirstAlbumArtist ?? string.Empty;
    }

    private static TimeSpan SafeDuration(TagLib.Properties properties)
    {
        try
        {
            return properties.Duration;
        }
        catch (Exception)
        {
            return TimeSpan.Zero;
        }
    }

    private static int SafeInt(Func<int> read)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static byte[]? ExtractCoverData(TagLib.Tag tag, out string? mimeType)
    {
        mimeType = null;

        try
        {
            var pictures = tag.Pictures;
            if (pictures is null || pictures.Length == 0)
            {
                return null;
            }

            // 优先取「封面」类型，其次取第一张图。
            var picture = pictures.FirstOrDefault(p => p.Type == TagLib.PictureType.FrontCover)
                          ?? pictures[0];

            mimeType = picture.MimeType;
            return picture.Data?.Data;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
