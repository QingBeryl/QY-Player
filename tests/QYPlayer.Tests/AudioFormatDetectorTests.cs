using QYPlayer.Core.Models;
using QYPlayer.Core.Sources;

namespace QYPlayer.Tests;

/// <summary>
/// 格式判定是解密插件分流的第一道关口：判错会让普通文件走解密路径，
/// 或者让加密文件被直接丢给引擎并产生难以理解的报错，因此单独覆盖。
/// </summary>
public class AudioFormatDetectorTests
{
    [Theory]
    [InlineData("song.mp3", AudioFormat.Mp3)]
    [InlineData("song.FLAC", AudioFormat.Flac)]
    [InlineData("song.Mp3", AudioFormat.Mp3)]
    [InlineData("song.m4a", AudioFormat.Aac)]
    [InlineData("song.wma", AudioFormat.Wma)]
    [InlineData("song.ape", AudioFormat.Ape)]
    [InlineData("song.ncm", AudioFormat.Ncm)]
    [InlineData("song.kgm", AudioFormat.Kgm)]
    [InlineData("song.kgma", AudioFormat.Kgm)]
    [InlineData("song.kwm", AudioFormat.Kwm)]
    [InlineData("song.vpr", AudioFormat.Vpr)]
    [InlineData("song.txt", AudioFormat.Unknown)]
    [InlineData("noextension", AudioFormat.Unknown)]
    public void FromPath_识别扩展名(string fileName, AudioFormat expected)
    {
        Assert.Equal(expected, AudioFormatDetector.FromPath(fileName));
    }

    [Fact]
    public void IsNative_加密格式不属于原生格式()
    {
        Assert.True(AudioFormatDetector.IsNative(AudioFormat.Mp3));
        Assert.False(AudioFormatDetector.IsNative(AudioFormat.Ncm));

        Assert.True(AudioFormatDetector.IsEncrypted(AudioFormat.Ncm));
        Assert.False(AudioFormatDetector.IsEncrypted(AudioFormat.Mp3));
    }

    [Theory]
    [InlineData("flac", AudioFormat.Flac)]
    [InlineData(".flac", AudioFormat.Flac)]
    [InlineData("WAV", AudioFormat.Wav)]
    public void FromName_供插件回传真实格式(string formatName, AudioFormat expected)
    {
        Assert.Equal(expected, AudioFormatDetector.FromName(formatName));
    }

    [Fact]
    public void NormalizeExtension_统一为小写带点()
    {
        Assert.Equal(".mp3", AudioFormatDetector.NormalizeExtension(@"C:\Music\Track.MP3"));
        Assert.Equal(string.Empty, AudioFormatDetector.NormalizeExtension("noextension"));
    }
}
