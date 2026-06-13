namespace LanInspector.Core.RemoteAccess;

public sealed record ConnectionCandidate(
    ConnectionMethod Method,
    string Target,
    int Port,
    string SshCommand,
    string Description);

public sealed record ConnectionTestResult(
    ConnectionCandidate Candidate,
    bool IsReachable,
    string? ErrorMessage = null);

public sealed record RemoteAccessRecommendation(
    string DeviceId,
    string DeviceDisplayName,
    IReadOnlyList<ConnectionCandidate> Candidates,
    ConnectionCandidate? BestCandidate,
    string Summary,
    string? SubnetRouteCommand = null,
    string? CgnatWarning = null);

public sealed record SubnetRouteSuggestion(
    string Command,
    string Explanation,
    string AdminConsoleNote);
