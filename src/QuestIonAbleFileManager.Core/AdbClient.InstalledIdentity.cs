using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace QuestIonAbleFileManager.Core;

public sealed partial class AdbClient
{
    private const int InstalledDigestMaximumOutputBytes = 4096;
    private const string InstalledDigestUnsupported = "qfm-installed-digest:unsupported\n";

    // Absence of the precise remote hashing/stat capability is the only fallback.
    // Path, body, command, parser, cancellation and transfer failures fail closed.
    private async Task<(string Sha256, long Size)?> ReadInstalledBaseDigestAsync(
        string serial, string remotePath, CancellationToken cancellationToken)
    {
        remotePath = AndroidInput.RequireRemotePath(remotePath);
        if (_runner is not IStreamingCommandRunner streamingRunner)
            throw new InvalidOperationException("The configured command runner does not support bounded installed-APK readback.");

        var command =
            "unsupported() { printf 'qfm-installed-digest:unsupported\\n'; exit 90; }; " +
            "command -v sha256sum >/dev/null 2>&1 || unsupported; " +
            "command -v stat >/dev/null 2>&1 || unsupported; " +
            "probe=$(stat -Lc '%d:%i:%s:%y:%z' /proc/$$ 2>/dev/null) || unsupported; " +
            "case \"$probe\" in ''|*'?'*) unsupported;; esac; " +
            $"candidate=$(realpath {AndroidInput.ShellQuote(remotePath)}) || exit 41; " +
            "expected=\"$candidate\"; " +
            BuildOpenedRemoteHandleProof() +
            "before=$(stat -Lc '%d:%i:%s:%y:%z' /proc/$$/fd/3) || exit 91; " +
            "case \"$before\" in ''|*'?'*) exit 91;; esac; " +
            "size=$(stat -Lc '%s' /proc/$$/fd/3) || exit 91; " +
            "digest=$(sha256sum <&3) || exit 92; digest=${digest%% *}; " +
            "after=$(stat -Lc '%d:%i:%s:%y:%z' /proc/$$/fd/3) || exit 91; " +
            "[ \"$before\" = \"$after\" ] || exit 93; " +
            "opened=$(readlink /proc/$$/fd/3) || exit 49; " +
            "[ \"$opened\" = \"$expected\" ] || exit 42; " +
            $"current=$(realpath {AndroidInput.ShellQuote(remotePath)}) || exit 41; " +
            "[ \"$current\" = \"$expected\" ] || exit 94; " +
            "current_stat=$(stat -Lc '%d:%i:%s:%y:%z' \"$current\") || exit 91; " +
            "[ \"$current_stat\" = \"$after\" ] || exit 94; " +
            "printf 'qfm-installed-digest:v1\\n%s\\n%s\\n' \"$size\" \"$digest\"";
        var arguments = new[] { "-s", serial, "exec-out", "sh", "-c", command };
        using var output = new MemoryStream();
        var result = await streamingRunner.RunToStreamAsync(
            AdbPath, arguments, output, InstalledDigestMaximumOutputBytes,
            TransferTimeout, cancellationToken).ConfigureAwait(false);
        if (output.Length > InstalledDigestMaximumOutputBytes || result.BytesWritten != output.Length)
            throw new InvalidDataException("Installed digest output was incomplete or exceeded its bound.");
        var text = new UTF8Encoding(false, true).GetString(output.ToArray());
        if (result.CommandResult.ExitCode == 90 && result.CommandResult.StandardError.Length == 0 &&
            text == InstalledDigestUnsupported)
            return null;
        result.CommandResult.EnsureSuccess("Read exact installed APK digest");
        if (result.CommandResult.StandardError.Length != 0)
            throw new InvalidDataException("Installed digest readback included standard error.");
        var match = Regex.Match(text, @"\Aqfm-installed-digest:v1\n([1-9][0-9]{0,18})\n([0-9a-f]{64})\n\z",
            RegexOptions.CultureInvariant);
        if (!match.Success || !long.TryParse(match.Groups[1].Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out var size) || size > FleetIntegrationContract.MaximumPullBytes)
            throw new InvalidDataException("Installed digest readback was malformed.");
        return (match.Groups[2].Value, size);
    }
}
