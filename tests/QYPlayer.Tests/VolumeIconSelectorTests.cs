using QYPlayer.Core.Playback;

namespace QYPlayer.Tests;

/// <summary>
/// 音量图标的档位判定。
/// </summary>
/// <remarks>
/// 这段逻辑曾经内联在界面的 XAML 触发器里，结果是图标恒定不变：
/// 元素上的本地值与样式触发器冲突，触发器被压住永不生效。
/// 改成纯函数后就能在此锁住行为，避免再退回去。
/// </remarks>
public class VolumeIconSelectorTests
{
    [Theory]
    [InlineData(100, VolumeIconLevel.High)]
    [InlineData(67, VolumeIconLevel.High)]
    [InlineData(66, VolumeIconLevel.Medium)]
    [InlineData(34, VolumeIconLevel.Medium)]
    [InlineData(33, VolumeIconLevel.Low)]
    [InlineData(1, VolumeIconLevel.Low)]
    public void Select_按音量分档(int volume, VolumeIconLevel expected)
    {
        Assert.Equal(expected, VolumeIconSelector.Select(volume, isMuted: false));
    }

    [Fact]
    public void Select_静音优先于音量()
    {
        // 静音时音量值通常仍非零（便于恢复），图标必须显示为静音。
        Assert.Equal(VolumeIconLevel.Muted, VolumeIconSelector.Select(80, isMuted: true));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Select_音量为零等同于静音(int volume)
    {
        // 没按静音但音量拉到 0，听感上就是没声音，图标不该还显示有声。
        Assert.Equal(VolumeIconLevel.Muted, VolumeIconSelector.Select(volume, isMuted: false));
    }
}
