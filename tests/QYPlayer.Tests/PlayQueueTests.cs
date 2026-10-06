using QYPlayer.Core.Models;
using QYPlayer.Core.Playback;

namespace QYPlayer.Tests;

/// <summary>
/// 播放队列的切歌规则。
/// </summary>
/// <remarks>
/// 这一层是纯计算，因此可以把四种模式 × 三个位置（首 / 中 / 尾）
/// 以及「只有一首」「空列表」这些边界全部穷举，不需要任何替身或 IO。
/// 随机模式用 <see cref="FixedRandomSource"/> 把「随机」钉死，只验证排除当前曲目这条规则。
/// </remarks>
public class PlayQueueTests
{
    /// <summary>按给定序列产出随机数，用于把随机播放的行为固定下来。</summary>
    private sealed class FixedRandomSource(params int[] values) : IRandomSource
    {
        private int _cursor;

        public int Next(int maxExclusive)
        {
            Assert.True(maxExclusive > 0, "队列不该在空列表上取随机数");

            var value = values[Math.Min(_cursor, values.Length - 1)];
            _cursor++;
            return value % maxExclusive;
        }
    }

    private static Track TrackAt(int index) => new()
    {
        Id = index.ToString(),
        FilePath = $@"C:\music\{index:00}.mp3",
    };

    private static PlayQueue QueueWith(int count, RepeatMode mode, int currentIndex = 0)
    {
        var queue = new PlayQueue { RepeatMode = mode };
        queue.SetTracks(Enumerable.Range(0, count).Select(TrackAt));
        queue.SetCurrent(TrackAt(currentIndex).FilePath);

        return queue;
    }

    [Fact]
    public void 空队列没有上一首与下一首()
    {
        var queue = new PlayQueue { RepeatMode = RepeatMode.RepeatAll };

        Assert.True(queue.IsEmpty);
        Assert.Null(queue.NextIndex(autoAdvance: true));
        Assert.Null(queue.PreviousIndex());
        Assert.Null(queue.Current);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(4, 5)]
    public void 顺序播放依次前进(int current, int expected)
    {
        Assert.Equal(expected, QueueWith(6, RepeatMode.Sequential, current).NextIndex(autoAdvance: true));
    }

    [Fact]
    public void 顺序播放到末尾后没有下一首()
    {
        // Sequential 的语义就是「播完最后一首停止」，返回 null 即代表停止。
        Assert.Null(QueueWith(3, RepeatMode.Sequential, currentIndex: 2).NextIndex(autoAdvance: true));
    }

    [Fact]
    public void 列表循环到末尾后绕回第一首()
    {
        Assert.Equal(0, QueueWith(3, RepeatMode.RepeatAll, currentIndex: 2).NextIndex(autoAdvance: true));
    }

    [Fact]
    public void 单曲循环在自动切歌时原地重播()
    {
        Assert.Equal(1, QueueWith(4, RepeatMode.RepeatOne, currentIndex: 1).NextIndex(autoAdvance: true));
    }

    [Fact]
    public void 单曲循环下用户手点下一首仍然换歌()
    {
        // 单曲循环只是「不许自动换歌」，不是「按钮失效」：
        // 用户明确点了下一首，就应当照常前进，否则按钮看起来是坏的。
        Assert.Equal(2, QueueWith(4, RepeatMode.RepeatOne, currentIndex: 1).NextIndex(autoAdvance: false));
    }

    [Fact]
    public void 只有一首时单曲循环重播它自己()
    {
        var queue = QueueWith(1, RepeatMode.RepeatOne);
        Assert.Equal(0, queue.NextIndex(autoAdvance: true));
    }

    [Fact]
    public void 只有一首时随机播放重播它自己()
    {
        // 列表里没有别的可选，返回 null 会让播放停在末尾，
        // 表现成「随机模式下歌放完就没声了」。
        var queue = QueueWith(1, RepeatMode.Shuffle);
        Assert.Equal(0, queue.NextIndex(autoAdvance: true));
    }

    [Theory]
    [InlineData(0, 0, 1)]
    [InlineData(2, 0, 0)]
    [InlineData(2, 3, 4)]
    [InlineData(4, 3, 3)]
    public void 随机播放不会连抽到当前这首(int current, int rawPick, int expected)
    {
        // 抽签只在「去掉当前这首」之后的 4 个槽位里进行，
        // 落在当前曲目之后的取值要整体后移一位，因此期望值与抽签值并不相等。
        var queue = new PlayQueue(new FixedRandomSource(rawPick)) { RepeatMode = RepeatMode.Shuffle };
        queue.SetTracks(Enumerable.Range(0, 5).Select(TrackAt));
        queue.SetCurrent(TrackAt(current).FilePath);

        var next = queue.NextIndex(autoAdvance: true);

        Assert.NotEqual(current, next);
        Assert.Equal(expected, next);
    }

    [Fact]
    public void 随机播放的上一首同样排除当前曲目()
    {
        var queue = new PlayQueue(new FixedRandomSource(0)) { RepeatMode = RepeatMode.Shuffle };
        queue.SetTracks(Enumerable.Range(0, 4).Select(TrackAt));
        queue.SetCurrent(TrackAt(3).FilePath);

        Assert.Equal(0, queue.PreviousIndex());
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(3, 2)]
    public void 上一首回到前一首(int current, int expected)
    {
        Assert.Equal(expected, QueueWith(5, RepeatMode.Sequential, current).PreviousIndex());
    }

    [Fact]
    public void 顺序播放的上一首在队首时重播第一首()
    {
        // 不回绕：顺序播放里从队首跳到队尾会显得莫名其妙。
        Assert.Equal(0, QueueWith(5, RepeatMode.Sequential, currentIndex: 0).PreviousIndex());
    }

    [Fact]
    public void 列表循环的上一首在队首时绕到末尾()
    {
        Assert.Equal(4, QueueWith(5, RepeatMode.RepeatAll, currentIndex: 0).PreviousIndex());
    }

    [Fact]
    public void 按路径重新定位当前曲目()
    {
        // 曲库刷新后用同一路径重新定位，正在播放的曲目不该因此丢失。
        var queue = QueueWith(3, RepeatMode.Sequential, currentIndex: 0);

        queue.SetTracks(Enumerable.Range(0, 3).Select(TrackAt), TrackAt(2).FilePath);

        Assert.Equal(2, queue.CurrentIndex);
        Assert.Equal(TrackAt(2).FilePath, queue.Current!.FilePath);
    }

    [Fact]
    public void 当前曲目被移出曲库后下标退回未知()
    {
        var queue = QueueWith(3, RepeatMode.Sequential, currentIndex: 2);

        // 只保留前三首里的两首，正在播放的那首已不在其中。
        queue.SetTracks([TrackAt(0), TrackAt(1)], TrackAt(2).FilePath);

        Assert.Equal(-1, queue.CurrentIndex);
        Assert.Null(queue.Current);
    }

    [Fact]
    public void 路径比对忽略大小写()
    {
        // Windows 上路径大小写不敏感，按字节比对会让同一文件被当成两首。
        var queue = new PlayQueue();
        queue.SetTracks([TrackAt(0)]);

        queue.SetCurrent(@"c:\MUSIC\00.MP3");

        Assert.Equal(0, queue.CurrentIndex);
    }

    [Fact]
    public void 空路径不匹配任何曲目()
    {
        var queue = new PlayQueue();
        queue.SetTracks([TrackAt(0)]);

        queue.SetCurrent(string.Empty);

        Assert.Equal(-1, queue.CurrentIndex);
    }

    [Fact]
    public void 清空后不残留当前位置()
    {
        var queue = QueueWith(3, RepeatMode.Sequential, currentIndex: 1);

        queue.Clear();

        Assert.Equal(0, queue.Count);
        Assert.Equal(-1, queue.CurrentIndex);
        Assert.Null(queue.NextIndex(autoAdvance: true));
    }
}
