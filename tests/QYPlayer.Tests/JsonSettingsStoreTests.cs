using System.Text.Json;
using QYPlayer.Core.Playback;
using QYPlayer.Core.Settings;
using QYPlayer.Data;

namespace QYPlayer.Tests;

/// <summary>
/// 设置文件的读写用例，重点在「怎么坏」而不是「正常路径」。
/// </summary>
/// <remarks>
/// 正常读写几乎不可能出问题，出问题的都是边界：文件不存在、内容被手工改坏、
/// 断电留下半截 JSON、旧版本写下的非法枚举值。这些情形一旦处理不当，
/// 表现是「播放器打不开」或「音量每次启动都变」，而用户完全无从联想。
/// </remarks>
public sealed class JsonSettingsStoreTests : IDisposable
{
    private readonly string _root;
    private readonly string _filePath;

    public JsonSettingsStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qyplayer-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _filePath = Path.Combine(_root, "settings.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不影响用例结论。
        }
    }

    [Fact]
    public void 文件不存在时用默认值()
    {
        var store = new JsonSettingsStore(_filePath);

        Assert.Equal(AppSettings.DefaultVolume, store.Current.Volume);
        Assert.Equal(AppTheme.System, store.Current.Theme);
        Assert.Equal(RepeatMode.Sequential, store.Current.RepeatMode);
        Assert.Empty(store.Current.LibraryFolders);

        // 只是读，不该顺手建出一个文件来。
        Assert.False(File.Exists(_filePath));
    }

    [Fact]
    public void Save_之后再读能拿到同样的值()
    {
        var first = new JsonSettingsStore(_filePath);
        first.Current.Volume = 37;
        first.Current.IsMuted = true;
        first.Current.Theme = AppTheme.Dark;
        first.Current.RepeatMode = RepeatMode.Shuffle;
        first.Current.LibraryFolders = [@"C:\Music", @"D:\无损"];
        first.Current.Window.Left = 120;
        first.Current.Window.Top = 80;
        first.Current.Window.Width = 1280;
        first.Current.Window.Height = 720;
        first.Current.Window.IsMaximized = false;
        first.Save();

        var second = new JsonSettingsStore(_filePath);

        Assert.Equal(37, second.Current.Volume);
        Assert.True(second.Current.IsMuted);
        Assert.Equal(AppTheme.Dark, second.Current.Theme);
        Assert.Equal(RepeatMode.Shuffle, second.Current.RepeatMode);
        Assert.Equal([@"C:\Music", @"D:\无损"], second.Current.LibraryFolders);
        Assert.Equal(120, second.Current.Window.Left);
        Assert.Equal(80, second.Current.Window.Top);
        Assert.Equal(1280, second.Current.Window.Width);
        Assert.Equal(720, second.Current.Window.Height);
    }

    [Fact]
    public void Save_不留下临时文件()
    {
        var store = new JsonSettingsStore(_filePath);
        store.Save();

        Assert.True(File.Exists(_filePath));

        // 写临时文件只是手段，写完之后它不该留在磁盘上被用户看到。
        Assert.False(File.Exists(_filePath + ".tmp"));
    }

    [Fact]
    public void 枚举存成字符串名而不是序号()
    {
        var store = new JsonSettingsStore(_filePath);
        store.Current.Theme = AppTheme.Dark;
        store.Current.RepeatMode = RepeatMode.RepeatOne;
        store.Save();

        var json = File.ReadAllText(_filePath);

        // 存序号的话，日后调整枚举成员顺序会把老配置解成别的值。
        Assert.Contains("\"Dark\"", json, StringComparison.Ordinal);
        Assert.Contains("\"RepeatOne\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void 文件是排过版的便于用户自己打开看()
    {
        var store = new JsonSettingsStore(_filePath);
        store.Save();

        var json = File.ReadAllText(_filePath);

        // 设置文件是用户可能手动编辑的，压成一行虽然省字节但不可读。
        Assert.Contains(Environment.NewLine, json, StringComparison.Ordinal);
    }

    [Fact]
    public void 损坏的_JSON_退回默认值并留下备份()
    {
        File.WriteAllText(_filePath, "{ \"Volume\": 40, 这不是合法的 JSON ");

        var store = new JsonSettingsStore(_filePath);

        Assert.Equal(AppSettings.DefaultVolume, store.Current.Volume);

        // 坏文件要改名保留而不是删掉：用户打开它就能看出哪里写错了。
        var backups = Directory.GetFiles(_root, "settings.json.corrupted-*");
        Assert.Single(backups);
        Assert.False(File.Exists(_filePath));
    }

    [Fact]
    public void 空文件当作没配置过()
    {
        File.WriteAllText(_filePath, "   ");

        var store = new JsonSettingsStore(_filePath);

        Assert.Equal(AppSettings.DefaultVolume, store.Current.Volume);

        // 空白内容不是「损坏」，不该被当成损坏去备份。
        Assert.Empty(Directory.GetFiles(_root, "*.corrupted-*"));
    }

    [Fact]
    public void 多出来的未知字段不会让整份设置读不出来()
    {
        File.WriteAllText(_filePath, """
            {
              "Volume": 55,
              "将来才有的字段": { "nested": [1, 2, 3] }
            }
            """);

        var store = new JsonSettingsStore(_filePath);

        // 降级安装（新版本写过文件、老版本再启动）必须还能用，
        // 不能因为不认识某个键就把整份配置丢掉。
        Assert.Equal(55, store.Current.Volume);
    }

    [Fact]
    public void 允许注释与尾随逗号()
    {
        File.WriteAllText(_filePath, """
            {
              // 用户自己加的行内说明
              "Volume": 45,
            }
            """);

        var store = new JsonSettingsStore(_filePath);

        Assert.Equal(45, store.Current.Volume);
    }

    [Fact]
    public void 保存时顺手收口非法值()
    {
        var store = new JsonSettingsStore(_filePath);
        store.Current.Volume = 9999;
        store.Current.LibraryFolders = [@"C:\Music", "  ", @"c:\music", @"D:\Music"];
        store.Save();

        // Save 里先 Normalize：写出去的文件本身就不该含非法值，
        // 否则问题会一路留到下次启动才发现。
        var json = File.ReadAllText(_filePath);
        Assert.Contains("\"Volume\": 100", json, StringComparison.Ordinal);

        var reloaded = new JsonSettingsStore(_filePath);
        Assert.Equal(100, reloaded.Current.Volume);
        Assert.Equal([@"C:\Music", @"D:\Music"], reloaded.Current.LibraryFolders);
    }

    [Fact]
    public void Save_失败不抛异常()
    {
        // 拿目录当文件路径：写入必然失败。
        var store = new JsonSettingsStore(_root);

        // 磁盘满、目录只读都可能发生，但都不构成「必须中断用户操作」的理由。
        store.Save();
    }
}

/// <summary>
/// <see cref="AppSettings.Normalize"/> 的收口规则。
/// </summary>
/// <remarks>
/// 这些用例独立于文件 IO，因为收口逻辑对「文件里读到什么」与
/// 「代码里构造了一个什么」同样适用，而且它是崩溃与诡异行为的最后一道防线。
/// </remarks>
public class AppSettingsTests
{
    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(60, 60)]
    [InlineData(100, 100)]
    [InlineData(101, 100)]
    [InlineData(int.MaxValue, 100)]
    public void 音量被夹到合法区间(int input, int expected)
    {
        var settings = new AppSettings { Volume = input };

        settings.Normalize();

        Assert.Equal(expected, settings.Volume);
    }

    [Fact]
    public void 非法枚举值退回默认()
    {
        var settings = new AppSettings
        {
            Theme = (AppTheme)99,
            RepeatMode = (RepeatMode)(-1),
        };

        settings.Normalize();

        Assert.Equal(AppTheme.System, settings.Theme);
        Assert.Equal(RepeatMode.Sequential, settings.RepeatMode);
    }

    [Fact]
    public void 曲库目录去空白与去重()
    {
        var settings = new AppSettings
        {
            LibraryFolders = [@"C:\Music", "   ", @"c:\music", @"D:\Music", ""],
        };

        settings.Normalize();

        // 大小写不同指的是同一个目录，重复会让扫描跑两遍。
        Assert.Equal([@"C:\Music", @"D:\Music"], settings.LibraryFolders);
    }

    [Fact]
    public void 无意义的窗口尺寸被清掉()
    {
        var settings = new AppSettings
        {
            Window = new WindowPlacement { Left = 100, Top = 50, Width = 0, Height = -1 },
        };

        settings.Normalize();

        // 记录 0 宽没有意义（例如窗口还没显示就被关闭），
        // 与其让下次启动开出一个看不见的窗口，不如当作没记忆过。
        Assert.Null(settings.Window.Width);
        Assert.Null(settings.Window.Height);
        Assert.Equal(100, settings.Window.Left);
        Assert.Equal(50, settings.Window.Top);
    }

    [Fact]
    public void 只有一半的坐标被清掉()
    {
        var settings = new AppSettings
        {
            Window = new WindowPlacement { Left = 100, Width = 800, Height = 600 },
        };

        settings.Normalize();

        // 只有一个方向有值说明位置不完整，恢复出来会跑到奇怪的地方。
        Assert.Null(settings.Window.Left);
        Assert.Null(settings.Window.Top);
        Assert.False(settings.Window.HasPosition);

        // 尺寸是独立的，不该被牵连。
        Assert.Equal(800, settings.Window.Width);
        Assert.True(settings.Window.HasSize);
    }

    [Fact]
    public void Normalize_可以重复调用()
    {
        var settings = new AppSettings { Volume = 200 };

        settings.Normalize();
        settings.Normalize();

        Assert.Equal(100, settings.Volume);
    }

    [Fact]
    public void 缺省的_JSON_对象能补出全部默认值()
    {
        // 反序列化一份空对象等价于「文件里什么都没写」，必须得到可用设置。
        var settings = JsonSerializer.Deserialize<AppSettings>("{}", new JsonSerializerOptions());

        Assert.NotNull(settings);
        settings.Normalize();

        Assert.Equal(AppSettings.DefaultVolume, settings.Volume);
        Assert.Equal(AppTheme.System, settings.Theme);
        Assert.NotNull(settings.Window);
    }
}
