using QYPlayer.Core.Library;
using QYPlayer.Core.Models;
using QYPlayer.Metadata;

namespace QYPlayer.Tests;

/// <summary>
/// 用真实音频跑通「扫描 → 解析元数据」的集成测试。
/// </summary>
/// <remarks>
/// <para>
/// 样本按需求文档 8.5 的约定处理：仓库根目录的真实音频是本地素材而非源码，不入库
/// （几个文件合计上百 MB，且含版权内容）。因此这里先探测素材是否存在——
/// 存在就跑完整断言，不存在则跳过该组用例，保证换台机器克隆下来照样全绿。
/// </para>
/// <para>
/// 样本覆盖中文文件名与中文标签，这两点是最容易出编码问题的地方，
/// 恰恰是纯逻辑用例构造不出来的。
/// </para>
/// </remarks>
public class RealAudioIntegrationTests
{
    /// <summary>仓库根目录：从测试程序集位置往上找到含 <c>QYPlayer.slnx</c> 的那一层。</summary>
    private static string? FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "QYPlayer.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static IReadOnlyList<string> FindSamples()
    {
        var root = FindRepositoryRoot();
        if (root is null)
        {
            return [];
        }

        return Directory
            .GetFiles(root, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
                           || path.EndsWith(".flac", StringComparison.OrdinalIgnoreCase)
                           || path.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase)
                           || path.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)
                           || path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static TheoryData<string> Samples()
    {
        var data = new TheoryData<string>();
        foreach (var sample in FindSamples())
        {
            data.Add(sample);
        }

        // 没有任何样本时给一个空数据源，xunit 会直接跳过整组用例。
        return data;
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public async Task 真实音频能解析出时长与格式(string samplePath)
    {
        var reader = new TagLibMetadataReader();
        var scanner = new LibraryScanner(reader);

        // 样本同处仓库根目录，扫描结果里挑出当前这一个。
        var tracks = await scanner.ScanAsync([Path.GetDirectoryName(samplePath)!]);
        var track = Assert.Single(tracks, t => t.FilePath == samplePath);

        Assert.True(track.FileSize > 0);

        // 时长能解析出来，说明标签容器被正确识别、音频流头也读到了。
        Assert.True(track.Duration > TimeSpan.Zero, $"{Path.GetFileName(samplePath)} 未解析出时长");

        // 格式来自扩展名判定，必须落地到具体格式而不是 Unknown。
        Assert.NotEqual(AudioFormat.Unknown, track.Format);
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public async Task 真实音频的中文标签不出现乱码(string samplePath)
    {
        var reader = new TagLibMetadataReader();

        var metadata = await reader.ReadAsync(samplePath);

        Assert.NotEqual(TrackMetadata.Empty, metadata);
        Assert.True(metadata.Duration > TimeSpan.Zero);

        // 这里不做「必须含中文」的断言：素材标签是否填了中文取决于文件本身。
        // 真正要防的是编码错乱——出现替换字符说明解码链路有问题。
        Assert.DoesNotContain('\uFFFD', metadata.Title);
        Assert.DoesNotContain('\uFFFD', metadata.Artist);
        Assert.DoesNotContain('\uFFFD', metadata.Album);
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public async Task 真实音频的解析结果与快速阶段一致(string samplePath)
    {
        var reader = new TagLibMetadataReader();
        var scanner = new LibraryScanner(reader);

        var files = scanner.ScanDirectories([Path.GetDirectoryName(samplePath)!]);
        var file = Assert.Single(files, f => f.FilePath == samplePath);

        var tracks = await scanner.ScanAsync([Path.GetDirectoryName(samplePath)!]);
        var track = Assert.Single(tracks, t => t.FilePath == samplePath);

        // 两阶段必须对齐：曲目携带的文件事实就是快速阶段读到的值。
        Assert.Equal(file.FileSize, track.FileSize);
        Assert.Equal(file.LastWriteTimeUtc, track.LastWriteTimeUtc);
        Assert.Equal(file.Format, track.Format);
        Assert.True(LibraryScanner.IsUnchanged(track, file));
    }
}
