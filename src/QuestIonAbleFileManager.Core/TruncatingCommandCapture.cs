using System.Text;

namespace QuestIonAbleFileManager.Core;

// Optional internal capability: retain a bounded prefix while draining both
// streams to EOF. Overflow is evidence, not a reason to kill a capture process.
internal interface ITruncatingCommandRunner : ICommandRunner
{
    Task<TruncatingCommandResult> RunTruncatingAsync(
        string fileName, IReadOnlyList<string> arguments,
        int maximumStandardOutputBytes, int maximumStandardErrorBytes,
        TimeSpan timeout, CancellationToken cancellationToken = default);
}

internal sealed record TruncatingCommandResult(
    CommandResult CommandResult, bool StandardOutputTruncated, bool StandardErrorTruncated)
{
    internal const int MaximumStreamBytes = 256 * 1024;
    public bool Truncated => StandardOutputTruncated || StandardErrorTruncated;

    // Compatibility for injected legacy runners. This bounds retained evidence,
    // not the upstream runner's collection/memory. A future retained diagnostic
    // owner must require ITruncatingCommandRunner instead of accepting this path.
    internal static TruncatingCommandResult FromLegacy(CommandResult result, int outputBytes, int errorBytes)
    {
        var output = Prefix(result.StandardOutput, outputBytes);
        var error = Prefix(result.StandardError, errorBytes);
        return new(result with { StandardOutput = output.Text, StandardError = error.Text }, output.Truncated, error.Truncated);
    }

    private static (string Text, bool Truncated) Prefix(string value, int maximumBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maximumBytes) return (value, false);
        var characters = 0;
        var bytes = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (rune.Utf8SequenceLength > maximumBytes - bytes) break;
            bytes += rune.Utf8SequenceLength;
            characters += rune.Utf16SequenceLength;
        }
        return (value[..characters], true);
    }

    internal static (string Text, bool Truncated) DecodePrefix(ReadOnlySpan<byte> bytes, bool truncated, int maximumBytes)
    {
        var characters = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        try
        {
            // Do not flush a cut multibyte suffix into a fabricated replacement
            // character. Invalid bytes wholly inside the prefix retain UTF-8's
            // ordinary replacement behavior; a complete stream flushes normally.
            var count = Encoding.UTF8.GetDecoder().GetChars(bytes, characters.AsSpan(), flush: !truncated);
            // Replacement characters can take more UTF-8 bytes than malformed
            // source bytes. Bound that normalized representation as well.
            var bounded = Prefix(new string(characters, 0, count), maximumBytes);
            return (bounded.Text, truncated || bounded.Truncated);
        }
        finally { Array.Clear(characters); }
    }
}
