namespace QYPlayer.Core.Playback;

/// <summary>
/// 跳转后的位置回传抑制。
/// </summary>
/// <remarks>
/// 解决的问题：用户点击进度条跳转时，界面已经算好新位置，但引擎的位置回调
/// 是异步投递的，队列里可能还压着一条「跳转生效前」的旧位置。它一旦在提交之后
/// 才被处理，就会把刚跳过去的位置又拽回原处，表现成「点了没反应」，
/// 且是否踩中取决于这几十毫秒窗口内是否恰有一条旧回包，因此时灵时不灵。
///
/// 对策是给提交后的这段时间上一个闩：引擎回传的位置若离目标还远，
/// 就判定它是跳转前的旧值，暂不采信；等引擎真的走到目标附近，再恢复跟随。
///
/// 兜底：若引擎因为不支持跳转等原因始终走不到目标附近，闩必须自己放开，
/// 否则进度条会永久冻住——比跳转不灵更糟。因此限制最多抑制若干次回传。
///
/// 放在 Core 而不是界面层，是因为这是一段纯粹的状态判断，
/// 抽出来才能被单元测试覆盖；否则这种「和异步回包赛跑」的逻辑
/// 只能靠反复手点去碰，回归时也说不清哪条分支被改坏了。
/// </remarks>
public sealed class SeekLatch
{
    /// <summary>
    /// 回传位置与目标相差多少秒以内算「已经到位」。
    /// </summary>
    /// <remarks>
    /// 引擎的位置回调本身有粒度（实践中约每 250ms 一次），不可能正好落在
    /// 点击的那个毫秒上，因此需要一段容差。取 1 秒：既远大于回调间隔，
    /// 又小到不会把明显偏掉的旧位置误判成「已到位」。
    /// </remarks>
    public const double ToleranceSeconds = 1.0;

    /// <summary>
    /// 最多连续抑制多少次回传，超过就判定跳转没能生效并自行放开。
    /// </summary>
    /// <remarks>
    /// 按引擎约每秒 4 次回传估算，20 次约合 5 秒，足够覆盖一次正常跳转的
    /// 生效时间，又不至于让界面在异常情况下长时间僵住。
    /// </remarks>
    public const int MaxHoldCount = 20;

    private double? _target;
    private int _held;

    /// <summary>是否正处于抑制状态。</summary>
    public bool IsArmed => _target is not null;

    /// <summary>记下跳转目标，开始抑制位置回传。</summary>
    /// <param name="targetSeconds">要跳转到的位置，单位秒。</param>
    public void Arm(double targetSeconds)
    {
        _target = targetSeconds;
        _held = 0;
    }

    /// <summary>解除抑制，恢复正常跟随。</summary>
    public void Reset()
    {
        _target = null;
        _held = 0;
    }

    /// <summary>
    /// 判断一条引擎回传的位置是否可以采用。
    /// </summary>
    /// <param name="positionSeconds">引擎回传的位置，单位秒。</param>
    /// <returns>
    /// <c>true</c> 表示可以采用；<c>false</c> 表示这是跳转生效前的旧位置，应当忽略。
    /// </returns>
    public bool ShouldAccept(double positionSeconds)
    {
        if (_target is not double target)
        {
            return true;
        }

        // 走到目标附近，说明跳转已经生效，撤掉闩，之后恢复正常跟随。
        if (Math.Abs(positionSeconds - target) <= ToleranceSeconds)
        {
            Reset();
            return true;
        }

        // 一直对不上，判定跳转没能生效，放开闩避免界面冻住。
        if (++_held >= MaxHoldCount)
        {
            Reset();
            return true;
        }

        return false;
    }
}
