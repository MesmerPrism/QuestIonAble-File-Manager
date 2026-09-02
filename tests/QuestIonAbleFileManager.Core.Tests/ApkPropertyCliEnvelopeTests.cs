using System.Text.Json;

namespace QuestIonAbleFileManager.Core.Tests;

[Collection("Console output")]
public sealed class ApkPropertyCliEnvelopeTests
{
    [Fact]
    public async Task FailureIsOneSanitizedSchemaBoundJsonEnvelope()
    {
        var privateSerial = "PRIVATE-PROPERTY-SERIAL-DO-NOT-EMIT";
        var privateApk = Path.Combine(Path.GetTempPath(), "private-property-app-DO-NOT-EMIT.apk");
        var privateManifest = Path.Combine(Path.GetTempPath(), "private-property-manifest-DO-NOT-EMIT.json");
        var privateOutput = Path.Combine(Path.GetTempPath(), "private-property-snapshot-DO-NOT-EMIT.json");
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            var exit = await CliApplication.RunAsync(
            [
                "apk", "properties", "observe", "--serial", privateSerial,
                "--file", privateApk, "--manifest", privateManifest,
                "--output", privateOutput, "--json"
            ]);
            Assert.Equal(2, exit);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }

        Assert.Equal(string.Empty, error.ToString());
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal(
            "questionable.file_manager.apk_property_observation_result.v1",
            document.RootElement.GetProperty("schema").GetString());
        Assert.False(document.RootElement.GetProperty("succeeded").GetBoolean());
        Assert.Equal(
            "input_rejected",
            document.RootElement.GetProperty("failure").GetProperty("code").GetString());
        Assert.DoesNotContain(privateSerial, output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(privateApk, output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(privateManifest, output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(privateOutput, output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DispatchedFailureEnvelopePreservesPendingReceiptAndStateChangePossibility()
    {
        var receipt = new OperatorMutationReceipt(
            "pc-test",
            OperatorCommandKind.ClearExactApkProperties,
            "QUEST123",
            "clear exact inspected-APK property manifest",
            OperatorMutationStage.Pending,
            "No matching effective state was confirmed.",
            HeadsetReadback: false,
            [
                new(OperatorMutationStage.Sent, DateTimeOffset.UnixEpoch, "sent"),
                new(OperatorMutationStage.Pending, DateTimeOffset.UnixEpoch.AddSeconds(1), "pending")
            ]);
        var exception = new OperatorMutationExecutionException(
            receipt,
            new InvalidOperationException("private inner detail must not be emitted"));
        var originalOut = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            Assert.Equal(1, CliApplication.WriteExactApkPropertyFailureJson(
                exception,
                mutationRequested: true));
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        using var document = JsonDocument.Parse(output.ToString());
        var root = document.RootElement;
        Assert.False(root.GetProperty("succeeded").GetBoolean());
        Assert.Equal("pending", root.GetProperty("mutation").GetProperty("Stage").GetString());
        Assert.Equal(2, root.GetProperty("mutation").GetProperty("Transitions").GetArrayLength());
        Assert.Equal("cleanup_unknown", root.GetProperty("failure").GetProperty("code").GetString());
        Assert.True(root.GetProperty("failure").GetProperty("state_change_possible").GetBoolean());
        Assert.DoesNotContain("private inner detail", output.ToString(), StringComparison.Ordinal);
    }
}
