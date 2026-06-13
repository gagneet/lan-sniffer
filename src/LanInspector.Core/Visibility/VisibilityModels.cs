using System.Net;

namespace LanInspector.Core.Visibility;

public enum VisibilityResult
{
    Visible,
    ProbablyVisible,
    ProbablyNotVisible,
    NotVisible,
    Unknown
}

public sealed record VisibilityExplanation(
    IPAddress Target,
    string TargetLabel,
    VisibilityResult Result,
    string Summary,
    IReadOnlyList<string> CanSee,
    IReadOnlyList<string> CannotSee,
    IReadOnlyList<string> Why,
    IReadOnlyList<string> HowToImprove);
