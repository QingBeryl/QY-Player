using Microsoft.EntityFrameworkCore;
using QYPlayer.Core.Models;

namespace QYPlayer.Data;

/// <summary>
/// 曲库的 EF Core 上下文。只映射 <see cref="Track"/> —— 播放列表等实体在后续批次加入，
/// 一次只加一个实体可以让迁移脚本保持可读。
/// </summary>
public sealed class QYPlayerDbContext : DbContext
{
    public QYPlayerDbContext(DbContextOptions<QYPlayerDbContext> options)
        : base(options)
    {
    }

    public DbSet<Track> Tracks => Set<Track>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        var track = modelBuilder.Entity<Track>();

        track.ToTable("Tracks");
        track.HasKey(t => t.Id);

        // Id 是 Guid 的 32 位十六进制字符串，长度固定，显式写出来便于读表结构。
        // 其余字符串不设长度：SQLite 本身不强制 VARCHAR 长度，写了只会给人「会被截断」的错觉，
        // 而标签字段的实际长度由用户的文件决定，不由我们决定。
        track.Property(t => t.Id).HasMaxLength(32);

        // 路径是曲目的身份，必须唯一。用 NOCASE 排序规则而不是默认的 BINARY：
        // Windows 路径大小写不敏感，若按 BINARY 建索引，C:\A.mp3 与 c:\a.mp3
        // 会被当成两条记录，扫描器的去重（OrdinalIgnoreCase）与数据库的唯一性就会不一致，
        // 在「用户改了路径大小写」或「不同来源给出不同大小写」时冒出重复行。
        track.Property(t => t.FilePath)
            .IsRequired()
            .UseCollation("NOCASE");

        track.HasIndex(t => t.FilePath).IsUnique();

        // 时长与时间戳都存 Ticks（INTEGER），不存 SQLite 默认的 TEXT：
        //
        // 1. 存 TEXT 时 ORDER BY 走的是字符串比较。时长写成 "00:03:07" 这类文本，
        //    按字符串排序的结果与按时间排序完全不同，"00:10:00" 会排在 "00:09:00" 之前。
        //    M2-4 的列表要按列排序，存 Ticks 才能让 SQL 端的排序是对的。
        // 2. DateTime 存 TEXT 会丢掉 Kind，读回来是 Unspecified。8.2 的增量比对靠
        //    「文件大小 + 最后写入时间」判断文件是否变过，存 Ticks 并显式还原为 Utc，
        //    可以保证写进去的值与读出来的值逐位相等，不受时区与格式影响。
        track.Property(t => t.Duration)
            .HasConversion(
                value => value.Ticks,
                ticks => TimeSpan.FromTicks(ticks));

        track.Property(t => t.LastWriteTimeUtc)
            .HasConversion(
                value => value.Ticks,
                ticks => new DateTime(ticks, DateTimeKind.Utc));

        // 格式存枚举的字符串名而不是序号：表是给人和迁移脚本读的，
        // 数字序号一旦枚举顺序被调整（例如在中间插入新格式）就会指错。
        track.Property(t => t.Format)
            .HasConversion<string>()
            .HasMaxLength(16);

        // FileName / DisplayTitle / DisplayArtist / DisplayAlbum 是只读计算属性，
        // 不在此处显式忽略也不会有列——EF Core 只映射有 setter 或有后备字段的属性。
        // Track 上仍然标了 [NotMapped]，作用是把「这些不是列」写成显式约定，
        // 避免日后有人给它们加上 setter 时静默多出几列。
    }
}
