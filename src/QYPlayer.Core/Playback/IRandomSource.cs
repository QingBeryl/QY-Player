namespace QYPlayer.Core.Playback;

/// <summary>
/// 随机数来源。
/// </summary>
/// <remarks>
/// 抽成接口只为了可测：随机播放的顺序没法断言，但「不重复抽到当前这首」
/// 「只有一首时原地重播」这类规则必须能被测试锁住，
/// 因此把「随机」这个不可控因素缩小到一个可替换的一维方法上。
/// </remarks>
public interface IRandomSource
{
    /// <summary>返回一个不小于 0 且小于 <paramref name="maxExclusive"/> 的整数。</summary>
    int Next(int maxExclusive);
}

/// <summary>基于 <see cref="Random.Shared"/> 的默认实现。</summary>
public sealed class SystemRandomSource : IRandomSource
{
    /// <inheritdoc />
    public int Next(int maxExclusive) =>
        maxExclusive <= 0 ? 0 : Random.Shared.Next(maxExclusive);
}
