using System.Net;

namespace LanInspector.Core.Visibility;

public interface IVisibilityExplanationService
{
    Task<VisibilityExplanation> ExplainAsync(IPAddress target, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<VisibilityExplanation>> ExplainAllKnownAsync(CancellationToken cancellationToken = default);
}
