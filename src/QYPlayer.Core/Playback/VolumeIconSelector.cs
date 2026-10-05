namespace QYPlayer.Core.Playback;

/// <summary>
/// 音量图标的档位。只描述「该显示几格」，与具体字体和码位无关。
/// </summary>
/// <remarks>
/// 把判定放在 Core 而不是界面层，是为了让这段「静音优先、再按音量分档」的
/// 组合逻辑可以被单元测试覆盖。界面层只负责把档位映射成实际字形，
/// 换字体或换图标集时不必回头改判定规则。
/// </remarks>
public enum VolumeIconLevel
{
    /// <summary>静音，或音量已归零：显示带叉的喇叭。</summary>
    Muted = 0,

    /// <summary>低音量：一格。</summary>
    Low,

    /// <summary>中等音量：两格。</summary>
    Medium,

    /// <summary>高音量：三格。</summary>
    High,
}

/// <summary>
/// 由静音状态与音量值推导出应显示的音量图标档位。
/// </summary>
public static class VolumeIconSelector
{
    /// <summary>低音量与中等音量之间的分界（含上界判断，取不到本值）。</summary>
    private const int MediumThreshold = 34;

    /// <summary>中等音量与高音量之间的分界。</summary>
    private const int HighThreshold = 67;

    /// <summary>
    /// 选择音量图标档位。
    /// </summary>
    /// <param name="volume">音量，0–100。超出范围会被夹取，避免异常输入。</param>
    /// <param name="isMuted">是否静音。静音优先于音量，因为静音时音量值可能仍非零。</param>
    public static VolumeIconLevel Select(int volume, bool isMuted)
    {
        if (isMuted)
        {
            return VolumeIconLevel.Muted;
        }

        // 音量为 0 时即使没按静音，听感上也等于静音，图标应保持一致。
        if (volume <= 0)
        {
            return VolumeIconLevel.Muted;
        }

        return volume switch
        {
            < MediumThreshold => VolumeIconLevel.Low,
            < HighThreshold => VolumeIconLevel.Medium,
            _ => VolumeIconLevel.High,
        };
    }
}
