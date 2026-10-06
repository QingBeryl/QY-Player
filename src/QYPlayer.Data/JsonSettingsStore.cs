using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using QYPlayer.Core.Settings;

namespace QYPlayer.Data;

/// <summary>
/// 把设置存成独立 JSON 文件，对应需求文档 8.1 的结论。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么写临时文件再改名。</b> 直接覆盖原文件时，若在 <c>File.WriteAllText</c>
/// 执行到一半断电或进程被杀，留下的就是半截 JSON。设置文件恰恰是在「窗口关闭」这种
/// 随时可能被系统掐断的时机写入的，所以「先写 .tmp 再原子替换」不是过度设计。
/// </para>
/// <para>
/// <b>为什么损坏时退回默认而不是抛异常。</b> 设置是辅助数据，一个坏了的设置文件
/// 不该让播放器启动不了。回退的同时把坏文件备份改名保留，用户和排查者都还能看到现场。
/// </para>
/// </remarks>
public sealed class JsonSettingsStore : ISettingsStore
{
    /// <summary>
    /// 序列化选项。
    /// </summary>
    /// <remarks>
    /// <c>WriteIndented</c> 是有意的：设置文件是用户可能自己打开看和改的，
    /// 排成一行虽然更省几个字节，但完全不可读。
    /// <c>JsonStringEnumConverter</c> 让枚举存字符串名，与曲库里格式字段的处理一致——
    /// 存序号的话，日后调整枚举顺序会把老配置解成别的值。
    /// <c>UnmappedMemberHandling.Skip</c> 保证新增字段后旧版本程序读到新文件
    /// 不会因为不认识某个键而整份失败。
    /// </remarks>
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _filePath;
    private readonly ILogger<JsonSettingsStore>? _logger;

    public JsonSettingsStore(string filePath, ILogger<JsonSettingsStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        _filePath = filePath;
        _logger = logger;

        Current = Load();
    }

    public AppSettings Current { get; }

    public void Save()
    {
        try
        {
            Current.Normalize();

            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(Current, SerializerOptions);

            // 先写临时文件，再整体改名覆盖。文件系统保证同分区内改名是原子的，
            // 于是「用户看到文件」与「文件内容完整」这两件事同时成立。
            var tempPath = _filePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _filePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException
                                      or UnauthorizedAccessException
                                      or NotSupportedException
                                      or PathTooLongException)
        {
            // 保存失败只记日志：磁盘满、目录只读、文件被占用都可能发生，
            // 但这些都不构成「必须中断用户操作」的理由。
            _logger?.LogWarning(ex, "保存设置失败：{Path}", _filePath);
        }
    }

    /// <summary>
    /// 读取设置文件。任何失败都退回默认值，绝不让调用方处理异常。
    /// </summary>
    private AppSettings Load()
    {
        if (!File.Exists(_filePath))
        {
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new AppSettings();
            }

            var settings = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions)
                           ?? new AppSettings();

            // 文件里的值不可信（用户可编辑、可能来自旧版本），统一收口一次。
            settings.Normalize();
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "设置文件无法解析，已退回默认值：{Path}", _filePath);

            // 把坏文件挪到一边而不是删掉：用户打开它就能看出哪里写错了，
            // 删掉则连线索都没有。备份失败也不再纠缠。
            TryBackupCorruptedFile();

            return new AppSettings();
        }
    }

    private void TryBackupCorruptedFile()
    {
        try
        {
            var backup = $"{_filePath}.corrupted-{DateTime.Now:yyyyMMddHHmmss}";
            File.Move(_filePath, backup, overwrite: true);
            _logger?.LogInformation("损坏的设置文件已备份为 {Path}", backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 备份失败不影响本次启动。
        }
    }
}
