using System.Globalization;
using System.Windows;
using System.Windows.Data;
using QYPlayer.Core.Models;

namespace QYPlayer.App.Converters;

/// <summary>
/// 判断列表项是不是「当前正在播放的那一首」，决定要不要显示那条律动指示。
/// </summary>
/// <remarks>
/// <para>
/// 为什么需要一个多值转换器：这件事实由两个来源共同决定——
/// 「这一行是不是当前曲目」要拿行自己的数据与 ViewModel 的当前曲目比，
/// 而「是否真的在响」在 ViewModel 上。XAML 的 DataTrigger 只能拿一个绑定与常量比较，
/// 表达不了「两个绑定相等」这种条件，因此收敛到这里。
/// </para>
/// <para>
/// 按 <see cref="Track.FilePath"/> 比较而不是引用相等：
/// 同一首歌可能同时存在于曲库列表与外部队列里，它们是两个不同的实例，
/// 用引用比较会让「从外部打开的歌」在列表里认不出自己。
/// </para>
/// <para>
/// 暂停与停止时返回 <see cref="Visibility.Collapsed"/>：条纹还在动却没有声音，
/// 会让人以为还在播。收起比留在原地更不容易误读。
/// </para>
/// </remarks>
public sealed class NowPlayingIndicatorConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        // 约定顺序：[0] 列表项，[1] 当前曲目，[2] 是否正在播放。
        if (values.Length < 3
            || values[0] is not Track item
            || values[1] is not Track current
            || values[2] is not bool isPlaying)
        {
            return Visibility.Collapsed;
        }

        var isSameTrack = string.Equals(item.FilePath, current.FilePath, StringComparison.OrdinalIgnoreCase);

        return isSameTrack && isPlaying ? Visibility.Visible : Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
