namespace QYPlayer.Core.Playback;

/// <summary>
/// 播放引擎的状态。与 UI 无关，UI 只订阅它的变化。
/// </summary>
public enum PlayerState
{
    /// <summary>没有加载任何媒体，或已停止。</summary>
    Stopped = 0,

    /// <summary>正在播放。</summary>
    Playing,

    /// <summary>已暂停，可以继续。</summary>
    Paused,

    /// <summary>正在加载或缓冲，尚未出声。</summary>
    Opening,

    /// <summary>播放出错。引擎可用，只是当前媒体打不开。</summary>
    Error,
}
