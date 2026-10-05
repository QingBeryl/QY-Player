using QYPlayer.Core.Models;

namespace QYPlayer.Core.Sources;

/// <summary>
/// 曲目的数据来源。把「文件在哪」与「数据怎么读」解耦，
/// 使加密格式可以作为一个实现插入，而不影响播放链路。
/// </summary>
/// <remarks>
/// 约定：<see cref="FilePath"/> 与 <see cref="OpenRead"/> 至少有一个可用。
/// 普通文件两者都可用，优先走 <see cref="FilePath"/> 以避免额外的 IO；
/// 需要解密才能读取的格式 <see cref="FilePath"/> 为 null，只能走流。
/// </remarks>
public interface ITrackSource : IDisposable
{
    /// <summary>
    /// 引擎可以直接打开的本地文件路径。
    /// 数据需要解密时为 null，此时必须使用 <see cref="OpenRead"/>。
    /// </summary>
    string? FilePath { get; }

    /// <summary>源的音频承载格式。加密格式在此返回解密后的真实格式。</summary>
    AudioFormat Format { get; }

    /// <summary>
    /// 打开一个只读、可定位的流。
    /// 调用方负责释放返回的流，但不负责释放源本身。
    /// </summary>
    Stream OpenRead();

    /// <summary>是否需要经过解密才能播放。</summary>
    bool RequiresDecryption { get; }
}
