using System.Diagnostics;
using System.IO;
using LibVLCSharp.Shared;
using Microsoft.Extensions.Logging;
using QYPlayer.Core.Models;
using QYPlayer.Core.Playback;
using QYPlayer.Core.Sources;

namespace QYPlayer.Audio;

/// <summary>
/// 基于 libVLC 的播放引擎实现。
/// </summary>
/// <remarks>
/// 为什么用 libVLC 而不是 WPF 自带的 MediaPlayer：
/// libVLC 支持的内存流与格式覆盖面都更广，且同一引擎可以承担后期的视频播放，
/// 避免 v3 阶段更换引擎。原生库的打包与加载是这类绑定最主要的故障来源，
/// 因此初始化失败时给出的是可诊断的具体原因，而不是笼统的失败。
///
/// 线程约定：libVLC 的事件回调来自其内部线程，本类在所有回调中只更新字段并转发事件，
/// 不做任何 UI 操作。UI 线程的切换由订阅方负责。
/// </remarks>
public sealed class VlcPlaybackService : IPlaybackService
{
    private readonly ILogger<VlcPlaybackService>? _logger;
    private readonly TrackSourceResolver _sourceResolver;
    private readonly PlaybackOptions _options;

    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _mediaPlayer;

    private ITrackSource? _currentSource;
    private Track? _currentTrack;
    private PlayerState _state = PlayerState.Stopped;
    private int _volume;
    private bool _isMuted;
    private bool _disposed;

    /// <summary>防止同一时刻有多个打开操作在途。</summary>
    private readonly SemaphoreSlim _openGate = new(1, 1);

    public VlcPlaybackService(
        TrackSourceResolver sourceResolver,
        PlaybackOptions? options = null,
        ILogger<VlcPlaybackService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(sourceResolver);

        _sourceResolver = sourceResolver;
        _options = options ?? new PlaybackOptions();
        _logger = logger;

        // 便携模式必须在 Core.Initialize 之前准备好，否则 libVLC 初始化时
        // 已经按 %APPDATA%\vlc 建好了缓存目录，之后再改就晚了。
        EnsurePortableDirectory();

        // 必须写全程限定名：本文件位于 QYPlayer 命名空间下，而 QYPlayer.Core 同时存在，
        // 只写 Core 会被解析成那个命名空间而不是 LibVLCSharp 的 Core 类型。
        LibVLCSharp.Shared.Core.Initialize(_options.NativeLibraryDirectory);

        var vlcArguments = _options.AdditionalArguments ??
        [
            // 本地音频播放不需要视频输出，显式关掉可减少初始化开销与侧录问题。
            "--no-video",
            // 即便已经切到便携目录，也不希望为了补全封面/艺人信息去联网，
            // 这是隐私与离线可用两方面的要求。
            "--no-metadata-network-access",
            "--quiet",
        ];

        _libVlc = new LibVLC(vlcArguments);
        _mediaPlayer = new MediaPlayer(_libVlc);

        _volume = Math.Clamp(_options.InitialVolume, 0, 100);
        _mediaPlayer.Volume = _volume;

        WireEvents();
    }

    public PlayerState State => _state;

    public Track? CurrentTrack => _currentTrack;

    public TimeSpan Position => SafeReadPosition();

    public TimeSpan Duration => SafeReadDuration();

    public int Volume => _volume;

    public bool IsMuted => _isMuted;

    public event EventHandler<PlayerState>? StateChanged;
    public event EventHandler<Track?>? CurrentTrackChanged;
    public event EventHandler<TimeSpan>? PositionChanged;
    public event EventHandler<TimeSpan>? DurationChanged;
    public event EventHandler? PlaybackEnded;
    public event EventHandler<string>? PlaybackFailed;
    public event EventHandler<int>? VolumeChanged;
    public event EventHandler<bool>? MuteChanged;

    public async Task PlayAsync(Track track, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _openGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 打开新曲目前先停掉旧的，否则会短暂混音。
            _mediaPlayer.Stop();
            ReleaseCurrentSource();

            SetState(PlayerState.Opening);

            ITrackSource source;
            try
            {
                source = _sourceResolver.Resolve(track.FilePath);
            }
            catch (Exception ex)
            {
                // 格式不受支持、插件缺失、文件不存在等，都走这里。
                // 必须广播曲目已清空：界面据此才把标题、封面、按钮可用性收回，
                // 否则会停在上一首的信息上，看起来像"点了没反应"。
                _currentTrack = null;
                CurrentTrackChanged?.Invoke(this, null);
                SetState(PlayerState.Error);
                RaiseFailed(ex.Message);
                return;
            }

            _currentSource = source;

            // 交由 using 释放托管包装是安全的：libvlc_media_player_set_media
            // 会让播放器自行持有一份原生引用，因此这里释放不会打断播放，
            // 同时避免反复打开文件时累积泄漏。
            using var media = CreateMedia(source, track);
            if (media is null)
            {
                _currentTrack = null;
                CurrentTrackChanged?.Invoke(this, null);
                SetState(PlayerState.Error);
                RaiseFailed($"无法打开媒体：{track.DisplayTitle}");
                return;
            }

            _currentTrack = track;
            CurrentTrackChanged?.Invoke(this, _currentTrack);

            if (!_mediaPlayer.Play(media))
            {
                SetState(PlayerState.Error);
                RaiseFailed($"播放失败：{track.DisplayTitle}");
                return;
            }

            SetState(PlayerState.Playing);
        }
        finally
        {
            _openGate.Release();
        }
    }

    public void Pause()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_state != PlayerState.Playing)
        {
            return;
        }

        _mediaPlayer.Pause();
    }

    public void Resume()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_state != PlayerState.Paused)
        {
            return;
        }

        _mediaPlayer.Play();
    }

    public void TogglePlayPause()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        switch (_state)
        {
            case PlayerState.Playing:
                Pause();
                break;

            case PlayerState.Paused:
                Resume();
                break;

            default:
                // 停止、播放结束或出错后的状态。此时按键的合理语义是
                // 「从头再放一遍」，否则按钮看着能点、按下去却什么都不发生。
                Restart();
                break;
        }
    }

    /// <summary>
    /// 从头重新播放当前曲目。没有已加载的媒体时不做任何事。
    /// </summary>
    private void Restart()
    {
        if (_currentTrack is null || _mediaPlayer.Media is null)
        {
            return;
        }

        SetState(PlayerState.Opening);
        _mediaPlayer.Position = 0f;

        if (!_mediaPlayer.Play())
        {
            SetState(PlayerState.Error);
            RaiseFailed($"播放失败：{_currentTrack.DisplayTitle}");
            return;
        }

        SetState(PlayerState.Playing);
    }

    public void Stop()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _mediaPlayer.Stop();
        ReleaseCurrentSource();

        _currentTrack = null;
        CurrentTrackChanged?.Invoke(this, null);
        SetState(PlayerState.Stopped);
    }

    public void Seek(TimeSpan position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // 只有在媒体已加载且时长已知时跳转才有意义。
        if (!_mediaPlayer.IsSeekable || _mediaPlayer.Length <= 0)
        {
            return;
        }

        var clamped = Math.Clamp(position.TotalMilliseconds, 0, _mediaPlayer.Length);
        _mediaPlayer.Time = (long)clamped;
    }

    public void SetVolume(int volume)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var clamped = Math.Clamp(volume, 0, 100);
        var changed = clamped != _volume;

        _volume = clamped;
        _mediaPlayer.Volume = _volume;

        // 值没变就不广播，避免界面双向绑定形成回声。
        if (changed)
        {
            VolumeChanged?.Invoke(this, _volume);
        }

        // 用户主动调音量视为解除静音，符合大多数播放器的习惯。
        if (_volume > 0 && _isMuted)
        {
            SetMute(false);
        }
    }

    public void SetMute(bool muted)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_isMuted == muted)
        {
            return;
        }

        _isMuted = muted;
        _mediaPlayer.Mute = muted;
        MuteChanged?.Invoke(this, _isMuted);
    }

    /// <summary>
    /// 准备 libVLC 的便携模式目录，把它的配置、插件缓存与封面缓存
    /// 全部留在程序目录内，而不是写入 %APPDATA%\vlc。
    /// </summary>
    /// <remarks>
    /// 依据来自 VLC 自身的 Windows 目录实现（src/win32/dirs.c）：
    /// <c>config_GetAppDir()</c> 会先取自身可执行文件所在目录，
    /// 拼出 <c>&lt;exe 目录&gt;\portable</c>，若该路径存在且是目录，
    /// 就直接作为配置/数据/缓存目录返回；否则才退回 CSIDL_APPDATA 下的 vlc 目录。
    /// 也就是说，这个目录必须由宿主提前创建，VLC 自己不会建。
    ///
    /// 注意这是「目录名固定」的约定，不是可自由命名的选项，
    /// 因此这里不改动名字，只负责保证它存在。
    /// </remarks>
    private void EnsurePortableDirectory()
    {
        if (!_options.UsePortableMode)
        {
            return;
        }

        var directory = _options.PortableModeDirectory
            ?? Path.Combine(AppContext.BaseDirectory, "portable");

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex)
        {
            // 只读介质（如光盘、只读共享）上创建失败时不应阻断播放，
            // 但要让问题可见，而不是悄悄退回写用户目录。
            _logger?.LogWarning(
                ex,
                "无法创建 libVLC 便携目录 {Directory}，播放器可能回退到用户目录写入缓存",
                directory);
        }
    }

    private Media? CreateMedia(ITrackSource source, Track track)
    {
        try
        {
            // 普通文件走路径，libVLC 自己管缓冲，内存占用最低。
            if (source.FilePath is not null)
            {
                return new Media(_libVlc, source.FilePath, FromType.FromPath);
            }

            // 加密格式走内存流。这里把流交给 libVLC 的流式读取接口，
            // 由它按需拉取数据，因此不需要把整个文件预解密进内存。
            var stream = source.OpenRead();
            return new Media(_libVlc, new StreamMediaInput(stream));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "为 {Track} 创建媒体失败", track.FilePath);
            return null;
        }
    }

    private void WireEvents()
    {
        _mediaPlayer.Opening += (_, _) => SetState(PlayerState.Opening);
        _mediaPlayer.Playing += (_, _) => SetState(PlayerState.Playing);
        _mediaPlayer.Paused += (_, _) => SetState(PlayerState.Paused);
        _mediaPlayer.Stopped += (_, _) => SetState(PlayerState.Stopped);

        _mediaPlayer.LengthChanged += (_, e) =>
            DurationChanged?.Invoke(this, TimeSpan.FromMilliseconds(e.Length));

        _mediaPlayer.TimeChanged += (_, e) =>
            PositionChanged?.Invoke(this, TimeSpan.FromMilliseconds(e.Time));

        _mediaPlayer.EndReached += (_, _) =>
        {
            // 播完要把状态退回 Stopped，否则引擎仍自称 Playing，
            // 再按播放键会走进「暂停」分支，表现成按了没反应。
            // 注意这里不清空 _currentTrack：曲目信息应当留在界面上，
            // 再按播放键才能从头重放，而不是变成不可点的灰按钮。
            SetState(PlayerState.Stopped);
            PlaybackEnded?.Invoke(this, EventArgs.Empty);
        };

        _mediaPlayer.EncounteredError += (_, _) =>
        {
            SetState(PlayerState.Error);
            RaiseFailed(_currentTrack is null
                ? "播放过程中发生错误。"
                : $"播放失败：{_currentTrack.DisplayTitle}");
        };
    }

    private void SetState(PlayerState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        StateChanged?.Invoke(this, state);
    }

    private void RaiseFailed(string message)
    {
        _logger?.LogWarning("播放失败：{Message}", message);
        PlaybackFailed?.Invoke(this, message);
    }

    /// <summary>
    /// libVLC 在未加载媒体时读取 Time / Length 会抛异常，这里统一兜住。
    /// </summary>
    private TimeSpan SafeReadPosition()
    {
        try
        {
            return _disposed ? TimeSpan.Zero : TimeSpan.FromMilliseconds(_mediaPlayer.Time);
        }
        catch (Exception)
        {
            return TimeSpan.Zero;
        }
    }

    private TimeSpan SafeReadDuration()
    {
        try
        {
            return _disposed ? TimeSpan.Zero : TimeSpan.FromMilliseconds(_mediaPlayer.Length);
        }
        catch (Exception)
        {
            return TimeSpan.Zero;
        }
    }

    private void ReleaseCurrentSource()
    {
        _currentSource?.Dispose();
        _currentSource = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _mediaPlayer.Stop();
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "释放播放器时停止媒体失败");
        }

        ReleaseCurrentSource();

        _mediaPlayer.Dispose();
        _libVlc.Dispose();
        _openGate.Dispose();
    }
}
