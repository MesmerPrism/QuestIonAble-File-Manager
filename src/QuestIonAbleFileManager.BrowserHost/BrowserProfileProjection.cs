using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost;

internal static class BrowserProfileProjection
{
    internal static BrowserProfileOutcome Empty(BrowserProfileRequest request, string effect) =>
        new(request.Action[8..], request.DeviceId, request.Action == "profile.import" ? request.ReplaceExisting : null, effect);
    internal static BrowserCommandOutcome Project(BrowserProfileRequest request, OperatorCommand command, OperatorExecutionResult result)
    {
        if (!ReferenceEquals(result.Command, command)) return Unknown(request, "profile_result_binding_mismatch");
        var projection = Empty(request, request.Mutation ? "confirmed" : "readOnly");
        if (request.Action == "profile.list" && result.ConnectivityProfileList is { } list &&
            list.Schema == QuestConnectivityProfileManagementContract.ListSchema && list.Status == "ok" &&
            list.Profiles.All(entry => QuestConnectivityProfileManagementContract.IsDeviceId(entry.DeviceId) && ValidState(entry.State, entry.ReasonCode)) &&
            list.Profiles.Select(entry => entry.DeviceId).Distinct(StringComparer.Ordinal).Count() == list.Profiles.Count)
        {
            projection = projection with { List = new(list.Schema, list.Status, list.Profiles.Take(128).ToArray()), TotalProfiles = list.Profiles.Count, Truncated = list.Profiles.Count > 128 };
        }
        else if (request.Action == "profile.status" && result.ConnectivityProfileStatus is { } status &&
            status.Schema == QuestConnectivityProfileManagementContract.StatusSchema && status.Status == "ok" && status.DeviceId == request.DeviceId && ValidState(status.State, status.ReasonCode))
            projection = projection with { Status = new(status.Schema, status.Status, status.DeviceId, status.State, status.ReasonCode) };
        else if (request.Mutation && result.ConnectivityProfileMutation is { } receipt &&
            receipt.Schema == QuestConnectivityProfileManagementContract.MutationSchema && receipt.Status == "confirmed" && receipt.DeviceId == request.DeviceId &&
            (request.Action == "profile.import" && receipt.State == "enrolled" &&
                (receipt.Action == "created" && receipt.ReasonCode == "profileCreated" || request.ReplaceExisting && receipt.Action == "replaced" && receipt.ReasonCode == "profileReplaced") ||
             request.Action == "profile.revoke" && receipt.Action == "revoked" && receipt.State == "absent" && receipt.ReasonCode is "profileRevoked" or "profileAlreadyAbsent"))
            projection = projection with { Receipt = new(receipt.Schema, receipt.Status, receipt.Action, receipt.DeviceId, receipt.State, receipt.ReasonCode) };
        else return Unknown(request, "profile_result_binding_mismatch");
        return new(request.RequestId, request.Action, "completed", Profile: projection);
    }
    private static bool ValidState(string state, string reason) => (state, reason) is
        ("enrolled", "profileEnrolled") or ("absent", "profileAbsent") or ("invalid", "profileInvalid");
    internal static BrowserCommandOutcome Failed(BrowserProfileRequest request, QuestConnectivityProfileManagementException exception)
    {
        var known = exception.Code is "profileReplaceConfirmationRequired" or "profileWriteVerificationFailedRolledBack" or "profileWriteRollbackFailed" or
            "profileRevokeReadbackFailed" or "profileStoreUnavailable" or "profileStoreWriteFailed" or "profileStoreDeleteFailed" or "profileCredentialInvalid";
        if (!known || exception.Status is not ("rejected" or "failed") || exception.RollbackState is not ("not_applicable" or "confirmed" or "uncertain"))
            return Unknown(request, "profile_operation_failed");
        var notPerformed = request.Action == "profile.import" && exception.Code == "profileReplaceConfirmationRequired" && exception.Status == "rejected" && exception.RollbackState == "not_applicable";
        var rolledBack = request.Action == "profile.import" && exception.Code == "profileWriteVerificationFailedRolledBack" && exception.Status == "failed" && exception.RollbackState == "confirmed";
        var effect = !request.Mutation ? "readOnly" : notPerformed ? "notPerformed" : rolledBack ? "rolledBack" : "unknown";
        return new(request.RequestId, request.Action, request.Mutation && !notPerformed && !rolledBack ? "outcomeUnknown" : "failed", Error: exception.Code,
            Profile: Empty(request, effect) with { Failure = new(exception.Code, exception.Status, exception.RollbackState) });
    }
    internal static BrowserCommandOutcome Unknown(BrowserProfileRequest request, string code) =>
        new(request.RequestId, request.Action, request.Mutation ? "outcomeUnknown" : "failed", Error: code,
            Profile: Empty(request, request.Mutation ? "unknown" : "readOnly") with { Failure = new(code, null, null) });
}
