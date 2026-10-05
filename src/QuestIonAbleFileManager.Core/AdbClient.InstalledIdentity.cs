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

        // Require the nanosecond timestamp formats, not merely any successful
        // stat invocation. Older/alternate stat implementations may print the
        // literal format token or seconds only.
        const string digit = "[0-9]";
        var stampPattern = string.Concat(Enumerable.Repeat(digit, 4)) + "-" + digit + digit + "-" + digit + digit +
            "' '" + digit + digit + ":" + digit + digit + ":" + digit + digit + "." +
            string.Concat(Enumerable.Repeat(digit, 9)) + "' '[+-]" + string.Concat(Enumerable.Repeat(digit, 4));
        var command =
            "unsupported() { printf 'qfm-installed-digest:unsupported\\n'; exit 90; }; " +
            "valid_stamp() { case \"$1\" in " + stampPattern + ") return 0;; *) return 1;; esac; }; " +
            "valid_metadata() { value=\"$1\"; for field in 1 2 3; do " +
            "part=\"${value%%|*}\"; [ \"$part\" != \"$value\" ] || return 1; " +
            "case \"$part\" in ''|*[!0-9]*) return 1;; esac; value=\"${value#*|}\"; done; " +
            "part=\"${value%%|*}\"; [ \"$part\" != \"$value\" ] || return 1; " +
            "valid_stamp \"$part\" || return 1; valid_stamp \"${value#*|}\"; }; " +
            "command -v sha256sum >/dev/null 2>&1 || unsupported; " +
            "command -v stat >/dev/null 2>&1 || unsupported; " +
            "probe=$(stat -Lc '%d|%i|%s|%y|%z' /proc/$$ 2>/dev/null) || unsupported; " +
            "valid_metadata \"$probe\" || unsupported; " +
            $"candidate=$(realpath {AndroidInput.ShellQuote(remotePath)}) || exit 41; " +
            "expected=\"$candidate\"; " +
            BuildOpenedRemoteHandleProof() +
            "before=$(stat -Lc '%d|%i|%s|%y|%z' /proc/$$/fd/3) || exit 91; " +
            "valid_metadata \"$before\" || exit 91; " +
            "size=$(stat -Lc '%s' /proc/$$/fd/3) || exit 91; " +
            "digest=$(sha256sum <&3) || exit 92; digest=${digest%% *}; " +
            "after=$(stat -Lc '%d|%i|%s|%y|%z' /proc/$$/fd/3) || exit 91; " +
            "[ \"$before\" = \"$after\" ] || exit 93; " +
            "opened=$(readlink /proc/$$/fd/3) || exit 49; " +
            "[ \"$opened\" = \"$expected\" ] || exit 42; " +
            $"current=$(realpath {AndroidInput.ShellQuote(remotePath)}) || exit 41; " +
            "[ \"$current\" = \"$expected\" ] || exit 94; " +
            "current_stat=$(stat -Lc '%d|%i|%s|%y|%z' \"$current\") || exit 91; " +
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
        // adb exec-out may report success after a remote shell exits with the
        // capability marker. Only the complete exact marker authorizes fallback.
        if (result.CommandResult.ExitCode is 0 or 90 && result.CommandResult.StandardError.Length == 0 &&
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
