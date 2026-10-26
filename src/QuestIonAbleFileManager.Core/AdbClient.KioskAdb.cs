using System.Diagnostics;
using System.Text;

namespace QuestIonAbleFileManager.Core;

public sealed partial class AdbClient
{
    public async Task<RustyKioskAdbResult> GetRustyKioskAdbInstallationAsync(string serial,
        RustyKioskProductContract? product = null, CancellationToken cancellationToken = default)
    {
        serial = AndroidInput.RequireSerial(serial); product = KioskProduct(product);
        var bounded = BoundedKioskClient();
        var installation = BoundKioskInstallation(await bounded.GetRustyKioskInstallationStatusAsync(serial, product, cancellationToken).ConfigureAwait(false));
        return new(serial, product.WireName, null, null, null, installation, false, RustyKioskAdbResultState.InstallationOnly, null,
            "Installation and provider contract facts checked; no Kiosk activity requested.");
    }

    // Additive preallocated-ID seam. Historical InvokeRustyKioskAsync retains its exact signature and behavior.
    public Task<RustyKioskAdbResult> InvokeRustyKioskWithRequestIdAsync(string serial, string providerRequestId,
        RustyKioskCommand expectedCommand, string? expectedValue = null, CancellationToken cancellationToken = default,
        RustyKioskProductContract? product = null) =>
        InvokeKioskAdbRequestAsync(serial, providerRequestId, expectedCommand, expectedValue, product, cancellationToken, null);

    internal async Task<RustyKioskAdbResult> InvokeKioskAdbRequestAsync(string serial, string providerRequestId,
        RustyKioskCommand expectedCommand, string? expectedValue, RustyKioskProductContract? product,
        CancellationToken cancellationToken, Action? dispatchObserved)
    {
        serial = AndroidInput.RequireSerial(serial); product = KioskProduct(product);
        providerRequestId = RustyKioskAdbContract.RequireProviderRequestId(providerRequestId);
        expectedValue = RustyKioskAdbContract.RequireExpectedValue(expectedCommand, expectedValue);
        var bounded = BoundedKioskClient();
        var installation = BoundKioskInstallation(await bounded.GetRustyKioskInstallationStatusAsync(serial, product, cancellationToken).ConfigureAwait(false));
        if (!installation.HostOperatorAvailable)
            return new(serial, product.WireName, providerRequestId, expectedCommand, expectedValue, installation, false,
                RustyKioskAdbResultState.InstallationOnly, null,
                expectedCommand == RustyKioskCommand.Select ? "Installation facts checked; selection not performed because the operator is unavailable." :
                    "Installation facts checked; catalog not requested because the operator is unavailable.");
        var arguments = new List<string> { "shell", "content", "call", "--uri", product.OperatorUri, "--method", "invoke", "--arg", expectedCommand.ToWireName(), "--extra", "request_id:s:" + providerRequestId };
        if (expectedValue is not null) arguments.AddRange(["--extra", "value_base64:s:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(expectedValue))]);
        // Provider admission itself may retain a pending request; preserve this marker even if later activity/readback fails.
        dispatchObserved?.Invoke();
        var admitted = await bounded.RunForDeviceAsync(serial, arguments, InspectionTimeout, cancellationToken).ConfigureAwait(false);
        admitted.EnsureSuccess("Admit bounded Kiosk request");
        if (BundleBoolean(admitted.StandardOutput, "accepted") != true)
            return new(serial, product.WireName, providerRequestId, expectedCommand, expectedValue, installation, false,
                RustyKioskAdbResultState.Rejected, null, "The provider rejected the catalog or selection request; no Kiosk activity requested.");
        var activity = await bounded.RunForDeviceAsync(serial,
            ["shell", "am", "start", "-W", "-n", product.MainActivity, "--es", RustyKioskContract.PendingRequestExtra, providerRequestId],
            ConnectionTimeout, cancellationToken).ConfigureAwait(false);
        activity.EnsureSuccess("Request Kiosk activity for bounded typed execution");
        if (activity.CondensedOutput.Contains("Error:", StringComparison.OrdinalIgnoreCase) ||
            activity.CondensedOutput.Contains("Permission Denial", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The Kiosk activity request did not acknowledge typed dispatch.");
        var timer = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = TimeSpan.FromSeconds(15) - timer.Elapsed;
            if (remaining <= TimeSpan.Zero)
                return new(serial, product.WireName, providerRequestId, expectedCommand, expectedValue, installation, true,
                    RustyKioskAdbResultState.Pending, null, "No completed provider result was observed in the bounded readback window.");
            var facts = await bounded.ReadKioskAdbResultAsync(serial, providerRequestId, expectedCommand, expectedValue, product,
                remaining < InspectionTimeout ? remaining : InspectionTimeout, cancellationToken).ConfigureAwait(false);
            if (facts.Result is not null)
                return facts with { Installation = installation, ActivityOpenRequested = true };
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<RustyKioskAdbResult> ReadRustyKioskRequestAsync(string serial, string providerRequestId,
        RustyKioskCommand expectedCommand, string? expectedValue = null, RustyKioskProductContract? product = null,
        CancellationToken cancellationToken = default)
    {
        serial = AndroidInput.RequireSerial(serial); product = KioskProduct(product);
        providerRequestId = RustyKioskAdbContract.RequireProviderRequestId(providerRequestId);
        expectedValue = RustyKioskAdbContract.RequireExpectedValue(expectedCommand, expectedValue);
        return await BoundedKioskClient().ReadKioskAdbResultAsync(serial, providerRequestId, expectedCommand, expectedValue,
            product, InspectionTimeout, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RustyKioskAdbResult> ReadKioskAdbResultAsync(string serial, string id, RustyKioskCommand command, string? value,
        RustyKioskProductContract product, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var result = await RunForDeviceAsync(serial, ["shell", "content", "call", "--uri", product.OperatorUri, "--method", "result", "--arg", id], timeout, cancellationToken).ConfigureAwait(false);
        result.EnsureSuccess("Read bounded original Kiosk result facts");
        // The current provider contract does not distinguish missing from queued here.
        // completed:false is never converted into a missing/no-effect assertion.
        if (BundleBoolean(result.StandardOutput, "completed") != true)
            return new(serial, product.WireName, id, command, value, null, false, RustyKioskAdbResultState.Pending, null,
                "The provider has not supplied a completed result; request existence and effects remain unknown.");
        var encoded = BundleValue(result.StandardOutput, "result_base64") ?? throw new InvalidDataException("A completed Kiosk response omitted its result.");
        var bytes = Convert.FromBase64String(encoded);
        if (bytes.Length > RustyKioskAdbContract.MaximumResultBytes) throw new InvalidDataException("Kiosk result byte limit exceeded.");
        var parsed = BoundKioskResult(RustyKioskOperatorResult.Parse(new UTF8Encoding(false, true).GetString(bytes)));
        if (parsed.RequestId != id || parsed.Command != command) throw new InvalidDataException("Kiosk original result ID or command did not match.");
        var state = !parsed.Accepted ? RustyKioskAdbResultState.Rejected : !parsed.Completed ||
            (command == RustyKioskCommand.Select && parsed.State.SelectedKey != value) ? RustyKioskAdbResultState.Pending : RustyKioskAdbResultState.Completed;
        return new(serial, product.WireName, id, command, value, null, false, state, parsed,
            state == RustyKioskAdbResultState.Completed ? "Matching completed provider result facts read; no foreground or application readiness claim." :
            state == RustyKioskAdbResultState.Rejected ? "The provider result reports rejection." : "The expected completed selection was not observed.");
    }

    private AdbClient BoundedKioskClient() => _runner is IStreamingCommandRunner streaming
        ? new AdbClient(AdbPath, new BoundedKioskRunner(streaming), _buildTools)
        : throw new NotSupportedException("The bounded Kiosk ADB workflow requires a streaming command runner.");
    private static RustyKioskProductContract KioskProduct(RustyKioskProductContract? product) =>
        RustyKioskProductContract.RequireKnown(product ?? RustyKioskProductContract.For(RustyKioskProductChannel.Stable));
    private static RustyKioskInstallationStatus BoundKioskInstallation(RustyKioskInstallationStatus installation) =>
        installation.MainVersion?.Length > 256 || installation.SetupHelperVersion?.Length > 256
            ? throw new InvalidDataException("Kiosk installation version text exceeded its bound.") : installation;

    private sealed class BoundedKioskRunner(IStreamingCommandRunner runner) : ICommandRunner
    {
        public async Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            using var output = new MemoryStream();
            var result = await runner.RunToStreamAsync(fileName, arguments, output, RustyKioskAdbContract.MaximumResultBytes,
                timeout, cancellationToken).ConfigureAwait(false);
            if (output.Length > RustyKioskAdbContract.MaximumResultBytes || result.BytesWritten != output.Length ||
                !string.IsNullOrEmpty(result.CommandResult.StandardOutput) || !string.IsNullOrEmpty(result.CommandResult.StandardError))
                throw new InvalidDataException("Bounded Kiosk output was oversized, incomplete, or contained unexpected diagnostics.");
            return result.CommandResult with { StandardOutput = new UTF8Encoding(false, true).GetString(output.ToArray()) };
        }
    }

    private static RustyKioskOperatorResult BoundKioskResult(RustyKioskOperatorResult result)
    {
        static void Text(string? value, int maximum) { if (value?.Length > maximum) throw new InvalidDataException("Kiosk result text limit exceeded."); }
        var state = result.State;
        Text(result.Message, 2048); Text(state.StatusLine, 2048); Text(state.Search, 160); Text(state.TagFilter, 160);
        Text(state.SelectedKey, 160); Text(state.SelectedName, 256); Text(state.SelectedPackage, 256); Text(state.OperationInProgress, 256);
        Text(state.TagFilePath, 1024); Text(state.PendingRequirementLaunchId, 256); Text(state.SelectedLaunchOptionsMessage, 2048);
        Text(state.LastDispatchedOptionId, 160); Text(state.LastDispatchedOptionPackage, 256);
        if (state.InstalledCount < 0 || state.NotInstalledCount < 0 || state.VisibleCount < 0) throw new InvalidDataException("Kiosk catalog counts were invalid.");
        foreach (var entry in state.Entries)
        {
            Text(entry.Key, 160); Text(entry.Name, 256); Text(entry.PackageName, 256);
            if (entry.Tags.Count > 64) throw new InvalidDataException("Kiosk entry tag count exceeded its bound.");
            foreach (var tag in entry.Tags) Text(tag, 160);
        }
        if (state.SelectedLaunchOptionsBinding is { } binding)
        {
            Text(binding.PackageName, 256); Text(binding.SigningIdentity, 64); Text(binding.ProviderAuthority, 256);
            Text(binding.ProviderClass, 512); Text(binding.OwnerActivity, 512); Text(binding.BindingSha256, 64);
        }
        return state.Entries.Count <= RustyKioskAdbContract.MaximumCatalogEntries ? result : result with
        { State = state with { Entries = state.Entries.Take(RustyKioskAdbContract.MaximumCatalogEntries).ToArray(), VisibleEntriesTruncated = true } };
    }
}
