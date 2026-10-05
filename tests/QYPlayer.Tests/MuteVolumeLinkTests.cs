using QYPlayer.Core.Playback;

namespace QYPlayer.Tests;

/// <summary>
/// 静音与音量滑块的联动：静音时归零，解除时还原。
/// </summary>
/// <remarks>
/// 这里锁住的重点不是「归零」和「还原」本身，而是那条容易被忽略的分支：
/// 静音后用户把滑块拖起来，引擎会顺带解除静音，此时绝不能再把滑块拽回原位，
/// 否则用户的拖动会被无声地撤销，手感比不联动更差。
/// </remarks>
public class MuteVolumeLinkTests
{
    [Fact]
    public void 静音时滑块归零()
    {
        var link = new MuteVolumeLink();

        Assert.Equal(0, link.OnMuteChanged(muted: true, currentVolume: 60));
    }

    [Fact]
    public void 解除静音时回到静音前的音量()
    {
        var link = new MuteVolumeLink();

        var muted = link.OnMuteChanged(muted: true, currentVolume: 60);
        Assert.Equal(0, muted);

        // 静音期间滑块停在 0，解除时应当还原成 60。
        Assert.Equal(60, link.OnMuteChanged(muted: false, currentVolume: 0));
    }

    [Fact]
    public void 静音前的音量会被记录下来供提示文字使用()
    {
        var link = new MuteVolumeLink();

        link.OnMuteChanged(muted: true, currentVolume: 73);

        Assert.Equal(73, link.VolumeBeforeMute);
    }

    [Fact]
    public void 静音时音量为零也照常记录()
    {
        var link = new MuteVolumeLink();

        // 若只在非零时记录，上一次静音留下的旧值会一直存着，
        // 解除时就会还原成一个用户从未设置过的音量。
        link.OnMuteChanged(muted: true, currentVolume: 50);
        link.OnMuteChanged(muted: true, currentVolume: 0);

        Assert.Equal(0, link.VolumeBeforeMute);
    }

    [Fact]
    public void 用户已拖动滑块时不被还原覆盖()
    {
        var link = new MuteVolumeLink();

        // 静音 → 用户把滑块从 0 拖到 35（引擎此时会自动解除静音）。
        link.OnMuteChanged(muted: true, currentVolume: 80);

        // 返回 null 表示维持当前值：以用户的拖动为准，不要弹回 80。
        Assert.Null(link.OnMuteChanged(muted: false, currentVolume: 35));
    }

    [Fact]
    public void 连续两次静音各记各的原音量()
    {
        var link = new MuteVolumeLink();

        link.OnMuteChanged(muted: true, currentVolume: 40);
        link.OnMuteChanged(muted: false, currentVolume: 0);

        // 用户改成 25 之后又静音，此时该记住的是 25 而不是 40。
        link.OnMuteChanged(muted: true, currentVolume: 25);
        Assert.Equal(25, link.VolumeBeforeMute);

        Assert.Equal(25, link.OnMuteChanged(muted: false, currentVolume: 0));
    }
}
