namespace LanInspector.Core.Diagnostics;

public enum CapturePrerequisiteKind
{
    Available,
    MissingDriver,
    InsufficientPermission,
    Unavailable
}

public sealed record CapturePrerequisiteStatus(
    CapturePrerequisiteKind Kind,
    string Summary,
    string? Suggestion = null);

public interface ICapturePrerequisiteService
{
    Task<CapturePrerequisiteStatus> CheckAsync(CancellationToken cancellationToken = default);
}
