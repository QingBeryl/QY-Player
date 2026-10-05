using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;
using QYPlayer.Plugin.Abstractions;

namespace QYPlayer.Core.Plugins;

/// <summary>
/// 插件宿主的默认实现：扫描固定目录、按契约校验、隔离加载。
/// </summary>
/// <remarks>
/// 几点刻意的取舍：
/// 一是只扫描 <see cref="PluginDirectory"/> 这一个目录，不接受任意路径，避免加载到不可信程序集。
/// 二是每个插件用独立的 <see cref="AssemblyLoadContext"/> 加载，插件之间的依赖冲突不会互相影响，
/// 插件崩溃也不会带走主程序。
/// 三是任何加载失败都只记录、不抛出：解密是可选能力，缺了它播放器照常工作。
/// </remarks>
public sealed class PluginHost : IPluginHost
{
    private readonly ILogger<PluginHost>? _logger;
    private readonly List<IDecryptorPlugin> _loaded = [];
    private readonly Dictionary<string, string> _failures = [];

    private bool _loadedOnce;

    public PluginHost(string pluginDirectory, ILogger<PluginHost>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginDirectory);

        PluginDirectory = pluginDirectory;
        _logger = logger;
    }

    public string PluginDirectory { get; }

    public IReadOnlyList<IDecryptorPlugin> LoadedPlugins => _loaded;

    public IReadOnlyDictionary<string, string> LoadFailures => _failures;

    public void LoadPlugins()
    {
        if (_loadedOnce)
        {
            return;
        }

        _loadedOnce = true;

        if (!Directory.Exists(PluginDirectory))
        {
            // 方案 B 下这是正常情况：用户没有安装任何解密插件。
            _logger?.LogInformation("插件目录不存在，跳过加载：{Directory}", PluginDirectory);
            return;
        }

        foreach (var dllPath in Directory.EnumerateFiles(PluginDirectory, "*.dll", SearchOption.TopDirectoryOnly))
        {
            TryLoadFromAssembly(dllPath);
        }

        _logger?.LogInformation(
            "插件加载完成：成功 {Loaded} 个，失败 {Failed} 个",
            _loaded.Count,
            _failures.Count);
    }

    private void TryLoadFromAssembly(string dllPath)
    {
        try
        {
            // 每个插件独立上下文，且不允许共享主程序的依赖程序集，
            // 否则插件携带不同版本的同一依赖时会互相覆盖。
            var context = new PluginLoadContext(dllPath);
            var assembly = context.LoadFromAssemblyPath(dllPath);

            var pluginTypes = assembly
                .GetTypes()
                .Where(t => typeof(IDecryptorPlugin).IsAssignableFrom(t)
                            && t is { IsInterface: false, IsAbstract: false });

            var foundAny = false;

            foreach (var type in pluginTypes)
            {
                if (Activator.CreateInstance(type) is not IDecryptorPlugin plugin)
                {
                    _failures[dllPath] = $"无法实例化插件类型：{type.FullName}";
                    continue;
                }

                if (Validate(plugin) is { } reason)
                {
                    _failures[dllPath] = reason;
                    continue;
                }

                _loaded.Add(plugin);
                foundAny = true;

                _logger?.LogInformation(
                    "已加载解密插件：{Name} {Version}（支持 {Extensions}）",
                    plugin.Name,
                    plugin.Version,
                    string.Join(", ", plugin.SupportedExtensions));
            }

            if (!foundAny && !_failures.ContainsKey(dllPath))
            {
                _failures[dllPath] = "程序集内没有找到实现了解密契约的类型。";
            }
        }
        catch (Exception ex)
        {
            _failures[dllPath] = ex.Message;
            _logger?.LogWarning(ex, "加载插件失败：{Path}", dllPath);
        }
    }

    /// <summary>
    /// 校验插件元信息。一个声明支持零个扩展名的插件是无意义的，
    /// 放进集合只会让解析逻辑空转，因此在入口就拦掉。
    /// </summary>
    private static string? Validate(IDecryptorPlugin plugin)
    {
        if (string.IsNullOrWhiteSpace(plugin.Name))
        {
            return "插件未声明名称。";
        }

        if (plugin.SupportedExtensions is null || plugin.SupportedExtensions.Count == 0)
        {
            return "插件未声明任何受支持的扩展名。";
        }

        var badExtension = plugin.SupportedExtensions
            .FirstOrDefault(e => string.IsNullOrWhiteSpace(e) || !e.StartsWith('.'));

        return badExtension is null
            ? null
            : $"插件声明的扩展名格式不正确：{badExtension}（应形如 \".ncm\"）。";
    }

    /// <summary>
    /// 插件专用加载上下文。解析依赖时优先用插件自己目录下的副本，
    /// 找不到才回退到默认上下文，从而让插件可以与主程序共存不同版本的同名库。
    /// </summary>
    private sealed class PluginLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;

        public PluginLoadContext(string pluginPath)
            : base(name: $"Plugin:{Path.GetFileNameWithoutExtension(pluginPath)}", isCollectible: false)
        {
            _resolver = new AssemblyDependencyResolver(pluginPath);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // 契约程序集必须由主程序提供，保证插件与宿主看到的是同一个类型身份。
            // 若由插件各带一份，类型判定会失败，插件将永远无法被识别。
            if (assemblyName.Name == typeof(IDecryptorPlugin).Assembly.GetName().Name)
            {
                return null;
            }

            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}
