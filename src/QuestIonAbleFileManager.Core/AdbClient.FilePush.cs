using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace QuestIonAbleFileManager.Core;

public sealed partial class AdbClient
{
    private const int PushDestinationMaximumOutputBytes = 1024;
    private const string PushDestinationAbsent = "qfm-file-push:v1:absent\n";
    private const string PushDestinationRejected = "qfm-file-push:v1:rejected\n";

    // One fixed, read-only observation before push and one after it. Default stat
    // observes the final inode without -L: mode and size come from that same stat.
    // Ancestors and the interval around ADB push are not held or made atomic.
    private async Task<long?> ReadPushDestinationSizeAsync(
        string serial, string remotePath, CancellationToken cancellationToken)
    {
        if (_runner is not IStreamingCommandRunner streaming)
            throw new InvalidOperationException("File push requires bounded destination type and size readback.");
        var script =
            $"destination={AndroidInput.ShellQuote(remotePath)}; " +
            "if [ -L \"$destination\" ]; then printf 'qfm-file-push:v1:rejected\\n'; exit 41; fi; " +
            "if [ ! -e \"$destination\" ]; then printf 'qfm-file-push:v1:absent\\n'; exit 0; fi; " +
            "if [ ! -f \"$destination\" ]; then printf 'qfm-file-push:v1:rejected\\n'; exit 41; fi; " +
            "metadata=$(stat -c '%f:%s' -- \"$destination\") || { printf 'qfm-file-push:v1:rejected\\n'; exit 42; }; " +
            "if [ -L \"$destination\" ] || [ ! -f \"$destination\" ]; then printf 'qfm-file-push:v1:rejected\\n'; exit 43; fi; " +
            "printf 'qfm-file-push:v1:%s\\n' \"$metadata\"";
        using var output = new MemoryStream();
        var captured = await streaming.RunToStreamAsync(AdbPath,
            ["-s", serial, "exec-out", "sh", "-c", script], output,
            PushDestinationMaximumOutputBytes, InspectionTimeout, cancellationToken).ConfigureAwait(false);
        if (output.Length > PushDestinationMaximumOutputBytes || captured.BytesWritten != output.Length ||
            captured.CommandResult.StandardError.Length != 0)
            throw new InvalidDataException("Push destination readback was incomplete or included errors.");
        var text = Encoding.UTF8.GetString(output.ToArray());
        if (text == PushDestinationRejected)
            throw new InvalidDataException("The push destination must be an absent path or a regular file, not a directory, final symlink, or other nonregular object.");
        captured.CommandResult.EnsureSuccess("Read push destination type and size");
        if (text == PushDestinationAbsent) return null;
        var match = Regex.Match(text, @"\Aqfm-file-push:v1:8[0-9a-fA-F]{3}:(0|[1-9][0-9]{0,18})\n\z",
            RegexOptions.CultureInvariant);
        if (!match.Success || !long.TryParse(match.Groups[1].Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out var size))
            throw new InvalidDataException("Push destination readback did not identify one regular file and bounded size.");
        return size;
    }
}
