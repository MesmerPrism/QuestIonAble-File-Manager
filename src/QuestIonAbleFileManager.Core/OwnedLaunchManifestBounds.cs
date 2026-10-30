using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;

namespace QuestIonAbleFileManager.Core;

internal static class OwnedLaunchManifestBounds
{
    internal static void RequireValid(ApkArtifactInspection artifact, InstalledApkIdentity before,
        InstalledApkIdentity? after, ApkLaunchDiagnosticAttempt attempt, ApkLaunchDiagnosticCapture capture,
        string hostFence, string deviceFence, string dispositionDetail, string? postReadbackFailure)
    {
        var budget = new Budget();
        budget.Artifact(artifact);
        budget.Installed(before);
        if (after is not null) budget.Installed(after);
        if (attempt is null || capture is null) throw Invalid();
        budget.Text(hostFence);
        budget.Text(deviceFence);
        budget.Text(dispositionDetail);
        budget.Text(postReadbackFailure);
        budget.Text(capture.RelativePath);
        budget.Text(capture.Sha256);
        budget.Text(attempt.FailureCode);
        budget.Text(attempt.FailureMessage);
        budget.Processes(attempt.CurrentPackageProcessIds);
        if (attempt.Launch is { } launch)
        {
            budget.Artifact(launch.Artifact);
            budget.Installed(launch.Installed);
            budget.Text(launch.Component);
            budget.Text(launch.LauncherTargetActivity);
            budget.Command(launch.CommandResult);
        }
    }

    private static LocalApiException Invalid() => LocalApiDiagnosticOutputIntent.Failure("diagnostic_manifest_invalid");

    // Validate the closed typed graph before a serializer, computed getter or
    // collection projection can allocate from its contents. This is an early
    // resource bound, not another effect classifier. The capped destination
    // still enforces the final complete-document size, including fixed fields.
    private sealed class Budget
    {
        private int _remaining = LocalApiDiagnosticOutputIntent.MaximumPayloadFileBytes;

        private void Charge(int count)
        {
            if (count < 0 || count > _remaining) throw Invalid();
            _remaining -= count;
        }

        internal void Text(string? value)
        {
            if (value is null) { Charge(4); return; }
            // Every UTF-16 code unit needs at least one serialized byte. Reject
            // large tokens before scanning or asking the encoder to allocate.
            if (value.Length > _remaining) throw Invalid();
            Charge(2);
            foreach (var rune in value.EnumerateRunes())
            {
                var bytes = rune.Value switch
                {
                    '\\' or '\b' or '\f' or '\n' or '\r' or '\t' => 2,
                    _ when JavaScriptEncoder.Default.WillEncode(rune.Value) => 6 * rune.Utf16SequenceLength,
                    _ => rune.Utf8SequenceLength
                };
                Charge(bytes);
            }
        }

        private void Identity(ApkArtifactIdentity? identity)
        {
            if (identity is null) { Charge(4); return; }
            Text(identity.PackageName);
            Text(identity.VersionName);
            Text(identity.SignerSha256);
            Text(identity.SplitName);
        }

        internal void Artifact(ApkArtifactInspection artifact)
        {
            if (artifact is null) throw Invalid();
            Text(artifact.Path);
            Text(artifact.Sha256);
            Identity(artifact.Identity);
        }

        internal void Installed(InstalledApkIdentity installed)
        {
            if (installed is null) throw Invalid();
            Text(installed.Serial);
            Identity(installed.Identity);
            Strings(installed.ApkPaths);
            Text(installed.BaseApkSha256);
            Text(installed.VerificationMethod);
            Text(installed.VerificationFallbackReason);
        }

        private void Strings(IReadOnlyList<string> values)
        {
            if (values is null || values.Count < 0 || values.Count > _remaining / 2) throw Invalid();
            Charge(values.Count);
            for (var index = 0; index < values.Count; index++) Text(values[index]);
        }

        internal void Processes(IReadOnlyList<int> values)
        {
            if (values is null || values.Count < 0 || values.Count > _remaining / 2) throw Invalid();
            Span<char> number = stackalloc char[11];
            for (var index = 0; index < values.Count; index++)
            {
                values[index].TryFormat(number, out var written, provider: CultureInfo.InvariantCulture);
                Charge(written + 1);
            }
        }

        internal void Command(CommandResult command)
        {
            if (command is null) throw Invalid();
            Text(command.FileName);
            Strings(command.Arguments);
            Text(command.StandardOutput);
            Text(command.StandardError);
            // CondensedOutput is also serialized. Bound its input twice before
            // the string.Join getter runs; never call it as an admission probe.
            Text(command.StandardOutput);
            Text(command.StandardError);
            Charge(64); // bounded empty-output fallback and join separator
        }
    }
}
