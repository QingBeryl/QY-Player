using System.Security.Cryptography;
using System.Text;

namespace QYPlayer.Metadata;

/// <summary>
/// 基于内容哈希的封面缓存。相同封面只落盘一次，文件名即内容哈希。
/// </summary>
public sealed class CoverCache : ICoverCache
{
    private readonly string _cacheDirectory;

    public CoverCache(string cacheDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);

        _cacheDirectory = cacheDirectory;
    }

    public string CacheDirectory => _cacheDirectory;

    public string? Save(byte[]? data, string? mimeType)
    {
        if (data is null || data.Length == 0)
        {
            return null;
        }

        var extension = ResolveExtension(mimeType, data);
        var hash = Convert.ToHexString(SHA256.HashData(data))[..32];
        var path = Path.Combine(_cacheDirectory, hash + extension);

        // 已经缓存过同内容的封面，直接复用。
        if (File.Exists(path))
        {
            return path;
        }

        Directory.CreateDirectory(_cacheDirectory);

        // 先写临时文件再改名，避免并发或异常中断留下半截文件。
        var tempPath = path + ".tmp";
        try
        {
            File.WriteAllBytes(tempPath, data);
            File.Move(tempPath, path, overwrite: true);
        }
        catch (IOException)
        {
            // 另一个线程已经写了同名文件，视为成功。
            if (!File.Exists(path))
            {
                throw;
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                TryDelete(tempPath);
            }
        }

        return path;
    }

    /// <summary>
    /// MIME 缺失或不可信时，回退到按文件头魔数判断。
    /// 这一步是必要的：部分软件的标签把 image/png 写成 image/jpg。
    /// </summary>
    private static string ResolveExtension(string? mimeType, byte[] data)
    {
        if (LooksLikePng(data))
        {
            return ".png";
        }

        if (LooksLikeJpeg(data))
        {
            return ".jpg";
        }

        // 无法从内容判断时才信任 MIME。
        var normalized = mimeType?.ToLowerInvariant();
        return normalized switch
        {
            "image/png" => ".png",
            "image/bmp" => ".bmp",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            _ => ".jpg",
        };
    }

    private static bool LooksLikePng(byte[] data) =>
        data.Length >= 8
        && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47;

    private static bool LooksLikeJpeg(byte[] data) =>
        data.Length >= 3
        && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // 清理临时文件失败不影响主流程。
        }
    }
}
