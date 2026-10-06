namespace QYPlayer.Core.Settings;

/// <summary>
/// 界面主题偏好。
/// </summary>
/// <remarks>
/// 显式区分「跟随系统」与「指定深/浅色」是必要的：M1 阶段的界面只跟随系统，
/// 一旦加入手动切换就必须记住用户是否做过选择，否则下次启动无法判断
/// 该继续跟随系统还是沿用上次的手动值。
/// </remarks>
public enum AppTheme
{
    /// <summary>跟随系统。默认值。</summary>
    System = 0,

    Light,

    Dark,
}
