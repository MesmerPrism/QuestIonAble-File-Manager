using System.Security.Cryptography;
using System.Text;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.Core.Tests;

public sealed class InstalledApkDigestTests
{
    private const string Serial = "QUEST123";
    private const string RemotePath = "/data/app/example/base.apk";
    private static readonly byte[] ApkBytes = [0x50, 0x4b, 0x03, 0x04];
    private static readonly string Hash = Convert.ToHexString(SHA256.HashData(ApkBytes)).ToLowerInvariant();
    private static ApkArtifactInspection Artifact => new("example.apk", ApkBytes.Length, Hash,
        new("com.example.app", 42, "1.0", new string('a', 64), null));
    private static string Digest(string? hash = null, string size = "4") =>
        $"qfm-installed-digest:v1\n{size}\n{hash ?? Hash}\n";

    [Fact]
    public async Task ExactDigestUsesBoundedReadbackAndRechecksPackageWithoutTransferringApk()
    {
        var runner = new DigestRunner(Digest());
        var result = await new AdbClient("adb", runner).ReadInstalledIdentityAsync(Serial, Artifact);
        Assert.Equal(Artifact.Identity, result.Identity);
        Assert.Equal(Hash, result.BaseApkSha256);
        Assert.Equal(4, result.BaseApkSizeBytes);
        Assert.Equal("same-opened-handle-device-sha256", result.VerificationMethod);
        Assert.Equal(2, runner.PackageReads);
        Assert.Equal(0, runner.ApkStreams);
        Assert.Equal(4096, Assert.Single(runner.DigestBounds));
        Assert.All(runner.Calls, args => Assert.Equal(new[] { "-s", Serial }, args.Take(2)));
    }

    [Theory]
    [InlineData("4", true)]
    [InlineData("5", false)]
    public async Task DifferentDigestOrSizeDoesNotBorrowExpectedIdentity(string size, bool differentHash)
    {
        var runner = new DigestRunner(Digest(differentHash ? new string('b', 64) : Hash, size));
        var result = await new AdbClient("adb", runner).ReadInstalledIdentityAsync(Serial, Artifact);
        Assert.Null(result.Identity);
        Assert.Equal(long.Parse(size), result.BaseApkSizeBytes);
        Assert.Equal(0, runner.ApkStreams);
    }

    [Fact]
    public async Task ExactUnsupportedResponseFallsBackToBoundedHostHash()
    {
        var runner = new DigestRunner("qfm-installed-digest:unsupported\n", 90);
        var result = await new AdbClient("adb", runner).ReadInstalledIdentityAsync(Serial, Artifact);
        Assert.Equal(Artifact.Identity, result.Identity);
        Assert.Equal("host-streamed-sha256", result.VerificationMethod);
        Assert.Equal("remote-handle-digest-capability-unsupported", result.VerificationFallbackReason);
        Assert.Equal(1, runner.ApkStreams);
        Assert.Equal(2, runner.PackageReads);
    }

    [Theory]
    [InlineData("qfm-installed-digest:v1\n4\n", 0, "")]
    [InlineData("qfm-installed-digest:v1\n04\n", 0, "")]
    [InlineData("qfm-installed-digest:unsupported\n", 1, "")]
    [InlineData("qfm-installed-digest:unsupported\n", 90, "permission denied")]
    [InlineData("qfm-installed-digest:unsupported\nextra\n", 90, "")]
    [InlineData("", 1, "permission denied")]
    public async Task MalformedOrFailedProbeNeverFallsBack(string wire, int exitCode, string error)
    {
        var runner = new DigestRunner(wire, exitCode, error);
        var failure = await Record.ExceptionAsync(() =>
            new AdbClient("adb", runner).ReadInstalledIdentityAsync(Serial, Artifact));
        Assert.NotNull(failure);
        Assert.True(failure is InvalidDataException or AdbCommandException,
            $"Expected a closed digest rejection, received {failure.GetType().Name}.");
        Assert.Equal(0, runner.ApkStreams);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("04")]
    [InlineData("9223372036854775808")]
    public async Task InvalidByteCountsFailClosed(string size)
    {
        var runner = new DigestRunner(Digest(size: size));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new AdbClient("adb", runner).ReadInstalledIdentityAsync(Serial, Artifact));
        Assert.Equal(0, runner.ApkStreams);
    }

    [Theory]
    [InlineData("ABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFAB")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public async Task NonCanonicalHashesFailClosed(string hash)
    {
        var runner = new DigestRunner(Digest(hash));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new AdbClient("adb", runner).ReadInstalledIdentityAsync(Serial, Artifact));
        Assert.Equal(0, runner.ApkStreams);
    }

    [Theory]
    [InlineData(42)]
    [InlineData(49)]
    [InlineData(91)]
    [InlineData(92)]
    [InlineData(93)]
    [InlineData(94)]
    public async Task RemoteHandleOrDigestFailureNeverFallsBack(int exitCode)
    {
        var runner = new DigestRunner("", exitCode);
        await Assert.ThrowsAsync<AdbCommandException>(() =>
            new AdbClient("adb", runner).ReadInstalledIdentityAsync(Serial, Artifact));
        Assert.Equal(0, runner.ApkStreams);
    }

    [Fact]
    public async Task CancelledProbeDoesNotBecomeFallback()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var runner = new DigestRunner(Digest());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new AdbClient("adb", runner).ReadInstalledIdentityAsync(Serial, Artifact, cancellation.Token));
        Assert.Equal(0, runner.ApkStreams);
    }

    [Fact]
    public async Task PackageReplacementAfterDigestFailsClosed()
    {
        var runner = new DigestRunner(Digest()) { ReplacePathOnRecheck = true };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new AdbClient("adb", runner).ReadInstalledIdentityAsync(Serial, Artifact));
        Assert.Equal(0, runner.ApkStreams);
    }

    [Fact]
    public async Task OversizedDigestIsBoundedAndNeverFallsBack()
    {
        var runner = new DigestRunner(new string('x', 4097));
        await Assert.ThrowsAsync<FleetTransferLimitException>(() =>
            new AdbClient("adb", runner).ReadInstalledIdentityAsync(Serial, Artifact));
        Assert.Equal(0, runner.ApkStreams);
    }

    [Fact]
    public async Task ProductionRunnerPreservesBoundedDigestBytesEvenOnUnsupportedExit()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var destination = new MemoryStream();
        var script = "[Console]::Out.Write(\"qfm-installed-digest:unsupported`n\"); exit 90";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var result = await new CommandRunner().RunToStreamAsync("powershell.exe",
            ["-NoProfile", "-NonInteractive", "-EncodedCommand", encoded], destination,
            4096, TimeSpan.FromSeconds(15));
        Assert.Equal(90, result.CommandResult.ExitCode);
        Assert.Equal("", result.CommandResult.StandardError);
        Assert.Equal("qfm-installed-digest:unsupported\n", Encoding.UTF8.GetString(destination.ToArray()));
        Assert.Equal(destination.Length, result.BytesWritten);
    }

    [Fact]
    public async Task ProductionRunnerStopsDigestOutputAtIts4096ByteBound()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var destination = new MemoryStream();
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes("[Console]::Out.Write(('x' * 4097))"));
        var error = await Assert.ThrowsAsync<FleetTransferLimitException>(() =>
            new CommandRunner().RunToStreamAsync("powershell.exe",
                ["-NoProfile", "-NonInteractive", "-EncodedCommand", encoded], destination,
                4096, TimeSpan.FromSeconds(15)));
        Assert.Equal(4096, error.MaximumBytes);
        Assert.True(destination.Length <= 4096);
    }

    [Theory]
    [InlineData("1|2|4|2026-10-05 12:34:56.123456789 +0000|2026-10-05 12:34:56.987654321 +0000", true)]
    [InlineData("1|2|4|2026-10-05 12:34:56 +0000|2026-10-05 12:34:56 +0000", false)]
    [InlineData("1|2|4|%y|%z", false)]
    [InlineData("1|inode|4|2026-10-05 12:34:56.123456789 +0000|2026-10-05 12:34:56.987654321 +0000", false)]
    [InlineData("1|2|4|2026-10-05 12:34:56.123456789 +0000|2026-10-05 12:34:56.987654321 +0000|extra", false)]
    [InlineData("1|2|4|2026-10-05 12:34:56.123456789 +0000|2026-10-05 12:34:56.987654321 +0000\nextra", false)]
    public async Task ProductionMetadataValidatorRequiresExactNanosecondFields(string metadata, bool accepted)
    {
        var bash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe");
        if (!File.Exists(bash)) return;
        var recording = new DigestRunner(Digest());
        await new AdbClient("adb", recording).ReadInstalledIdentityAsync(Serial, Artifact);
        var digestCall = Assert.Single(recording.Calls, args => args.Count == 6 && args[2] == "exec-out");
        var command = digestCall[5];
        var capabilityOffset = command.IndexOf("command -v sha256sum", StringComparison.Ordinal);
        Assert.True(capabilityOffset > 0);
        var functions = command[..capabilityOffset];
        Assert.Contains("valid_stamp()", functions, StringComparison.Ordinal);
        Assert.Contains("valid_metadata()", functions, StringComparison.Ordinal);
        // The exact production validators execute locally; all subsequent device commands are excluded.
        var literal = "'" + metadata.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        var result = await new CommandRunner().RunAsync(bash,
            ["-c", functions + "valid_metadata " + literal], TimeSpan.FromSeconds(15));
        Assert.Equal(accepted ? 0 : 1, result.ExitCode);
        Assert.Equal("", result.StandardOutput);
        Assert.Equal("", result.StandardError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeProductionWireIsAcceptedByProductionParser(bool unsupported)
    {
        var bash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe");
        if (!File.Exists(bash)) return;
        var recording = new DigestRunner(Digest());
        await new AdbClient("adb", recording).ReadInstalledIdentityAsync(Serial, Artifact);
        var call = Assert.Single(recording.Calls, args => args.Count == 6 && args[2] == "exec-out");
        var command = call[5];
        string nativeCommand;
        if (unsupported)
        {
            var functionEnd = command.IndexOf("valid_stamp()", StringComparison.Ordinal);
            Assert.True(functionEnd > 0);
            nativeCommand = command[..functionEnd] + "unsupported";
        }
        else
        {
            var printfOffset = command.LastIndexOf("printf 'qfm-installed-digest:v1", StringComparison.Ordinal);
            Assert.True(printfOffset > 0);
            nativeCommand = "size=4; digest='" + Hash + "'; " + command[printfOffset..];
        }
        using var wire = new MemoryStream();
        var native = await new CommandRunner().RunToStreamAsync(bash, ["-c", nativeCommand], wire,
            4096, TimeSpan.FromSeconds(15));
        var expected = unsupported ? "qfm-installed-digest:unsupported\n" : Digest();
        Assert.Equal(Encoding.UTF8.GetBytes(expected), wire.ToArray());
        Assert.Equal(unsupported ? 90 : 0, native.CommandResult.ExitCode);
        Assert.Equal("", native.CommandResult.StandardError);
        Assert.Equal(wire.Length, native.BytesWritten);

        // Replay bytes emitted by the real production printf through the public parser route.
        var replay = new DigestRunner(Encoding.UTF8.GetString(wire.ToArray()), native.CommandResult.ExitCode);
        var identity = await new AdbClient("adb", replay).ReadInstalledIdentityAsync(Serial, Artifact);
        Assert.Equal(Artifact.Identity, identity.Identity);
        Assert.Equal(Hash, identity.BaseApkSha256);
        Assert.Equal(4, identity.BaseApkSizeBytes);
        Assert.Equal(unsupported ? "host-streamed-sha256" : "same-opened-handle-device-sha256", identity.VerificationMethod);
        Assert.Equal(unsupported ? 1 : 0, replay.ApkStreams);
        Assert.Equal(2, replay.PackageReads);
    }

    [Fact]
    public async Task AdbExecOutSuccessWithExactUnsupportedWireUsesExplicitFallback()
    {
        // adb exec-out can discard the remote shell's exit 90. The wire body remains decisive.
        var runner = new DigestRunner("qfm-installed-digest:unsupported\n", 0);
        var result = await new AdbClient("adb", runner).ReadInstalledIdentityAsync(Serial, Artifact);
        Assert.Equal(Artifact.Identity, result.Identity);
        Assert.Equal("host-streamed-sha256", result.VerificationMethod);
        Assert.Equal("remote-handle-digest-capability-unsupported", result.VerificationFallbackReason);
        Assert.Equal(1, runner.ApkStreams);
    }

    [Theory]
    [InlineData(0, "qfm-installed-digest:unsupported\nextra", "", false)]
    [InlineData(0, "qfm-installed-digest:unsupported\n", "error", false)]
    [InlineData(1, "qfm-installed-digest:unsupported\n", "", false)]
    [InlineData(42, "qfm-installed-digest:unsupported\n", "", false)]
    [InlineData(0, "qfm-installed-digest:unsupported\n", "", true)]
    public async Task UnsupportedClassificationRejectsAnythingOtherThanCompleteExactCapabilityWire(
        int exitCode, string wire, string error, bool incomplete)
    {
        var runner = new DigestRunner(wire, exitCode, error) { IncompleteDigestCount = incomplete };
        var failure = await Record.ExceptionAsync(() => new AdbClient("adb", runner).ReadInstalledIdentityAsync(Serial, Artifact));
        Assert.True(failure is InvalidDataException or AdbCommandException);
        Assert.Equal(0, runner.ApkStreams);
    }

    private sealed class DigestRunner(string wire, int exitCode = 0, string error = "") : IStreamingCommandRunner
    {
        public bool ReplacePathOnRecheck { get; init; }
        public bool IncompleteDigestCount { get; init; }
        public int PackageReads { get; private set; }
        public int ApkStreams { get; private set; }
        public List<long> DigestBounds { get; } = [];
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments,
            TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Calls.Add(arguments.ToArray());
            Assert.Equal("shell", arguments[2]);
            Assert.Equal("pm path 'com.example.app'", arguments[3]);
            PackageReads++;
            var path = ReplacePathOnRecheck && PackageReads > 1 ? "/data/app/replacement/base.apk" : RemotePath;
            return Task.FromResult(new CommandResult(fileName, arguments, 0, $"package:{path}\n", "", TimeSpan.Zero));
        }

        public async Task<StreamingCommandResult> RunToStreamAsync(string fileName,
            IReadOnlyList<string> arguments, Stream destination, long maximumBytes,
            TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Calls.Add(arguments.ToArray());
            Assert.Equal("exec-out", arguments[2]);
            var digest = arguments.Any(arg => arg.Contains("qfm-installed-digest", StringComparison.Ordinal));
            byte[] bytes;
            if (digest)
            {
                DigestBounds.Add(maximumBytes);
                bytes = Encoding.UTF8.GetBytes(wire);
            }
            else
            {
                ApkStreams++;
                bytes = ApkBytes;
            }
            if (bytes.LongLength > maximumBytes) throw new FleetTransferLimitException(maximumBytes);
            await destination.WriteAsync(bytes, cancellationToken);
            return new(new CommandResult(fileName, arguments, digest ? exitCode : 0, "", digest ? error : "", TimeSpan.Zero),
                bytes.LongLength + (digest && IncompleteDigestCount ? 1 : 0), Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }
    }
}
