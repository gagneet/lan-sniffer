using LanInspector.Core.Analysis;

namespace LanInspector.Core.Plugins;

public sealed class PluginManager
{
    private readonly List<IPlugin> _plugins = [];

    public IReadOnlyCollection<IPlugin> Plugins => _plugins;

    public void Register(IPlugin plugin)
    {
        _plugins.Add(plugin);
    }

    public IReadOnlyCollection<IPacketAnalyzer> CreateAnalyzers()
    {
        return _plugins.SelectMany(plugin => plugin.CreateAnalyzers()).ToArray();
    }
}
