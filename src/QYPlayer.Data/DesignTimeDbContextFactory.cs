using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace QYPlayer.Data;

/// <summary>
/// 供 <c>dotnet ef</c> 命令行在设计时构造上下文。
/// </summary>
/// <remarks>
/// 没有它，迁移命令就必须指定一个可执行的启动工程并跑起整套宿主；
/// 有了它，<c>dotnet ef migrations add &lt;名称&gt; --project src/QYPlayer.Data</c>
/// 就能直接生成迁移脚本。
///
/// 此处的连接字符串只用于让工具把模型建出来，迁移脚本的内容取自
/// <see cref="QYPlayerDbContext.OnModelCreating"/>，与这个路径无关；
/// 真正生效的库路径由 App 在启动时通过 <c>AppPaths</c> 传入。
/// </remarks>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<QYPlayerDbContext>
{
    public QYPlayerDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<QYPlayerDbContext>()
            .UseSqlite("Data Source=design-time.db")
            .Options;

        return new QYPlayerDbContext(options);
    }
}
