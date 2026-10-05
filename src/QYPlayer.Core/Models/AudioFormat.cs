namespace QYPlayer.Core.Models;

/// <summary>
/// 音频容器格式。前一组是通用格式，后一组是各平台的加密格式。
/// </summary>
public enum AudioFormat
{
    Unknown = 0,

    Mp3,
    Flac,
    Wav,
    Aac,
    Ogg,
    Opus,
    Wma,
    Ape,
    Alac,

    Ncm,
    Kgm,
    Vpr,
    Kwm,
}
