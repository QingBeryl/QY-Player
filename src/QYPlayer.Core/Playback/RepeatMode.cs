namespace QYPlayer.Core.Playback;

/// <summary>
/// 播放模式。M1 只定义，M2.5 打通曲库后才真正生效。
/// </summary>
public enum RepeatMode
{
    /// <summary>顺序播放，播完最后一首停止。</summary>
    Sequential = 0,

    /// <summary>列表循环。</summary>
    RepeatAll,

    /// <summary>单曲循环。</summary>
    RepeatOne,

    /// <summary>随机播放。</summary>
    Shuffle,
}
