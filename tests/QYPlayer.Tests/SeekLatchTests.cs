using QYPlayer.Core.Playback;

namespace QYPlayer.Tests;

/// <summary>
/// 跳转后的位置回传抑制。
/// </summary>
/// <remarks>
/// 锁住的重点是「点击跳转为什么时而不灵」：提交之后引擎可能还回传一条
/// 跳转生效前的旧位置，若照单全收就会把进度条拽回原处。
/// 这里覆盖三条分支：拦掉旧回包、到位后恢复跟随、长时间不生效时自动放开。
/// </remarks>
public class SeekLatchTests
{
    [Fact]
    public void 未上闩时一律采用()
    {
        var latch = new SeekLatch();

        Assert.True(latch.ShouldAccept(12.5));
        Assert.False(latch.IsArmed);
    }

    [Fact]
    public void 上闩后拦掉远离目标的旧回包()
    {
        var latch = new SeekLatch();

        // 跳到 120 秒，紧接着回来的却仍是跳转前的旧位置。
        latch.Arm(120);

        Assert.False(latch.ShouldAccept(3.5));
        Assert.True(latch.IsArmed);
    }

    [Fact]
    public void 回包走到目标附近即恢复跟随()
    {
        var latch = new SeekLatch();

        latch.Arm(120);

        // 引擎真的跳过去了，位置落在容差内，应当采用并自动撤闩。
        Assert.True(latch.ShouldAccept(120.4));
        Assert.False(latch.IsArmed);

        // 撤闩之后继续正常跟随。
        Assert.True(latch.ShouldAccept(300));
    }

    [Fact]
    public void 容差边界内外的判定不同()
    {
        var latch = new SeekLatch();
        latch.Arm(100);

        // 恰好落在容差边缘：采用（判定为已到位）。
        Assert.True(latch.ShouldAccept(100 + SeekLatch.ToleranceSeconds));

        latch.Arm(100);

        // 略超容差：仍按旧回包拦掉。
        Assert.False(latch.ShouldAccept(100 + SeekLatch.ToleranceSeconds + 0.01));
    }

    [Fact]
    public void 迟迟不生效时自动放开避免界面冻住()
    {
        var latch = new SeekLatch();
        latch.Arm(500);

        // 模拟引擎始终没跳过去：连续收到大量偏离目标的位置。
        // 前 MaxHoldCount - 1 次应当被拦住。
        for (var i = 0; i < SeekLatch.MaxHoldCount - 1; i++)
        {
            Assert.False(latch.ShouldAccept(10));
        }

        // 达到上限后必须自行放开，否则进度条会永久停止更新。
        Assert.True(latch.ShouldAccept(10));
        Assert.False(latch.IsArmed);
    }

    [Fact]
    public void 重新上闩会重置计数()
    {
        var latch = new SeekLatch();

        latch.Arm(500);
        for (var i = 0; i < SeekLatch.MaxHoldCount - 1; i++)
        {
            latch.ShouldAccept(10);
        }

        // 用户又点了一次别处：计数应当从头开始，新目标同样受保护。
        latch.Arm(800);

        Assert.False(latch.ShouldAccept(10));
        Assert.True(latch.IsArmed);
    }

    [Fact]
    public void 手动重置后立即恢复跟随()
    {
        var latch = new SeekLatch();
        latch.Arm(200);

        latch.Reset();

        Assert.False(latch.IsArmed);
        Assert.True(latch.ShouldAccept(7));
    }
}
