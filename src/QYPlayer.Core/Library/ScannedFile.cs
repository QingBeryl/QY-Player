using QYPlayer.Core.Models;

namespace QYPlayer.Core.Library;

/// <summary>
/// 快速扫描阶段的产物：只包含「列目录就能拿到」的信息，不读文件内容。
/// </summary>
/// <param name="FilePath">文件的绝对路径。</param>
/// <param name="FileSize">文件字节数。</param>
/// <param name="LastWriteTimeUtc">最后写入时间（UTC）。</param>
/// <param name="Format">
/// 按扩展名判定的格式。这里不读文件头：格式判错的代价要在播放与解析元数据时才显现，
/// 快速阶段保持零内容读取（见需求文档 8.1）。
/// </param>
public sealed record ScannedFile(
    string FilePath,
    long FileSize,
    DateTime LastWriteTimeUtc,
    AudioFormat Format);
