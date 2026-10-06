using QYPlayer.Core.Models;

namespace QYPlayer.Core.Library;

/// <summary>
/// 曲库的持久化。
/// </summary>
/// <remarks>
/// 界面与扫描器都只认这个接口，具体是 SQLite 还是别的东西由实现决定。
/// 接口定义在领域层、实现放在 <c>QYPlayer.Data</c>，
/// 这样 <c>QYPlayer.Core</c> 不必引用 EF Core。
///
/// 所有方法都是 <b>按路径对齐</b> 的：路径是曲目在磁盘上的身份，
/// 而 <see cref="Track.Id"/> 只是数据库主键，重新扫描会产出新的实例。
/// 若按 Id 对齐，每次扫描都会把整个曲库复制一遍。
/// </remarks>
public interface ILibraryStore
{
    /// <summary>确保库结构就绪（建库、应用迁移）。启动时调用一次。</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>读取全部曲目。</summary>
    Task<IReadOnlyList<Track>> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 新增或更新曲目：路径已存在则原地更新，不存在则插入。
    /// </summary>
    /// <returns>受影响的条数。</returns>
    Task<int> UpsertAsync(
        IReadOnlyCollection<Track> tracks,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除不在 <paramref name="keepPaths"/> 中的曲目，用于「文件已被移走或删除」的情形。
    /// </summary>
    /// <param name="keepPaths">本次扫描到、应当保留的路径全集。</param>
    /// <returns>删除的条数。</returns>
    Task<int> RemoveMissingAsync(
        IReadOnlyCollection<string> keepPaths,
        CancellationToken cancellationToken = default);

    /// <summary>清空曲库。</summary>
    Task ClearAsync(CancellationToken cancellationToken = default);
}
