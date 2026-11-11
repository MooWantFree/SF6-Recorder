namespace Sf6Prototype;

public record CaptureEvidence(string RunId, string PlayerId, string AccountId, DateTimeOffset CapturedAt, int MatchCount);

public static class SessionEvidence
{
    public static bool Restored(CaptureEvidence? previous, string runId, string playerId,
        string accountId, bool loginFlowVisited, int? authHttpStatus, int? battleHttpStatus, bool hasReplayList) =>
        previous != null && previous.RunId != runId && previous.PlayerId == playerId &&
        previous.AccountId == accountId && !string.IsNullOrEmpty(accountId) && !loginFlowVisited &&
        authHttpStatus == 200 && battleHttpStatus == 200 && hasReplayList;
}
