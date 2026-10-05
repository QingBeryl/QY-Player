namespace QYPlayer.Core.Models;

/// <summary>
/// 曲库中的一首曲目。M1 阶段只承载播放所需的最小信息，M2 接入数据库后作为实体映射。
/// </summary>
public sealed class Track
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>磁盘上的文件绝对路径。加密格式文件的路径同样记录在此。</summary>
    public required string FilePath { get; init; }

    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;

    public TimeSpan Duration { get; set; }

    public AudioFormat Format { get; set; } = AudioFormat.Unknown;

    /// <summary>采样率（Hz）。未知时为 0。</summary>
    public int SampleRate { get; set; }

    /// <summary>位深。未知时为 0。</summary>
    public int BitsPerSample { get; set; }

    /// <summary>比特率（kbps）。未知时为 0。</summary>
    public int Bitrate { get; set; }

    /// <summary>封面缓存文件的路径。未提取封面时为 null。</summary>
    public string? CoverCachePath { get; set; }

    /// <summary>文件名（含扩展名），用于展示与排序。</summary>
    public string FileName => Path.GetFileName(FilePath);

    /// <summary>标签缺失时回退为文件名，避免列表出现空白行。</summary>
    public string DisplayTitle =>
        string.IsNullOrWhiteSpace(Title) ? Path.GetFileNameWithoutExtension(FilePath) : Title;

    public string DisplayArtist =>
        string.IsNullOrWhiteSpace(Artist) ? "未知艺术家" : Artist;

    public string DisplayAlbum =>
        string.IsNullOrWhiteSpace(Album) ? "未知专辑" : Album;
}
