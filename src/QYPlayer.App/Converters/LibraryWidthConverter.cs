using System.Globalization;
using System.Windows.Data;

namespace QYPlayer.App.Converters;

/// <summary>
/// 左栏容器的宽度：展开时是曲库栏宽度，收起时是那条窄条的宽度。
/// </summary>
/// <remarks>
/// <para>
/// 收起不能只是把左栏宽度改成 0：那样连「展开」的入口也没了，
/// 用户会被卡在收起状态里。因此收起时保留一条窄条，只放一个展开按钮，
/// 这样「收起 / 展开」两个方向都有明确的可点击处。两者恒为互补关系。
/// </para>
/// <para>
/// 返回值刻意是 <see cref="double"/> 而不是 <c>GridLength</c>：XAML 里绑定目标
/// 是容器的 <c>FrameworkElement.Width</c>，它是 double。若返回 GridLength 会因
/// 类型不匹配导致绑定失败，Width 落回 NaN，宽度被内容撑开，340/44 全都不生效。
/// </para>
/// </remarks>
public sealed class LibraryWidthConverter : IValueConverter
{
    /// <summary>展开时曲库栏的宽度。与界面设计稿一致。</summary>
    private const double LibraryWidth = 340;

    /// <summary>收起后窄条的宽度。刚好容得下一个 36px 的按钮加上两侧留白。</summary>
    private const double RailWidth = 44;

    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? RailWidth : LibraryWidth;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
