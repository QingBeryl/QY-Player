namespace QYPlayer.Core.Settings;

/// <summary>
/// 设置的读取与保存。
/// </summary>
/// <remarks>
/// 接口留在领域层、实现（JSON 文件）放在 <c>QYPlayer.Data</c>，
/// 与 <c>IMetadataReader</c>、<c>IPlaybackService</c> 一致：
/// 需要设置的代码（ViewModel、启动流程）不应该被迫依赖某一种存储格式。
/// </remarks>
public interface ISettingsStore
{
    /// <summary>当前设置。实例常驻，调用方直接改属性即可。</summary>
    AppSettings Current { get; }

    /// <summary>
    /// 把 <see cref="Current"/> 写回存储。
    /// </summary>
    /// <remarks>
    /// 同步而非异步：设置文件只有几百字节，写成异步只会让调用方
    /// （音量变化、窗口关闭）多出一层 await，而两者本身没有等待的价值。
    /// 实现必须保证写入的原子性与异常安全——保存失败不能把程序带崩，
    /// 更不能把原来那份好文件覆盖坏。
    /// </remarks>
    void Save();
}
