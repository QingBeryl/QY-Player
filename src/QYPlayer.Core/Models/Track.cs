using System.ComponentModel.DataAnnotations.Schema;

namespace QYPlayer.Core.Models;

/// <summary>
/// 曲库中的一首曲目。M1 阶段只承载播放所需的最小信息，M2 起作为数据库实体映射。
/// </summary>
/// <remarks>
/// <see cref="FileName"/>、<see cref="DisplayTitle"/>、<see cref="DisplayArtist"/>、
/// <see cref="DisplayAlbum"/> 四个是计算属性，已用 <see cref="NotMappedAttribute"/> 排除在映射之外。
/// 不加这一处的后果不是报错而是静默跑偏：EF Core 会把它们当成可写列建进表里，
/// 然后每次查询都拿数据库里的空值覆盖计算结果，列表里的回退逻辑（文件名代替标题等）全部失效。
/// </remarks>
public sealed class Track
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>磁盘上的文件绝对路径。加密格式文件的路径同样记录在此。</summary>
    public required string FilePath { get; init; }

    /// <summary>
    /// 文件字节数。与 <see cref="LastWriteTimeUtc"/> 一起用于增量比对（见需求文档 8.2）：
    /// 两者都没变就跳过重新解析，避免每次启动把整个曲库读一遍。
    /// </summary>
    public long FileSize { get; set; }

    /// <summary>文件最后写入时间（UTC）。用 UTC 而非本地时间，避免时区与夏令时带来的误判。</summary>
    public DateTime LastWriteTimeUtc { get; set; }

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
    [NotMapped]
    public string FileName => Path.GetFileName(FilePath);

    /// <summary>标签缺失时回退为文件名，避免列表出现空白行。</summary>
    [NotMapped]
    public string DisplayTitle =>
        string.IsNullOrWhiteSpace(Title) ? Path.GetFileNameWithoutExtension(FilePath) : Title;

    [NotMapped]
    public string DisplayArtist =>
        string.IsNullOrWhiteSpace(Artist) ? "未知艺术家" : Artist;

    [NotMapped]
    public string DisplayAlbum =>
        string.IsNullOrWhiteSpace(Album) ? "未知专辑" : Album;

    /// <summary>
    /// 时长的展示文本，例如 03:45；超过一小时才带上小时位。
    /// </summary>
    /// <remarks>
    /// 与引擎回传的位置文本用同一套排版（见 MainViewModel 的时间格式化），
    /// 免得列表里的时长和进度条上的时长长得不一样。
    /// 用 <see cref="TimeSpan"/> 的原生格式做不到这一点：
    /// "mm\:ss" 在超过一小时时会把分钟截断到 0-59，而 "hh\:mm\:ss" 又会让
    /// 短曲子也顶上两个前导零。
    /// </remarks>
    [NotMapped]
    public string DurationText => Duration.TotalHours >= 1
        ? $"{(int)Duration.TotalHours:00}:{Duration.Minutes:00}:{Duration.Seconds:00}"
        : $"{Duration.Minutes:00}:{Duration.Seconds:00}";
}
