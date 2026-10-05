using QYPlayer.Core.Models;
using QYPlayer.Plugin.Abstractions;

namespace QYPlayer.Core.Plugins;

/// <summary>
/// 插件宿主。负责在启动时发现并加载解密插件，并对外提供已启用的插件集合。
/// </summary>
/// <remarks>
/// 关于安全：只扫描主程序目录下固定的 plugins 子目录，不加载任意路径的程序集。
/// 插件加载失败只记录并跳过，不影响主程序启动。方案 B 下默认不加载任何插件，
/// 因此未做任何配置的安装与「不带解密能力」的播放器行为完全一致。
/// </remarks>
public interface IPluginHost
{
    /// <summary>已成功加载且通过校验的插件。</summary>
    IReadOnlyList<IDecryptorPlugin> LoadedPlugins { get; }

    /// <summary>加载失败的插件信息，用于在设置页提示用户。键为文件路径，值为失败原因。</summary>
    IReadOnlyDictionary<string, string> LoadFailures { get; }

    /// <summary>插件目录的绝对路径。目录不存在时不会自动创建。</summary>
    string PluginDirectory { get; }

    /// <summary>
    /// 扫描插件目录并加载全部可用插件。重复调用不会重复加载同一插件。
    /// </summary>
    void LoadPlugins();
}
