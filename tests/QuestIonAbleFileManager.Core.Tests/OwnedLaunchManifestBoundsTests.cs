using System.Collections;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.Core.Tests;

public sealed class OwnedLaunchManifestBoundsTests
{
    [Fact]
    public void OrdinaryPrivateFactsFitWithoutChangingThem()
    {
        var (artifact, installed, attempt, capture) = Facts();
        Require(artifact, installed, attempt, capture);
        Assert.Equal("com.example.fixture", artifact.Identity.PackageName);
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("escaped-token")]
    [InlineData("nested-command")]
    public void OversizedTokensAreRefusedBeforeSerialization(string field)
    {
        var (artifact, installed, attempt, capture) = Facts();
        var oversized = field == "escaped-token" ? new string('\u263a', 50_000) : new string('x', 262_145);
        if (field == "nested-command")
            attempt = attempt with { Launch = attempt.Launch! with
                { CommandResult = attempt.Launch.CommandResult with { StandardOutput = oversized } } };
        else attempt = attempt with { FailureMessage = oversized };
        var error = Assert.Throws<LocalApiException>(() => Require(artifact, installed, attempt, capture));
        Assert.Equal("diagnostic_manifest_invalid", error.Code);
    }

    [Fact]
    public void ExcessiveCollectionCountIsRefusedWithoutEnumeratingIt()
    {
        var (artifact, installed, attempt, capture) = Facts();
        attempt = attempt with { CurrentPackageProcessIds = new UnreadableLargeList() };
        Assert.Throws<LocalApiException>(() => Require(artifact, installed, attempt, capture));
    }

    [Fact]
    public void AggregateBoundStopsManyIndividuallySmallTokens()
    {
        var (artifact, installed, attempt, capture) = Facts();
        attempt = attempt with { Launch = attempt.Launch! with
            { CommandResult = attempt.Launch.CommandResult with
                { Arguments = Enumerable.Repeat(new string('x', 2048), 200).ToArray() } } };
        Assert.Throws<LocalApiException>(() => Require(artifact, installed, attempt, capture));
    }

    private static void Require(ApkArtifactInspection artifact, InstalledApkIdentity installed,
        ApkLaunchDiagnosticAttempt attempt, ApkLaunchDiagnosticCapture capture) =>
        OwnedLaunchManifestBounds.RequireValid(artifact, installed, installed, attempt, capture,
            new string('a', 32), "1793102400.000", "Exact launch observed.", null);

    private static (ApkArtifactInspection, InstalledApkIdentity, ApkLaunchDiagnosticAttempt, ApkLaunchDiagnosticCapture) Facts()
    {
        var artifact = new ApkArtifactInspection("retained://fixture/base.apk", 4, new string('b', 64),
            new("com.example.fixture", 1, "1", new string('a', 64), null));
        var installed = new InstalledApkIdentity("SYNTHETIC_TARGET", artifact.Identity,
            ["/data/app/base.apk"], artifact.Sha256, 4);
        var command = new CommandResult("adb", ["-s", installed.Serial, "shell", "am", "start", "-n",
            "com.example.fixture/.MainActivity"], 0, "", "", TimeSpan.Zero);
        var launch = new ResolvedAppLaunchResult(artifact, installed, "com.example.fixture/.MainActivity", command, true);
        return (artifact, installed, new(true, launch, null, null, [1234]),
            new("logcat-uid-post-fence.txt", 0, new string('c', 64), true, false, false, true, 0));
    }

    private sealed class UnreadableLargeList : IReadOnlyList<int>
    {
        public int Count => 262_145;
        public int this[int index] => throw new InvalidOperationException("Collection must be refused by count.");
        public IEnumerator<int> GetEnumerator() => throw new InvalidOperationException("Do not enumerate oversized facts.");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
