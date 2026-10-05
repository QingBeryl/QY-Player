using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using QYPlayer.Core.Models;
using QYPlayer.Core.Playback;
using QYPlayer.Core.Sources;
using QYPlayer.Metadata;

namespace QYPlayer.App.ViewModels;

/// <summary>
/// 主窗口的 ViewModel。
/// </summary>
/// <remarks>
/// M1 阶段的职责范围：打开单个文件、播放控制、进度与音量。
/// 曲库、播放列表、上一首/下一首在 M2 与 M2.5 接入。
///
/// 线程约定：播放引擎的事件来自其内部线程，这里统一通过
/// <see cref="SynchronizationContext"/> 回到 UI 线程再更新可绑定属性，
/// 否则 WPF 绑定会在跨线程访问时抛异常。
/// </remarks>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IPlaybackService _playback;
    private readonly IMetadataReader _metadataReader;
    private readonly TrackSourceResolver _sourceResolver;
    private readonly ILogger<MainViewModel>? _logger;
    private readonly SynchronizationContext? _uiContext;

    private bool _isUpdatingFromEngine;
    private bool _disposed;

    public MainViewModel(
        IPlaybackService playback,
        IMetadataReader metadataReader,
        TrackSourceResolver sourceResolver,
        ILogger<MainViewModel>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(playback);
        ArgumentNullException.ThrowIfNull(metadataReader);
        ArgumentNullException.ThrowIfNull(sourceResolver);

        _playback = playback;
        _metadataReader = metadataReader;
        _sourceResolver = sourceResolver;
        _logger = logger;

        // 构造发生在 UI 线程，此时捕获的上下文即用于后续回投。
        _uiContext = SynchronizationContext.Current;

        _volume = _playback.Volume;
        _statusText = "请选择要播放的音频文件";

        Subscribe();
    }

    [ObservableProperty]
    private Track? _currentTrack;

    [ObservableProperty]
    private string _title = "QY Player";

    [ObservableProperty]
    private string _artist = string.Empty;

    [ObservableProperty]
    private string _album = string.Empty;

    [ObservableProperty]
    private string _formatText = string.Empty;

    [ObservableProperty]
    private string _statusText;

    /// <summary>当前是否处于「可暂停」状态，用于按钮启用逻辑。</summary>
    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private bool _isPaused;

    /// <summary>是否已加载媒体。决定播放控制是否可用。</summary>
    [ObservableProperty]
    private bool _hasMedia;

    [ObservableProperty]
    private int _volume;

    [ObservableProperty]
    private bool _isMuted;

    /// <summary>进度条位置，单位秒。与引擎的毫秒位置双向同步。</summary>
    [ObservableProperty]
    private double _positionSeconds;

    [ObservableProperty]
    private double _durationSeconds;

    /// <summary>当前位置的文本形式，例如 01:23。</summary>
    public string PositionText => FormatTime(TimeSpan.FromSeconds(PositionSeconds));

    public string DurationText => FormatTime(TimeSpan.FromSeconds(DurationSeconds));

    /// <summary>拖动进度条时不把中间值写回引擎，避免跳转抖动与性能损耗。</summary>
    public bool IsSeekDragging { get; set; }

    [RelayCommand]
    private async Task OpenFileAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择音频文件",
            Filter = BuildAudioFilter(),
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await LoadAndPlayAsync(dialog.FileName);
    }

    /// <summary>
    /// 读取元数据并开始播放。公开出来是为了 M2 的曲库双击播放可以直接复用。
    /// </summary>
    public async Task LoadAndPlayAsync(string filePath)
    {
        try
        {
            StatusText = "正在读取文件信息…";

            var metadata = await _metadataReader.ReadAsync(filePath);

            var track = new Track
            {
                FilePath = filePath,
                Title = metadata.Title,
                Artist = metadata.Artist,
                Album = metadata.Album,
                Duration = metadata.Duration,
                Format = AudioFormatDetector.FromPath(filePath),
                SampleRate = metadata.SampleRate,
                BitsPerSample = metadata.BitsPerSample,
                Bitrate = metadata.Bitrate,
                CoverCachePath = metadata.CoverCachePath,
            };

            await _playback.PlayAsync(track);
        }
        catch (Exception ex)
        {
            // 元数据读取失败不应当阻止播放，这里只提示引擎的结果。
            _logger?.LogError(ex, "加载文件失败：{Path}", filePath);
            StatusText = $"加载失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void TogglePlayPause() => _playback.TogglePlayPause();

    [RelayCommand]
    private void Stop() => _playback.Stop();

    [RelayCommand]
    private void ToggleMute() => _playback.SetMute(!_playback.IsMuted);

    /// <summary>
    /// 进度条松手后调用，把最终位置写回引擎。
    /// </summary>
    [RelayCommand]
    private void CommitSeek()
    {
        IsSeekDragging = false;

        if (!HasMedia)
        {
            return;
        }

        _playback.Seek(TimeSpan.FromSeconds(PositionSeconds));
    }

    partial void OnVolumeChanged(int value)
    {
        if (_isUpdatingFromEngine)
        {
            return;
        }

        _playback.SetVolume(value);
    }

    partial void OnPositionSecondsChanged(double value) => OnPropertyChanged(nameof(PositionText));

    partial void OnDurationSecondsChanged(double value) => OnPropertyChanged(nameof(DurationText));

    private void Subscribe()
    {
        _playback.StateChanged += OnStateChanged;
        _playback.CurrentTrackChanged += OnCurrentTrackChanged;
        _playback.PositionChanged += OnPositionChanged;
        _playback.DurationChanged += OnDurationChanged;
        _playback.PlaybackEnded += OnPlaybackEnded;
        _playback.PlaybackFailed += OnPlaybackFailed;
    }

    private void Unsubscribe()
    {
        _playback.StateChanged -= OnStateChanged;
        _playback.CurrentTrackChanged -= OnCurrentTrackChanged;
        _playback.PositionChanged -= OnPositionChanged;
        _playback.DurationChanged -= OnDurationChanged;
        _playback.PlaybackEnded -= OnPlaybackEnded;
        _playback.PlaybackFailed -= OnPlaybackFailed;
    }

    private void OnStateChanged(object? sender, PlayerState state) => Post(() =>
    {
        IsPlaying = state == PlayerState.Playing;
        IsPaused = state == PlayerState.Paused;
        HasMedia = state is PlayerState.Playing or PlayerState.Paused or PlayerState.Opening;

        StatusText = state switch
        {
            PlayerState.Opening => "正在打开…",
            PlayerState.Playing => "正在播放",
            PlayerState.Paused => "已暂停",
            PlayerState.Stopped => HasMedia ? "已停止" : StatusText,
            PlayerState.Error => "播放出错",
            _ => StatusText,
        };
    });

    private void OnCurrentTrackChanged(object? sender, Track? track) => Post(() =>
    {
        CurrentTrack = track;

        if (track is null)
        {
            Title = "QY Player";
            Artist = string.Empty;
            Album = string.Empty;
            FormatText = string.Empty;
            DurationSeconds = 0;
            PositionSeconds = 0;
            return;
        }

        Title = track.DisplayTitle;
        Artist = track.DisplayArtist;
        Album = track.DisplayAlbum;
        FormatText = BuildFormatText(track);

        // 标签里的时长可能不准，以引擎解析出的为准，先给一个近似值避免界面空白。
        DurationSeconds = track.Duration.TotalSeconds;
    });

    private void OnPositionChanged(object? sender, TimeSpan position) => Post(() =>
    {
        if (IsSeekDragging)
        {
            return;
        }

        _isUpdatingFromEngine = true;
        PositionSeconds = position.TotalSeconds;
        _isUpdatingFromEngine = false;
    });

    private void OnDurationChanged(object? sender, TimeSpan duration) => Post(() =>
        DurationSeconds = duration.TotalSeconds);

    private void OnPlaybackEnded(object? sender, EventArgs e) => Post(() =>
    {
        // M2.5 接入播放队列后，这里改为自动切下一首。
        StatusText = "播放结束";
        IsPlaying = false;
        IsPaused = false;
    });

    private void OnPlaybackFailed(object? sender, string message) => Post(() =>
    {
        StatusText = message;
        HasMedia = false;
        IsPlaying = false;
        IsPaused = false;
    });

    /// <summary>
    /// 把回调切回 UI 线程。构造时若没有捕获到上下文（例如在设计器或测试中），
    /// 则直接执行，保证逻辑仍可运行。
    /// </summary>
    private void Post(Action action)
    {
        if (_uiContext is null || SynchronizationContext.Current == _uiContext)
        {
            action();
            return;
        }

        _uiContext.Post(_ => action(), null);
    }

    private static string BuildFormatText(Track track)
    {
        var parts = new List<string>();

        if (track.Format != AudioFormat.Unknown)
        {
            parts.Add(track.Format.ToString().ToUpperInvariant());
        }

        if (track.SampleRate > 0)
        {
            parts.Add($"{track.SampleRate / 1000.0:0.#} kHz");
        }

        if (track.BitsPerSample > 0)
        {
            parts.Add($"{track.BitsPerSample} bit");
        }

        if (track.Bitrate > 0)
        {
            parts.Add($"{track.Bitrate} kbps");
        }

        return string.Join("  ·  ", parts);
    }

    private static string BuildAudioFilter()
    {
        var extensions = AudioFormatDetector.SupportedExtensions
            .Select(e => "*" + e)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var allSupported = string.Join(";", extensions);

        return $"音频文件|{allSupported}"
               + "|常见格式|*.mp3;*.flac;*.wav;*.m4a;*.ogg;*.wma"
               + "|所有文件|*.*";
    }

    private static string FormatTime(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes:00}:{value.Seconds:00}";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Unsubscribe();
    }
}
