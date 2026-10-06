namespace QYPlayer.Data;

/// <summary>
/// 本程序集的标志类型。
/// </summary>
/// <remarks>
/// 没有承载逻辑，作用只是让分层结构从第一天起就是完整的——
/// 曲库、设置与日志的实现都落在 <c>QYPlayer.Data</c> 里（见需求文档 9.11 的 M2-2 批次），
/// 后续要加播放列表等实体时也在这里扩展。
/// 保留它是为了避免日后新增工程时再回头调整解决方案与依赖方向。
/// </remarks>
internal static class AssemblyMarker
{
}
