using LanInspector.Core.Analysis;

namespace LanInspector.Core.Plugins;

public interface IPlugin
{
    string Name { get; }

    IReadOnlyCollection<IPacketAnalyzer> CreateAnalyzers();
}
