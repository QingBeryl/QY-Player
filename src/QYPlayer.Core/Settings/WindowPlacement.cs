namespace QYPlayer.Core.Settings;

/// <summary>
/// 上次关闭时的窗口位置与尺寸。
/// </summary>
/// <remarks>
/// 四个值都用可空类型而不是直接给默认数值：<c>double</c> 的 <see cref="double.NaN"/>
/// 在 JSON 里是非法的，写成数字又无法表达「从未记录过」。
/// 于是「为 null」既表示还没记忆过，也让界面可以退回系统默认摆放。
/// </remarks>
public sealed class WindowPlacement
{
    public double? Left { get; set; }

    public double? Top { get; set; }

    public double? Width { get; set; }

    public double? Height { get; set; }

    public bool IsMaximized { get; set; }

    /// <summary>
    /// 是否具备可用的位置。恢复窗口位置要求横向坐标成对出现，
    /// 只有一半数据的记录（例如早期版本写的）应当被忽略。
    /// </summary>
    public bool HasPosition => Left is not null && Top is not null;

    /// <summary>是否具备可用的尺寸。</summary>
    public bool HasSize => Width > 0 && Height > 0;
}
