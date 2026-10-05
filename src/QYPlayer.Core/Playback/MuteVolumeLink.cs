namespace QYPlayer.Core.Playback;

/// <summary>
/// 静音与音量滑块的联动状态。
/// </summary>
/// <remarks>
/// 交互约定：按下静音时滑块一并归零，解除静音时滑块回到静音前的位置。
/// 听起来只是两行赋值，但必须处理一个会互相打架的情况——静音后用户把滑块往上拖，
/// 引擎会视为「用户想出声」而自动解除静音；此时若再把滑块拽回静音前的位置，
/// 用户看到的就是「拖了又被弹回去」，比不联动更难受。
/// 因此还原只在「解除静音时滑块仍停在 0」这一种情况下发生：
/// 只要滑块已经被用户动过，就以用户的值优先。
///
/// 放在 Core 而不是界面层，是因为这段判断同时涉及静音标志、当前音量、
/// 静音前的音量三个量，是纯粹的状态推导。抽出来才能被单元测试覆盖，
/// 否则只能靠手点界面去试，回归时也说不清哪条分支被改坏了。
/// </remarks>
public sealed class MuteVolumeLink
{
    /// <summary>静音前的音量，用于解除静音时还原。</summary>
    private int _volumeBeforeMute;

    /// <summary>
    /// 静音前的音量。供界面提示「解除后恢复到多少」。
    /// </summary>
    public int VolumeBeforeMute => _volumeBeforeMute;

    /// <summary>
    /// 在静音状态发生变化时调用，返回滑块应当显示的音量。
    /// </summary>
    /// <param name="muted">变化后的静音状态。</param>
    /// <param name="currentVolume">变化前的当前音量。</param>
    /// <returns>
    /// 滑块应当采用的值；返回 <c>null</c> 表示维持当前值不变。
    /// </returns>
    public int? OnMuteChanged(bool muted, int currentVolume)
    {
        if (muted)
        {
            // 无论当时音量是多少都记下来。若只在非零时记录，上一次静音留下的
            // 旧值会一直存着，解除时就会把滑块还原成一个用户从未设置过的音量。
            _volumeBeforeMute = currentVolume;
            return 0;
        }

        // 滑块已被手动拖动过（不再为 0），说明用户就是想用这个音量，不要覆盖。
        if (currentVolume != 0)
        {
            return null;
        }

        return _volumeBeforeMute;
    }
}
