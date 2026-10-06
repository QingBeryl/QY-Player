using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using QYPlayer.Core.Library;
using QYPlayer.Core.Metadata;
using QYPlayer.Core.Models;
using QYPlayer.Core.Playback;
using QYPlayer.Core.Settings;
using QYPlayer.Core.Sources;
using QYPlayer.Metadata;
using Wpf.Ui.Controls;

namespace QYPlayer.App.ViewModels;

/// <summary>
/// 主窗口的 ViewModel。
/// </summary>
/// <remarks>
/// <para>
/// M1 阶段的职责范围：打开单个文件、播放控制、进度与音量。
/// M2-4 起接入曲库：列表、搜索、上一首/下一首、四种播放模式，
/// 以及外部文件变动后的自更新（需求文档 9.11）。
/// </para>
/// <para>
/// 线程约定：播放引擎的事件来自其内部线程，这里统一通过
/// <see cref="SynchronizationContext"/> 回到 UI 线程再更新可绑定属性，
/// 否则 WPF 绑定会在跨线程访问时抛异常。
/// </para>
/// </remarks>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IPlaybackService _playback;
    private readonly IMetadataReader _metadataReader;
    private readonly TrackSourceResolver _sourceResolver;
    private readonly ILibraryStore _libraryStore;
    private readonly LibrarySynchronizer _synchronizer;
    private readonly LibraryWatcher _watcher;
    private readonly LibraryScanner _scanner;
    private readonly ISettingsStore _settings;
    private readonly ILogger<MainViewModel>? _logger;
    private readonly SynchronizationContext? _uiContext;

    private bool _isUpdatingFromEngine;
    private bool _disposed;

    /// <summary>防止曲库扫描重入：重扫期间再次收到变动通知时只记一个待办标记。</summary>
    private bool _isSyncing;
    private bool _resyncRequested;

    /// <summary>
    /// 静音与音量滑块的联动规则。见 <see cref="MuteVolumeLink"/>。
    /// 放在 Core 里是为了让「归零 / 还原 / 以用户拖动为准」这三条分支能被单测覆盖。
    /// </summary>
    private readonly MuteVolumeLink _muteLink = new();

    public MainViewModel(
        IPlaybackService playback,
        IMetadataReader metadataReader,
        TrackSourceResolver sourceResolver,
        ILibraryStore libraryStore,
        LibrarySynchronizer synchronizer,
        LibraryWatcher watcher,
        LibraryScanner scanner,
        ISettingsStore settings,
        ILogger<MainViewModel>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(playback);
        ArgumentNullException.ThrowIfNull(metadataReader);
        ArgumentNullException.ThrowIfNull(sourceResolver);
        ArgumentNullException.ThrowIfNull(libraryStore);
        ArgumentNullException.ThrowIfNull(synchronizer);
        ArgumentNullException.ThrowIfNull(watcher);
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(settings);

        _playback = playback;
        _metadataReader = metadataReader;
        _sourceResolver = sourceResolver;
        _libraryStore = libraryStore;
        _synchronizer = synchronizer;
        _watcher = watcher;
        _scanner = scanner;
        _settings = settings;
        _logger = logger;

        // 构造发生在 UI 线程，此时捕获的上下文即用于后续回投。
        _uiContext = SynchronizationContext.Current;

        _volume = _playback.Volume;
        _isMuted = _playback.IsMuted;
        _statusText = "正在加载曲库…";

        // 收起状态直接取自设置。这里刻意赋字段而不是走属性 setter：
        // setter 会触发一次「写设置」的副作用，启动时不该产生这次写盘。
        _isLibraryCollapsed = _settings.Current.IsLibraryCollapsed;

        _queue = new PlayQueue { RepeatMode = _settings.Current.RepeatMode };

        TrackItems = new ReadOnlyObservableCollection<Track>(_trackItems);
        VisibleTracks = new ReadOnlyObservableCollection<Track>(_visibleTracks);

        Subscribe();

        // 监听器的通知来自线程池线程，必须切回 UI 线程再动列表。
        _watcher.Changed += OnLibraryChangedOnDisk;
    }

    /// <summary>曲库全量列表，顺序与数据库一致（路径升序）。</summary>
    private readonly ObservableCollection<Track> _trackItems = [];

    /// <summary>搜索过滤后的列表，界面绑定的是这一个。</summary>
    private readonly ObservableCollection<Track> _visibleTracks = [];

    private readonly PlayQueue _queue;

    public ReadOnlyObservableCollection<Track> TrackItems { get; }

    public ReadOnlyObservableCollection<Track> VisibleTracks { get; }

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

    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>曲库是否空闲（没有在扫描）。界面据此提示「正在扫描…」。</summary>
    [ObservableProperty]
    private bool _isLibraryBusy;

    /// <summary>
    /// 左侧曲库栏是否收起。
    /// </summary>
    /// <remarks>
    /// 收起只改变界面占位，不影响曲库本身：扫描、监听、正在播放的曲目都照旧，
    /// 展开回来还是原来那一份列表。状态写进设置，下次启动保持用户摆好的样子。
    /// </remarks>
    [ObservableProperty]
    private bool _isLibraryCollapsed;

    /// <summary>收起状态的联动文案，让按钮的提示始终说明「点了会怎样」。</summary>
    public string LibraryCollapseHint => IsLibraryCollapsed ? "展开曲库" : "收起曲库";

    /// <summary>
    /// 曲库栏是否正在显示。
    /// </summary>
    /// <remarks>
    /// 供界面直接绑定可见性用。写成一个显式的属性而不是给 XAML 加一个
    /// 「布尔取反再转可见性」的转换器：后者要在每个用到的地方都套一层，
    /// 而这件事在语义上就是「展开」这一件事，放在这里更好读。
    /// </remarks>
    public bool IsLibraryExpanded => !IsLibraryCollapsed;

    /// <summary>
    /// 收起开关的图标：收起后改成向外的箭头，表示「再点一下就展开」。
    /// </summary>
    /// <remarks>
    /// 图标交给 ViewModel 决定而不是用两个按钮互相切换可见性：
    /// 后者的写法要维护两份几乎相同的标记，且切换瞬间可能出现两个都不可见。
    /// </remarks>
    public SymbolRegular LibraryCollapseSymbol => IsLibraryCollapsed
        ? SymbolRegular.PanelLeftExpand24
        : SymbolRegular.PanelLeftContract24;

    /// <summary>当前是否处于「可暂停」状态，用于按钮启用逻辑。</summary>
    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private bool _isPaused;

    /// <summary>
    /// 是否已加载曲目。决定播放控制是否可用。
    /// </summary>
    /// <remarks>
    /// 刻意不看是否正在播放：播完之后曲目仍在，用户按播放键应当能重放，
    /// 按钮不该变灰。只有停止（清空曲目）或播放失败才回到不可用。
    /// </remarks>
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

    /// <summary>
    /// 音量图标。静音或音量为 0 时显示带叉的喇叭，其余按音量大小递进。
    /// </summary>
    /// <remarks>
    /// 档位的判定放在 <see cref="VolumeIconSelector"/>，这里只做「档位 → 字形」的映射。
    /// 之所以不用 XAML 触发器，是因为要同时看静音状态和音量值才能决定画哪个图标，
    /// 单靠属性触发器表达不了这种组合，而本地值又会压住样式触发器导致图标恒定不变。
    /// </remarks>
    public SymbolRegular VolumeSymbol => VolumeIconSelector.Select(Volume, IsMuted) switch
    {
        VolumeIconLevel.Muted => SymbolRegular.SpeakerMute24,
        VolumeIconLevel.Low => SymbolRegular.Speaker024,
        VolumeIconLevel.Medium => SymbolRegular.Speaker124,
        _ => SymbolRegular.Speaker224,
    };

    /// <summary>音量的文字说明，用于提示条。</summary>
    /// <remarks>
    /// 静音时滑块已经归零，此时再显示「音量 0%」会让人以为音量真的被改掉了，
    /// 因此改为提示原本的音量，说明一键恢复会回到哪里。
    /// </remarks>
    public string VolumeText => IsMuted
        ? $"已静音（原音量 {_muteLink.VolumeBeforeMute}%，按 Ctrl+M 恢复）"
        : $"音量 {Volume}%";

    /// <summary>主播放键的图标：播放中显示暂停条，否则显示播放三角。</summary>
    public SymbolRegular PlayPauseSymbol => IsPlaying ? SymbolRegular.Pause24 : SymbolRegular.Play24;

    /// <summary>主播放键的提示文字，随状态在「播放」与「暂停」之间切换。</summary>
    public string PlayPauseHint => IsPlaying ? "暂停 (空格)" : "播放 (空格)";

    /// <summary>播放模式图标。四种模式各有一个字形，用户一眼能认出当前处于哪一种。</summary>
    public SymbolRegular RepeatSymbol => _queue.RepeatMode switch
    {
        RepeatMode.RepeatAll => SymbolRegular.ArrowRepeatAll24,
        RepeatMode.RepeatOne => SymbolRegular.ArrowRepeat124,
        RepeatMode.Shuffle => SymbolRegular.ArrowShuffle24,
        _ => SymbolRegular.ArrowRepeatAllOff24,
    };

    public string RepeatHint => _queue.RepeatMode switch
    {
        RepeatMode.RepeatAll => "列表循环（点击切换）",
        RepeatMode.RepeatOne => "单曲循环（点击切换）",
        RepeatMode.Shuffle => "随机播放（点击切换）",
        _ => "顺序播放（点击切换）",
    };

    /// <summary>被搜索过滤掉多少条，用于「没有匹配」的空态提示。</summary>
    public bool HasNoMatches => !IsLibraryBusy
        && VisibleTracks.Count == 0
        && !string.IsNullOrWhiteSpace(SearchText);

    /// <summary>曲库为空（一首都没有）时的空态提示。</summary>
    public bool IsLibraryEmpty => !IsLibraryBusy
        && TrackItems.Count == 0
        && string.IsNullOrWhiteSpace(SearchText);

    /// <summary>拖动进度条时不把中间值写回引擎，避免跳转抖动与性能损耗。</summary>
    public bool IsSeekDragging { get; set; }

    /// <summary>
    /// 跳转后的位置回传抑制，见 <see cref="SeekLatch"/>。
    /// </summary>
    /// <remarks>
    /// 没有它的话，点击跳转会与引擎异步回传的旧位置赛跑：
    /// 队列里那条「跳转生效前」的位置一旦在提交之后才被处理，
    /// 就会把刚跳过去的位置又拽回来，这就是「时而不灵」的来源。
    /// </remarks>
    private readonly SeekLatch _seekLatch = new();

    [RelayCommand]
    private async Task OpenFileAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择音频文件",

            // 多选是明确需求：用户一次挑好几首时，应当按挑的顺序连着播，
            // 而不是只播第一首、剩下的还得再打开一次。
            Multiselect = true,
            Filter = BuildAudioFilter(),
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true || dialog.FileNames is not { Length: > 0 } files)
        {
            return;
        }

        await PlayPathsAsync(files);
    }

    /// <summary>
    /// 「用本播放器打开一批文件 / 文件夹」的统一入口。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 打开文件对话框、拖放、命令行参数、右键「打开方式」四个入口拿到的都是
    /// 一串路径（可能混着文件与文件夹），最终都收敛到这里，
    /// 保证四种入口的播放顺序规则完全一致。
    /// </para>
    /// <para>
    /// <b>顺序</b>由 <see cref="LibraryScanner.ExpandPaths"/> 决定：用户给的先后优先，
    /// 文件夹就地在它被给出的位置上按名称升序展开。
    /// </para>
    /// <para>
    /// 展开结果会临时替换播放队列，而不是只播第一首：
    /// 「一次打开的多个文件与文件夹按顺序播放」要求播完这一首能自然接上下一首，
    /// 因此队列必须知道这一整批的存在。曲库列表本身不受影响，
    /// 下次曲库同步时会按曲库内容重建队列。
    /// </para>
    /// </remarks>
    public async Task PlayPathsAsync(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var files = _scanner.ExpandPaths(paths);

        if (files.Count == 0)
        {
            StatusText = "没有可播放的音频文件";
            return;
        }

        // 一开始就打上外部队列的标记：读标签要花时间，这期间曲库同步随时可能
        // 完成一次重建，标记早点落下才能保证那一批的顺序不被中途打散。
        _isExternalQueue = true;

        var tracks = new List<Track>(files.Count);

        foreach (var file in files)
        {
            tracks.Add(await ResolveTrackAsync(file));
        }

        // 先摆好队列再播第一首：这样播完自动接下一首时，
        // 队列里已经有下一首的位置，切歌不会退回曲库里的顺序。
        _queue.SetTracks(tracks);
        OnPropertyChanged(nameof(CurrentIndex));

        await PlayAtAsync(0);
    }

    /// <summary>
    /// 取一首曲目的元数据：曲库里已有的直接复用，否则现场读标签。
    /// </summary>
    /// <remarks>
    /// 复用曲库条目是有意义的：它带着缓存好的封面路径，
    /// 重新读一遍标签会重复计算封面，列表里几万首时这笔开销不小。
    /// 元数据读取失败不阻止播放，交给引擎报错即可（例如加密格式未装插件）。
    /// </remarks>
    private async Task<Track> ResolveTrackAsync(string filePath)
    {
        foreach (var known in _trackItems)
        {
            if (string.Equals(known.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
            {
                return known;
            }
        }

        var metadata = await _metadataReader.ReadAsync(filePath);

        return new Track
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
    }

    /// <summary>
    /// 读取元数据并开始播放。公开出来是为了曲库列表的双击播放可以直接复用。
    /// </summary>
    public async Task LoadAndPlayAsync(string filePath)
    {
        // 曲库里的曲目已经带着元数据（含缓存好的封面路径），
        // 再读一次标签纯属浪费，而且会让封面缓存被重复计算。
        var known = _queue.IndexOf(filePath) >= 0 ? _queue.Tracks[_queue.IndexOf(filePath)] : null;

        if (known is not null)
        {
            _queue.SetCurrent(filePath);
            OnPropertyChanged(nameof(CurrentIndex));
            await _playback.PlayAsync(known);
            return;
        }

        await LoadAndPlayFromDiskAsync(filePath);
    }

    /// <summary>不在曲库里的文件（打开文件对话框、拖放进来）走这条路，需要现场读标签。</summary>
    private async Task LoadAndPlayFromDiskAsync(string filePath)
    {
        try
        {
            StatusText = "正在读取文件信息…";

            await _playback.PlayAsync(await ResolveTrackAsync(filePath));
        }
        catch (Exception ex)
        {
            // 元数据读取失败不应当阻止播放，这里只提示引擎的结果。
            _logger?.LogError(ex, "加载文件失败：{Path}", filePath);
            StatusText = $"加载失败：{ex.Message}";
        }
    }

    /// <summary>曲库列表双击：播放被点中的那一首。</summary>
    [RelayCommand]
    private async Task PlayTrackAsync(Track? track)
    {
        if (track is null)
        {
            return;
        }

        // 从列表点歌意味着用户回到了「按曲库顺序听」这件事上，
        // 之前那批临时打开的文件队列就此交还（见 RestoreLibraryQueue）。
        RestoreLibraryQueue();

        await LoadAndPlayAsync(track.FilePath);
    }

    [RelayCommand]
    private async Task PlayNextAsync() => await PlayAtAsync(_queue.NextIndex(autoAdvance: false));

    [RelayCommand]
    private async Task PlayPreviousAsync() => await PlayAtAsync(_queue.PreviousIndex());

    /// <summary>
    /// 切到指定下标。下标为 null 表示「按当前模式没有下一首」，此时停下。
    /// </summary>
    private async Task PlayAtAsync(int? index)
    {
        if (index is not int target || target < 0 || target >= _queue.Count)
        {
            // 顺序播放在末尾没有下一首，语义就是停止。
            _playback.Stop();
            StatusText = "播放结束";
            return;
        }

        var track = _queue.Tracks[target];
        _queue.SetCurrent(track.FilePath);
        OnPropertyChanged(nameof(CurrentIndex));

        await _playback.PlayAsync(track);
    }

    /// <summary>依次切换四种播放模式，并把选择记进设置。</summary>
    [RelayCommand]
    private void CycleRepeatMode()
    {
        _queue.RepeatMode = _queue.RepeatMode switch
        {
            RepeatMode.Sequential => RepeatMode.RepeatAll,
            RepeatMode.RepeatAll => RepeatMode.RepeatOne,
            RepeatMode.RepeatOne => RepeatMode.Shuffle,
            _ => RepeatMode.Sequential,
        };

        _settings.Current.RepeatMode = _queue.RepeatMode;
        _settings.Save();

        OnPropertyChanged(nameof(RepeatSymbol));
        OnPropertyChanged(nameof(RepeatHint));
    }

    /// <summary>
    /// 收起 / 展开左侧曲库栏。
    /// </summary>
    /// <remarks>
    /// 状态属于「用户摆好的界面」，改了立刻存盘，下次启动保持同一布局。
    /// </remarks>
    [RelayCommand]
    private void ToggleLibraryCollapse() => IsLibraryCollapsed = !IsLibraryCollapsed;

    partial void OnIsLibraryCollapsedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsLibraryExpanded));
        OnPropertyChanged(nameof(LibraryCollapseHint));
        OnPropertyChanged(nameof(LibraryCollapseSymbol));

        _settings.Current.IsLibraryCollapsed = value;
        _settings.Save();
    }

    /// <summary>曲库扫描期间用于显示进度。</summary>
    [ObservableProperty]
    private string? _scanProgressText;

    private readonly List<string> _libraryFolders = [];

    /// <summary>
    /// 启动时加载并同步曲库。由窗口在 Loaded 时调用一次。
    /// </summary>
    /// <remarks>
    /// 之所以不在构造函数里做：构造发生在窗口显示之前，
    /// 此时扫描一万个文件会让窗口迟迟不出现（2.3 要求 2 秒内可交互）。
    /// 先让界面出来，再在后台把曲库填进去。
    /// </remarks>
    public async Task InitializeLibraryAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            IsLibraryBusy = true;

            await _libraryStore.InitializeAsync(cancellationToken);

            _libraryFolders.Clear();
            _libraryFolders.AddRange(_settings.Current.LibraryFolders);

            await SyncLibraryAsync(cancellationToken);

            // 同步完成后再开监听：先监听再扫描的话，
            // 扫描期间忽略的事件会被当成「又有新变动」而触发一次多余重扫。
            _watcher.Watch(_libraryFolders);

            StatusText = TrackItems.Count == 0
                ? "曲库为空，请先添加音乐文件夹"
                : $"曲库就绪，共 {TrackItems.Count} 首";
        }
        catch (OperationCanceledException)
        {
            // 窗口关闭时取消，不需要提示。
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "加载曲库失败");
            StatusText = $"曲库加载失败：{ex.Message}";
        }
        finally
        {
            IsLibraryBusy = false;
            NotifyEmptyStateChanged();
        }
    }

    /// <summary>
    /// 把磁盘现状与库里的记录对齐：启动兜底与运行期自更新走的是同一条路径。
    /// </summary>
    private async Task SyncLibraryAsync(CancellationToken cancellationToken = default)
    {
        if (_isSyncing)
        {
            // 扫描期间又收到变动通知时只记一个标记，等这一轮结束后再补一次，
            // 否则同时开两次扫描会互相覆盖结果。
            _resyncRequested = true;
            return;
        }

        _isSyncing = true;

        try
        {
            do
            {
                _resyncRequested = false;
                await RunSyncOnceAsync(cancellationToken);
            }
            while (_resyncRequested && !cancellationToken.IsCancellationRequested);
        }
        finally
        {
            _isSyncing = false;

            // 监听目录跟着设置走：用户新加了文件夹后要立刻开始盯它。
            _watcher.Watch(_libraryFolders);
        }
    }

    private async Task RunSyncOnceAsync(CancellationToken cancellationToken)
    {
        var known = await _libraryStore.LoadAsync(cancellationToken);

        // 进度回调来自扫描线程，先切回 UI 线程再改可绑定属性。
        var progress = new Progress<ScanProgress>(report =>
        {
            ScanProgressText = report.Total <= 0
                ? null
                : $"正在解析 {report.Completed} / {report.Total}";
        });

        var result = await _synchronizer.SyncAsync(
            known,
            _libraryFolders,
            progress,
            cancellationToken);

        // 只有新增或变化过的曲目才写库，避免每次启动都对整库做一次写事务。
        if (result.NeedUpsert.Count > 0)
        {
            await _libraryStore.UpsertAsync(result.NeedUpsert, cancellationToken);
        }

        if (result.RemovedPaths.Count > 0)
        {
            await _libraryStore.RemoveMissingAsync(
                result.Tracks.Select(track => track.FilePath).ToList(),
                cancellationToken);
        }

        ApplyTracks(result.Tracks);

        ScanProgressText = null;
    }

    /// <summary>
    /// 把同步结果换成界面列表。保留当前正在播放的那一首的队列位置。
    /// </summary>
    /// <remarks>
    /// 若当前播放的是「从外面打开的一批文件」，队列就<b>不</b>跟着曲库重建：
    /// 那一批的播放顺序是用户当时点选的顺序，曲库同步没有理由把它打散。
    /// 这一点在启动时尤其重要——双击文件启动时，应用会同时开始加载曲库，
    /// 若这里照常重建，刚打开的那一批在几百毫秒后就会被曲库顺序顶掉。
    /// </remarks>
    private void ApplyTracks(IReadOnlyList<Track> tracks)
    {
        var currentPath = _playback.CurrentTrack?.FilePath ?? _queue.Current?.FilePath;

        _trackItems.Clear();
        foreach (var track in tracks)
        {
            _trackItems.Add(track);
        }

        if (!_isExternalQueue)
        {
            // 队列按新列表重建，但当前曲目按路径重新定位：
            // 刷新不该让「正在播的是哪一首」丢失，否则切下一首会跳回开头。
            _queue.SetTracks(tracks, currentPath);
        }

        ApplyFilter();
        OnPropertyChanged(nameof(CurrentIndex));
    }

    /// <summary>
    /// 是否正在播放「从外面打开的一批文件 / 文件夹」。
    /// </summary>
    /// <remarks>
    /// 这一批的队列是临时的：它只服务于「一次打开的东西按顺序播完」。
    /// 用户一旦从曲库列表里点歌、或往曲库里加文件夹，
    /// 队列就该交还给曲库顺序（见 <see cref="RestoreLibraryQueue"/>）。
    /// </remarks>
    private bool _isExternalQueue;

    /// <summary>把队列交还给曲库顺序。仅在外部队列生效时有动作。</summary>
    private void RestoreLibraryQueue()
    {
        if (!_isExternalQueue)
        {
            return;
        }

        _isExternalQueue = false;

        // 按路径重新定位当前曲目：它可能不在曲库里，那么下标退回 -1，
        // 界面上不高亮任何一行，与「这首不在曲库中」的事实一致。
        _queue.SetTracks(_trackItems, _playback.CurrentTrack?.FilePath);
        OnPropertyChanged(nameof(CurrentIndex));
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnIsLibraryBusyChanged(bool value) => NotifyEmptyStateChanged();

    private void NotifyEmptyStateChanged()
    {
        OnPropertyChanged(nameof(HasNoMatches));
        OnPropertyChanged(nameof(IsLibraryEmpty));
    }

    /// <summary>
    /// 按关键词过滤列表。
    /// </summary>
    /// <remarks>
    /// 在内存里过滤而不是查数据库：曲库规模是「几千到几万条」，
    /// 内存过滤是毫秒级，而每次敲键都发一次 SQL 反而更慢，
    /// 也会让列表在输入过程中反复重排。
    /// </remarks>
    private void ApplyFilter()
    {
        var keyword = SearchText?.Trim();

        _visibleTracks.Clear();

        foreach (var track in _trackItems)
        {
            if (string.IsNullOrEmpty(keyword) || Matches(track, keyword))
            {
                _visibleTracks.Add(track);
            }
        }

        NotifyEmptyStateChanged();
    }

    /// <summary>标题、艺术家、专辑、文件名任一命中即可，与用户「记得的那点信息」对齐。</summary>
    private static bool Matches(Track track, string keyword) =>
        Contains(track.DisplayTitle, keyword)
        || Contains(track.DisplayArtist, keyword)
        || Contains(track.DisplayAlbum, keyword)
        || Contains(track.FileName, keyword);

    private static bool Contains(string source, string keyword) =>
        source.Contains(keyword, StringComparison.CurrentCultureIgnoreCase);

    /// <summary>
    /// 当前曲目在<b>可见列表</b>中的下标，供列表高亮选中行。
    /// </summary>
    /// <remarks>
    /// 必须按可见列表算而不是全量列表：搜索过滤之后两者的下标不再一致，
    /// 用全量下标去索引可见列表会把高亮落到别的曲目（甚至越界失焦）。
    /// 正在播放的那一首被过滤掉时返回 -1，此时列表不高亮任何一行，符合预期。
    /// </remarks>
    public int CurrentIndex => _visibleTracks.IndexOf(FindCurrentTrack()!);

    /// <summary>在列表里找出与引擎当前曲目路径相同的那一项。</summary>
    private Track? FindCurrentTrack()
    {
        var path = _playback.CurrentTrack?.FilePath;

        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (var track in _visibleTracks)
        {
            if (string.Equals(track.FilePath, path, StringComparison.OrdinalIgnoreCase))
            {
                return track;
            }
        }

        return null;
    }

    /// <summary>
    /// 让用户挑一个文件夹加入曲库。空曲库时界面的引导按钮也走这里。
    /// </summary>
    [RelayCommand]
    private async Task AddLibraryFolderAsync()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择音乐文件夹",
            Multiselect = false,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var folder = dialog.FolderName;

        if (_libraryFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
        {
            StatusText = "该文件夹已在曲库中";
            return;
        }

        _libraryFolders.Add(folder);

        // 曲库目录属于设置的一部分，改了要立刻存盘，否则下次启动又没了。
        _settings.Current.LibraryFolders = [.. _libraryFolders];
        _settings.Save();

        // 加入了新曲库文件夹，说明用户接下来要按曲库听，
        // 之前那批临时打开的文件队列可以交还了。
        RestoreLibraryQueue();

        IsLibraryBusy = true;
        StatusText = "正在扫描曲库…";

        try
        {
            await SyncLibraryAsync();
            StatusText = $"曲库已更新，共 {TrackItems.Count} 首";
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "扫描曲库失败");
            StatusText = $"扫描失败：{ex.Message}";
        }
        finally
        {
            IsLibraryBusy = false;
            NotifyEmptyStateChanged();
        }
    }

    [RelayCommand]
    private void TogglePlayPause() => _playback.TogglePlayPause();

    [RelayCommand]
    private void Stop() => _playback.Stop();

    [RelayCommand]
    private void ToggleMute() => _playback.SetMute(!_playback.IsMuted);

    /// <summary>
    /// 进度条松手、单击或键盘微调后调用，把位置写回引擎。
    /// </summary>
    /// <remarks>
    /// 提交之后立刻上闩：引擎的位置回传是异步的，队列里可能还压着一条
    /// 跳转生效前的旧位置，它一旦晚于这里被处理就会把刚跳过去的值拽回来。
    /// 上闩之后由 <see cref="SeekLatch"/> 负责识别并忽略这类旧回包。
    /// </remarks>
    [RelayCommand]
    private void CommitSeek()
    {
        IsSeekDragging = false;

        if (!HasMedia)
        {
            return;
        }

        var target = PositionSeconds;
        _playback.Seek(TimeSpan.FromSeconds(target));
        _seekLatch.Arm(target);
    }

    partial void OnVolumeChanged(int value)
    {
        // 图标的格数、提示文字都跟着音量走，必须显式通知，
        // 否则只更新了滑块位置，喇叭图标还停在上一个档位。
        OnPropertyChanged(nameof(VolumeSymbol));
        OnPropertyChanged(nameof(VolumeText));

        if (_isUpdatingFromEngine)
        {
            return;
        }

        _playback.SetVolume(value);
    }

    partial void OnIsMutedChanged(bool value)
    {
        OnPropertyChanged(nameof(VolumeSymbol));
        OnPropertyChanged(nameof(VolumeText));
    }

    partial void OnIsPlayingChanged(bool value)
    {
        OnPropertyChanged(nameof(PlayPauseSymbol));
        OnPropertyChanged(nameof(PlayPauseHint));
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
        _playback.VolumeChanged += OnVolumeChangedFromEngine;
        _playback.MuteChanged += OnMuteChangedFromEngine;
    }

    private void Unsubscribe()
    {
        _playback.StateChanged -= OnStateChanged;
        _playback.CurrentTrackChanged -= OnCurrentTrackChanged;
        _playback.PositionChanged -= OnPositionChanged;
        _playback.DurationChanged -= OnDurationChanged;
        _playback.PlaybackEnded -= OnPlaybackEnded;
        _playback.PlaybackFailed -= OnPlaybackFailed;
        _playback.VolumeChanged -= OnVolumeChangedFromEngine;
        _playback.MuteChanged -= OnMuteChangedFromEngine;
    }

    /// <summary>
    /// 引擎侧的音量变化。会走这里的情况不只用户拖动滑块：
    /// 静音时若引擎把音量归零，界面也要跟着动。
    /// </summary>
    private void OnVolumeChangedFromEngine(object? sender, int volume) => Post(() =>
    {
        if (Volume == volume)
        {
            return;
        }

        _isUpdatingFromEngine = true;
        Volume = volume;
        _isUpdatingFromEngine = false;
    });

    private void OnMuteChangedFromEngine(object? sender, bool muted) => Post(() =>
    {
        // 调音量会自动解除静音，引擎通过这个事件把结果告诉界面。
        // 不订阅它的话，图标会停在静音状态，与实际声音不符。
        IsMuted = muted;

        // 静音时滑块归零，解除时回到静音前的位置。
        // 放在 IsMuted 之后：Volume 的 setter 会依据静音状态决定图标档位，
        // 顺序反了图标会先按旧状态画一次。
        //
        // 这里刻意走 _isUpdatingFromEngine，不把 0 写进引擎：
        // 静音靠的是引擎的 Mute 开关，音量值应当保持原样，
        // 否则引擎侧的音量会被真的改成 0，解除静音就找不回原值了。
        if (_muteLink.OnMuteChanged(muted, Volume) is int target && target != Volume)
        {
            _isUpdatingFromEngine = true;
            Volume = target;
            _isUpdatingFromEngine = false;
        }

        // 提示文字里带着静音前的音量，归零与还原之后都必须重算。
        OnPropertyChanged(nameof(VolumeText));
    });

    private void OnStateChanged(object? sender, PlayerState state) => Post(() =>
    {
        IsPlaying = state == PlayerState.Playing;
        IsPaused = state == PlayerState.Paused;

        // HasMedia 不在这里推导：它表示「是否已加载曲目」，播完之后曲目还在，
        // 按播放键应当能重放。若跟着状态走，播完就会变灰且再也点不动。
        StatusText = state switch
        {
            PlayerState.Opening => "正在打开…",
            PlayerState.Playing => "正在播放",
            PlayerState.Paused => "已暂停",
            // 走到 Stopped 只有两种可能：用户停止，或自然播完
            //（后者紧接着会被 PlaybackEnded 覆盖成「播放结束」）。
            PlayerState.Stopped => "已停止",
            PlayerState.Error => "播放出错",
            _ => StatusText,
        };
    });

    private void OnCurrentTrackChanged(object? sender, Track? track) => Post(() =>
    {
        CurrentTrack = track;

        // 曲目清空（停止）或装载（开始播放）都走这里，是 HasMedia 的唯一来源。
        HasMedia = track is not null;

        // 引擎装载的曲目可能与队列记录的不是同一个实例（例如拖进来的单文件），
        // 按路径对齐可以让切歌从它所在的位置继续。
        if (track is not null)
        {
            _queue.SetCurrent(track.FilePath);
        }

        OnPropertyChanged(nameof(CurrentIndex));

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

        // 跳转刚提交时，队列里可能还压着一条跳转生效前的旧位置。
        // 它若在此刻被采用，就会把进度条拽回原处，看起来像「点了没反应」。
        if (!_seekLatch.ShouldAccept(position.TotalSeconds))
        {
            return;
        }

        _isUpdatingFromEngine = true;
        PositionSeconds = position.TotalSeconds;
        _isUpdatingFromEngine = false;
    });

    private void OnDurationChanged(object? sender, TimeSpan duration) => Post(() =>
        DurationSeconds = duration.TotalSeconds);

    /// <summary>
    /// 自然播完：交给队列决定下一首。
    /// </summary>
    /// <remarks>
    /// 引擎在播完时刻意保留了当前曲目，因此这里可以直接往下一首走，
    /// 不需要先处理「曲目被清空」的情形。顺序播放到末尾时
    /// <see cref="PlayQueue.NextIndex"/> 返回 null，此时停下即符合
    /// 「顺序播放，播完最后一首停止」的定义（需求文档 2.1）。
    ///
    /// <c>autoAdvance: true</c> 的区别只在单曲循环：自动切歌时原地重播。
    /// </remarks>
    private void OnPlaybackEnded(object? sender, EventArgs e) => Post(() =>
    {
        var next = _queue.NextIndex(autoAdvance: true);

        if (next is null)
        {
            StatusText = "播放结束";
            IsPlaying = false;
            IsPaused = false;
            return;
        }

        // 不能 await：这是事件处理器，用火忘方式启动即可，
        // 失败路径由 PlayAsync 内部的错误广播兜住。
        _ = PlayAtAsync(next);
    });

    private void OnPlaybackFailed(object? sender, string message) => Post(() =>
    {
        // HasMedia 不在这里改：它由曲目事件统一维护。引擎在失败时已广播
        // 曲目清空，这里再清一次只会造成两个来源互相打架。
        StatusText = message;
        IsPlaying = false;
        IsPaused = false;
    });

    /// <summary>
    /// 磁盘上的曲库发生变化（新增、删除、改名、写入完成）。
    /// </summary>
    /// <remarks>
    /// 监听器只告诉「变了」，具体变没变、变了什么由重扫判断（8.2 的口径）：
    /// 大小与最后写入时间都没动过，这次重扫就一首也不解析。
    /// </remarks>
    private void OnLibraryChangedOnDisk(object? sender, EventArgs e) => Post(() =>
    {
        StatusText = "曲库有变动，正在更新…";
        _ = SyncLibraryAsyncFromWatcherAsync();
    });

    private async Task SyncLibraryAsyncFromWatcherAsync()
    {
        try
        {
            IsLibraryBusy = true;
            await SyncLibraryAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "响应曲库变动失败");
        }
        finally
        {
            IsLibraryBusy = false;

            if (StatusText == "曲库有变动，正在更新…")
            {
                StatusText = $"曲库已更新，共 {TrackItems.Count} 首";
            }
        }
    }

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

        _watcher.Changed -= OnLibraryChangedOnDisk;
        _watcher.Dispose();

        Unsubscribe();
    }
}
