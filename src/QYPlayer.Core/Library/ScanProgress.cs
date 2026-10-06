namespace QYPlayer.Core.Library;

/// <summary>
/// 慢速阶段的进度快照。只在「解析完一个文件」时报告一次，
/// 让界面能显示「已解析 / 总数」，而不必自己数文件。
/// </summary>
/// <param name="Completed">已完成解析的文件数。</param>
/// <param name="Total">本批待解析的文件总数。</param>
/// <param name="CurrentFile">刚刚解析完的文件路径；初始报告时为 null。</param>
public sealed record ScanProgress(int Completed, int Total, string? CurrentFile = null)
{
    /// <summary>完成比例，取值 0–1。总数为 0 时返回 0。</summary>
    public double Fraction => Total <= 0 ? 0 : (double)Completed / Total;
}
