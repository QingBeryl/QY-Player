using QYPlayer.Core.Models;

namespace QYPlayer.Core.Playback;

/// <summary>
/// 播放引擎。只管「播放一个媒体」这件事，不负责播放列表与上一首/下一首。
/// 队列与切歌逻辑放在上层服务，这样换引擎不影响队列逻辑。
/// </summary>
public interface IPlaybackService : IDisposable
{
    PlayerState State { get; }

    /// <summary>当前加载的曲目。未加载时为 null。</summary>
    Track? CurrentTrack { get; }

    /// <summary>当前播放位置。无媒体时为 <see cref="TimeSpan.Zero"/>。</summary>
    TimeSpan Position { get; }

    /// <summary>当前媒体总时长。未知时为 <see cref="TimeSpan.Zero"/>。</summary>
    TimeSpan Duration { get; }

    /// <summary>音量，取值 0–100。</summary>
    int Volume { get; }

    /// <summary>静音状态。与音量分开，便于恢复静音前的音量。</summary>
    bool IsMuted { get; }

    event EventHandler<PlayerState>? StateChanged;

    /// <summary>当前曲目变化。切歌或停止时触发，停止时参数为 null。</summary>
    event EventHandler<Track?>? CurrentTrackChanged;

    event EventHandler<TimeSpan>? PositionChanged;

    /// <summary>时长解析完成后触发。打开媒体时同步拿不到时长，只能等引擎回调。</summary>
    event EventHandler<TimeSpan>? DurationChanged;

    /// <summary>媒体自然播放结束（非用户主动停止）。</summary>
    event EventHandler? PlaybackEnded;

    /// <summary>播放失败。参数为面向用户的失败原因。</summary>
    event EventHandler<string>? PlaybackFailed;

    Task PlayAsync(Track track, CancellationToken cancellationToken = default);

    void Pause();

    void Resume();

    /// <summary>在播放与暂停之间切换。停止状态下调用则不动作。</summary>
    void TogglePlayPause();

    void Stop();

    void Seek(TimeSpan position);

    /// <summary>设置音量。传入值会被夹到 0–100。</summary>
    void SetVolume(int volume);

    void SetMute(bool muted);
}
