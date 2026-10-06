using QYPlayer.Core.Models;

namespace QYPlayer.Core.Playback;

/// <summary>
/// 播放队列：记住「当前在列表的哪一首」以及上一首 / 下一首该去哪一首。
/// </summary>
/// <remarks>
/// <para>
/// 这一层刻意不碰 <see cref="IPlaybackService"/>，只做纯粹的「位置计算」：
/// 输入是列表长度、当前位置与播放模式，输出是下一个索引。
/// 因为切歌规则里有不少边界分支（列表末尾、只有一首、随机不能连抽同一首），
/// 把它们与「真正去播放」解耦之后，这些分支才能用单测穷举，
/// 而不必为了测一条边界去启动一个 libVLC 实例。
/// </para>
/// <para>
/// 曲目身份按 <see cref="Track.FilePath"/> 对齐，与 <c>ILibraryStore</c> 一致：
/// <see cref="Track.Id"/> 是数据库主键，重新扫描会产出新实例，
/// 拿它做身份会让「曲库刷新后当前曲目丢失」。
/// </para>
/// </remarks>
public sealed class PlayQueue
{
    private readonly List<Track> _tracks = [];
    private readonly IRandomSource _random;

    private int _currentIndex = -1;

    public PlayQueue(IRandomSource? random = null) =>
        _random = random ?? new SystemRandomSource();

    /// <summary>队列内容，顺序与曲库列表一致（路径升序）。</summary>
    public IReadOnlyList<Track> Tracks => _tracks;

    public int Count => _tracks.Count;

    public bool IsEmpty => _tracks.Count == 0;

    /// <summary>播放模式。由界面设置，存盘后下次启动恢复。</summary>
    public RepeatMode RepeatMode { get; set; } = RepeatMode.Sequential;

    /// <summary>当前曲目在队列中的下标。没有当前曲目时为 -1。</summary>
    public int CurrentIndex => _currentIndex;

    public Track? Current =>
        _currentIndex >= 0 && _currentIndex < _tracks.Count ? _tracks[_currentIndex] : null;

    /// <summary>
    /// 用新的曲库列表替换队列内容。
    /// </summary>
    /// <param name="tracks">新的曲库曲目，应当是稳定顺序。</param>
    /// <param name="currentPath">
    /// 当前正在播放的曲目路径。按路径重新定位下标，
    /// 这样曲库刷新（新增、删除文件）不会让「正在播放的是哪一首」丢失。
    /// </param>
    public void SetTracks(IEnumerable<Track> tracks, string? currentPath = null)
    {
        ArgumentNullException.ThrowIfNull(tracks);

        _tracks.Clear();
        _tracks.AddRange(tracks);

        _currentIndex = currentPath is null ? -1 : IndexOf(currentPath);
    }

    /// <summary>按路径查下标，找不到返回 -1。</summary>
    public int IndexOf(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return -1;
        }

        for (var index = 0; index < _tracks.Count; index++)
        {
            if (string.Equals(_tracks[index].FilePath, filePath, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>把当前曲目改为指定路径。找不到时当前位置退回 -1。</summary>
    public void SetCurrent(string filePath) => _currentIndex = IndexOf(filePath);

    public void Clear()
    {
        _tracks.Clear();
        _currentIndex = -1;
    }

    /// <summary>
    /// 算下一首的下标。
    /// </summary>
    /// <param name="autoAdvance">
    /// 是否由「播完自动切」触发。这个参数只影响单曲循环：
    /// 自动切时原地重播，而用户手点下一首是明确的换歌意图，照常前进。
    /// </param>
    /// <returns>下一首的下标；顺序播放已到末尾时返回 null，表示应当停下。</returns>
    public int? NextIndex(bool autoAdvance)
    {
        if (IsEmpty)
        {
            return null;
        }

        if (RepeatMode == RepeatMode.Shuffle)
        {
            return PickRandomOther();
        }

        if (autoAdvance && RepeatMode == RepeatMode.RepeatOne)
        {
            // 只有一首歌时也必须重播它，因此不小于 0 的下标退回 0。
            return _currentIndex >= 0 ? _currentIndex : 0;
        }

        // 没有当前曲目（-1）时加一正好是 0，语义即「从头开始」，无需特判。
        var next = _currentIndex + 1;

        if (next < _tracks.Count)
        {
            return next;
        }

        // 走到末尾：列表循环回到开头，顺序播放则没有下一首。
        return RepeatMode == RepeatMode.RepeatAll ? 0 : null;
    }

    /// <summary>
    /// 算上一首的下标。
    /// </summary>
    /// <remarks>
    /// 上一首在队首时一律回到第一首重播，不做从末尾绕回——
    /// 除非是列表循环，那时绕回末尾才与「循环」的语义一致。
    /// </remarks>
    public int? PreviousIndex()
    {
        if (IsEmpty)
        {
            return null;
        }

        if (RepeatMode == RepeatMode.Shuffle)
        {
            return PickRandomOther();
        }

        var previous = _currentIndex - 1;

        if (previous >= 0)
        {
            return previous;
        }

        return RepeatMode == RepeatMode.RepeatAll ? _tracks.Count - 1 : 0;
    }

    /// <summary>
    /// 随机抽一首「不是当前这首」。只有一首时原地重播。
    /// </summary>
    private int PickRandomOther()
    {
        if (_tracks.Count == 1)
        {
            return 0;
        }

        // 当前曲目未知时不排除任何一首，否则会永远抽不到第一首。
        if (_currentIndex < 0)
        {
            return _random.Next(_tracks.Count);
        }

        // 在「去掉当前这首」之后的 n-1 个槽位里抽，再把落在当前之后的整体后移一位，
        // 相当于对整个列表随机而把当前那一首排除掉，且概率均匀。
        var pick = _random.Next(_tracks.Count - 1);
        return pick >= _currentIndex ? pick + 1 : pick;
    }
}
