using QYPlayer.Core.Models;

namespace QYPlayer.Core.Sources;

/// <summary>
/// 按扩展名判定格式。快速扫描只用扩展名，文件头的魔数校验放到「准备播放」与「解析元数据」两个已注定要读文件的时机（见 8.1）。
/// </summary>
public static class AudioFormatDetector
{
    private static readonly Dictionary<string, AudioFormat> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp3"] = AudioFormat.Mp3,
        [".flac"] = AudioFormat.Flac,
        [".wav"] = AudioFormat.Wav,
        [".m4a"] = AudioFormat.Aac,
        [".aac"] = AudioFormat.Aac,
        [".ogg"] = AudioFormat.Ogg,
        [".opus"] = AudioFormat.Opus,
        [".wma"] = AudioFormat.Wma,
        [".ape"] = AudioFormat.Ape,
        [".alac"] = AudioFormat.Alac,

        [".ncm"] = AudioFormat.Ncm,
        [".kgm"] = AudioFormat.Kgm,
        [".kgma"] = AudioFormat.Kgm,
        [".vpr"] = AudioFormat.Vpr,
        [".kwm"] = AudioFormat.Kwm,
    };

    /// <summary>通用播放器可直接读取的格式。加密格式不在此列。</summary>
    private static readonly HashSet<AudioFormat> NativeFormats =
    [
        AudioFormat.Mp3, AudioFormat.Flac, AudioFormat.Wav, AudioFormat.Aac,
        AudioFormat.Ogg, AudioFormat.Opus, AudioFormat.Wma, AudioFormat.Ape, AudioFormat.Alac,
    ];

    /// <summary>加密格式。这些格式必须由解密插件处理。</summary>
    private static readonly HashSet<AudioFormat> EncryptedFormats =
    [
        AudioFormat.Ncm, AudioFormat.Kgm, AudioFormat.Vpr, AudioFormat.Kwm,
    ];

    public static AudioFormat FromPath(string filePath) =>
        ByExtension.TryGetValue(Path.GetExtension(filePath), out var format)
            ? format
            : AudioFormat.Unknown;

    /// <summary>
    /// 按格式名（不含点，例如 "flac"）判定格式。
    /// 供解密插件回传解密后的真实格式时使用。
    /// </summary>
    public static AudioFormat FromName(string formatName)
    {
        if (string.IsNullOrWhiteSpace(formatName))
        {
            return AudioFormat.Unknown;
        }

        var normalized = formatName.StartsWith('.') ? formatName : "." + formatName;
        return FromPath("x" + normalized);
    }

    /// <summary>
    /// 返回扩展名：小写且带点。文件没有扩展名时返回空字符串。
    /// </summary>
    public static string NormalizeExtension(string filePath) =>
        Path.GetExtension(filePath).ToLowerInvariant();

    /// <summary>该格式是否可直接交给播放引擎读取，无需解密。</summary>
    public static bool IsNative(AudioFormat format) => NativeFormats.Contains(format);

    /// <summary>该格式是否为加密容器。</summary>
    public static bool IsEncrypted(AudioFormat format) => EncryptedFormats.Contains(format);

    /// <summary>引擎可识别的所有扩展名，用于文件对话框与扫描筛选。</summary>
    public static IReadOnlyCollection<string> SupportedExtensions => ByExtension.Keys;
}
