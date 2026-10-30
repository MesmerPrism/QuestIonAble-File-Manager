using System.Net;
using System.Text;
using System.Text.Json;
using QuestIonAbleFileManager.BrowserHost;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost.Tests;

public sealed class BrowserHostTests
{
    private static readonly string Credential = new('s', 40);
    private static byte[] Request(string action, string extra = "", string? id = null) =>
        Encoding.UTF8.GetBytes($$"""{"requestId":"{{id ?? Guid.NewGuid().ToString("D")}}","action":"{{action}}"{{extra}}} """);

    [Theory]
    [InlineData("http://localhost:8123/")]
    [InlineData("http://0.0.0.0:8123/")]
    [InlineData("http://192.0.2.1:8123/")]
    [InlineData("https://127.0.0.1:8123/")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://127.0.0.1:0/")]
    [InlineData("http://127.0.0.1:8123/path")]
    [InlineData("http://user@127.0.0.1:8123/")]
    [InlineData("http://127.0.0.1:8123/?token=secret")]
    public void ListenerRequiresExplicitNumericLoopback(string listen) =>
        Assert.Throws<BrowserRequestException>(() => new BrowserSecurity(listen, Credential));

    [Theory]
    [InlineData("http://127.0.0.1:8123/", "127.0.0.1:8123", "http://127.0.0.1:8123", "127.0.0.1")]
    [InlineData("http://[::1]:8123/", "[::1]:8123", "http://[::1]:8123", "::1")]
    public void HostOriginRemoteAndBearerMustAgree(string listen, string host, string origin, string remote)
    {
        var security = new BrowserSecurity(listen, Credential);
        Assert.True(security.AllowsRequest(host, origin, "same-origin", IPAddress.Parse(remote)));
        Assert.True(security.AllowsRequest(host, null, null, IPAddress.Parse(remote)));
        Assert.False(security.AllowsRequest("attacker.example:8123", origin, "same-origin", IPAddress.Parse(remote)));
        Assert.False(security.AllowsRequest(host, "https://mesmerprism.com", "cross-site", IPAddress.Parse(remote)));
        Assert.False(security.AllowsRequest(host, "null", null, IPAddress.Parse(remote)));
        Assert.False(security.AllowsRequest(host, origin, "cross-site", IPAddress.Parse(remote)));
        Assert.False(security.AllowsRequest(host, origin, "same-origin", IPAddress.Parse("192.0.2.1")));
        Assert.True(security.Authenticate("Bearer " + Credential));
        Assert.False(security.Authenticate("Bearer " + Credential + "x"));
        Assert.False(security.Authenticate("bearer " + Credential));
    }

    [Theory]
    [InlineData("/app.js", true)]
    [InlineData("/apk-set-view.js", true)]
    [InlineData("/apk-install-view.js", true)]
    [InlineData("/apk-install-view.js/private.apk", false)]
    [InlineData("/apk-set-view.js/private.apk", false)]
    [InlineData("/usb-proof.js", true)]
    [InlineData("/vendor/tango-adb.js", true)]
    [InlineData("/../LICENSE", false)]
    [InlineData("/config.json", false)]
    [InlineData("/other.js", false)]
    public void StaticFilesAreClosed(string path, bool allowed) => Assert.Equal(allowed, BrowserSecurity.IsStaticPath(path));

    [Theory]
    [InlineData("GET", "/", "navigate", "document", true)]
    [InlineData("GET", "/index.html", "navigate", "document", true)]
    [InlineData("GET", "/browser/v1/session", "navigate", "document", false)]
    [InlineData("POST", "/index.html", "navigate", "document", false)]
    [InlineData("GET", "/config.json", "navigate", "document", false)]
    [InlineData("GET", "/app.js", "cors", "script", false)]
    [InlineData("GET", "/", "cors", "empty", false)]
    public void PublicLinkCanNavigateToInertStaticPageButCannotAccessApi(
        string method, string path, string mode, string destination, bool allowed)
    {
        var security = new BrowserSecurity("http://127.0.0.1:8123/", Credential);
        var navigation = BrowserSecurity.IsStaticNavigation(method, path, mode, destination);
        Assert.Equal(allowed, security.AllowsRequest("127.0.0.1:8123", "https://mesmerprism.com",
            "cross-site", IPAddress.Loopback, navigation));
        Assert.False(security.AllowsRequest("attacker.example:8123", "https://mesmerprism.com",
            "cross-site", IPAddress.Loopback, navigation));
        Assert.False(security.AllowsRequest("127.0.0.1:8123", "https://mesmerprism.com",
            "cross-site", IPAddress.Parse("192.0.2.1"), navigation));
    }

    [Theory]
    [InlineData("device.keep-awake", ",\"serial\":\"QUEST123\",\"durationMilliseconds\":59999,\"confirmed\":true")]
    [InlineData("device.keep-awake", ",\"serial\":\"QUEST123\",\"durationMilliseconds\":28800001,\"confirmed\":true")]
    [InlineData("device.keep-awake", ",\"serial\":\"QUEST123\",\"durationMilliseconds\":60000,\"confirmed\":false")]
    [InlineData("device.normal", ",\"serial\":\"QUEST123\",\"confirmed\":true,\"shell\":\"whoami\"")]
    [InlineData("devices", ",\"serial\":\"QUEST123\"")]
    [InlineData("wifi.connect", ",\"serial\":\"other:5555\",\"host\":\"192.0.2.1\",\"port\":5555,\"confirmed\":true")]
    [InlineData("wifi.enable", ",\"serial\":\"QUEST123\",\"port\":0,\"confirmed\":true")]
    [InlineData("fleet", "")]
    public void InvalidActionsCannotReachExecutor(string action, string extra) =>
        Assert.Throws<BrowserRequestException>(() => BrowserCommands.Parse(Request(action, extra)));

    [Fact]
    public void StrictJsonRejectsDuplicateCaseUnknownAndOversize()
    {
        Assert.Throws<BrowserRequestException>(() => BrowserCommands.Parse(Request("devices", ",\"action\":\"devices\"")));
        Assert.Throws<BrowserRequestException>(() => BrowserCommands.Parse(Request("devices", ",\"Action\":\"devices\"")));
        Assert.Throws<BrowserRequestException>(() => BrowserCommands.Parse(new byte[4097]));
        Assert.Throws<BrowserRequestException>(() => BrowserCommands.Parse("[]"u8.ToArray()));
    }

    [Fact]
    public void AllowedMutationsBuildExistingImmutableCliEquivalent()
    {
        var hold = BrowserCommands.Parse(Request("device.keep-awake",
            ",\"serial\":\"QUEST123\",\"durationMilliseconds\":60000,\"confirmed\":true"));
        Assert.Equal(OperatorCommands.SetQuestKeepAwake("QUEST123", true, 60000, true).CliArguments, hold.Command.CliArguments);
        var connect = BrowserCommands.Parse(Request("wifi.connect",
            ",\"serial\":\"192.0.2.1:5555\",\"host\":\"192.0.2.1\",\"port\":5555,\"confirmed\":true"));
        Assert.Equal(OperatorCommands.ConnectWifiAdb("192.0.2.1", 5555, true).CliArguments, connect.Command.CliArguments);
    }

    [Fact]
    public async Task DiscoveryUsesRealSharedExecutorWithFakeRunnerOnly()
    {
        var runner = new FakeRunner();
        var registry = new BrowserCommands(new OperatorCommandExecutor(new AdbClient("fake-adb", runner)));
        Assert.Empty(runner.Calls);
        var outcome = await registry.ExecuteAsync(Request("devices"));
        Assert.Equal("completed", outcome.State);
        Assert.Single(outcome.Result!.Devices!);
        Assert.Equal(new[] { "devices", "-l" }, Assert.Single(runner.Calls));
    }

    [Fact]
    public async Task DisconnectedEndpointCanConnectBeforeReadyReadbackWithoutCanonicalClaim()
    {
        var runner = new ConnectRunner();
        var registry = new BrowserCommands(new OperatorCommandExecutor(new AdbClient("fake-adb", runner)));
        var outcome = await registry.ExecuteAsync(Request("wifi.connect",
            ",\"serial\":\"192.0.2.1:5555\",\"host\":\"192.0.2.1\",\"port\":5555,\"confirmed\":true"));
        Assert.Equal("completed", outcome.State);
        Assert.Equal(new[] { "connect", "192.0.2.1:5555" }, runner.Calls[0]);
        Assert.All(runner.Calls.Skip(1), call => Assert.Equal(new[] { "devices", "-l" }, call));
        Assert.Null(outcome.Result!.Command.Serial);
        Assert.Equal("192.0.2.1:5555", outcome.Result.WifiAdbConnectionResult!.Endpoint);
        Assert.Null(outcome.Result.MutationReceipt);
    }

    [Fact]
    public async Task PendingReadbackNeverBecomesCompletedAndReplayCannotDispatch()
    {
        var calls = 0;
        var registry = new BrowserCommands((command, _) =>
        {
            calls++;
            return Task.FromResult(new OperatorExecutionResult(command));
        });
        var body = Request("device.keep-awake", ",\"serial\":\"QUEST123\",\"durationMilliseconds\":60000,\"confirmed\":true");
        var outcome = await registry.ExecuteAsync(body);
        Assert.Equal("pending", outcome.State);
        Assert.Equal(outcome, registry.Get(outcome.RequestId));
        var error = await Assert.ThrowsAsync<BrowserRequestException>(() => registry.ExecuteAsync(body));
        Assert.Equal("request_already_consumed", error.Code);
        Assert.Null(error.Dispatched);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(60000, "completed", OperatorMutationStage.Confirmed)]
    [InlineData(120000, "pending", OperatorMutationStage.Pending)]
    public async Task RealSharedMutationHandlerRequiresExactEffectiveHoldReadback(
        int reportedDuration, string state, OperatorMutationStage stage)
    {
        var runner = new PowerRunner(reportedDuration);
        var registry = new BrowserCommands(new OperatorCommandExecutor(new AdbClient("fake-adb", runner)));
        var outcome = await registry.ExecuteAsync(Request("device.keep-awake",
            ",\"serial\":\"QUEST123\",\"durationMilliseconds\":60000,\"confirmed\":true"));
        Assert.Equal(state, outcome.State);
        Assert.Equal(stage, outcome.Result!.MutationReceipt!.Stage);
        Assert.Contains(runner.Calls, call => call.SequenceEqual(new[]
        {
            "-s", "QUEST123", "shell", "am", "broadcast", "-a", "com.oculus.vrpowermanager.prox_close", "--ei", "duration", "60000"
        }));
        Assert.All(runner.Calls.Where(call => call[0] != "devices"), call =>
            Assert.Equal(new[] { "-s", "QUEST123" }, call.Take(2)));
    }

    [Fact]
    public async Task ExceptionIsSanitizedUncertainAndRetained()
    {
        var registry = new BrowserCommands((_, _) => throw new IOException("private-path secret-token raw-output"));
        var body = Request("device.normal", ",\"serial\":\"QUEST123\",\"confirmed\":true");
        var outcome = await registry.ExecuteAsync(body);
        Assert.Equal("outcomeUnknown", outcome.State);
        var serialized = JsonSerializer.Serialize(outcome);
        Assert.DoesNotContain("private-path", serialized);
        Assert.DoesNotContain("secret-token", serialized);
        await Assert.ThrowsAsync<BrowserRequestException>(() => registry.ExecuteAsync(body));
    }

    [Fact]
    public async Task RestartChangesSessionAndCapacityDoesNotEvictReplayEvidence()
    {
        var registry = new BrowserCommands((command, _) => Task.FromResult(new OperatorExecutionResult(command)));
        var restarted = new BrowserCommands((command, _) => Task.FromResult(new OperatorExecutionResult(command)));
        Assert.Throws<BrowserRequestException>(() => restarted.RequireSession(registry.SessionId));
        registry.RequireSession(registry.SessionId);
        var first = await registry.ExecuteAsync(Request("devices"));
        for (var i = 1; i < BrowserCommands.MaximumRequests; i++) await registry.ExecuteAsync(Request("devices"));
        var error = await Assert.ThrowsAsync<BrowserRequestException>(() => registry.ExecuteAsync(Request("devices")));
        Assert.Equal("session_capacity_reached", error.Code);
        Assert.Equal(first, registry.Get(first.RequestId));
    }

    [Fact]
    public async Task SingleInflightActionAndStatusSurvivesPageDisconnect()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new BrowserCommands(async (command, _) =>
        {
            entered.SetResult();
            await release.Task;
            return new OperatorExecutionResult(command);
        });
        var body = Request("devices");
        var id = BrowserCommands.Parse(body).RequestId;
        var running = registry.ExecuteAsync(body);
        await entered.Task;
        Assert.Equal("running", registry.Get(id).State);
        var duplicate = await Assert.ThrowsAsync<BrowserRequestException>(() => registry.ExecuteAsync(body));
        Assert.Equal("request_already_consumed", duplicate.Code);
        Assert.Null(duplicate.Dispatched);
        var error = await Assert.ThrowsAsync<BrowserRequestException>(() => registry.ExecuteAsync(Request("devices")));
        Assert.Equal("busy", error.Code);
        Assert.False(error.Dispatched);
        release.SetResult();
        Assert.Equal("completed", (await running).State);
    }

    private sealed class FakeRunner : ICommandRunner
    {
        public List<string[]> Calls { get; } = [];
        public Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(arguments.ToArray());
            Assert.Equal(new[] { "devices", "-l" }, arguments);
            return Task.FromResult(new CommandResult(fileName, arguments, 0,
                "List of devices attached\nQUEST123 device product:test model:Quest transport_id:1\n", "", TimeSpan.Zero));
        }
    }

    private sealed class PowerRunner(int duration) : ICommandRunner
    {
        public List<string[]> Calls { get; } = [];
        public Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(arguments.ToArray());
            var joined = string.Join(" ", arguments);
            var output = joined switch
            {
                "devices -l" => "List of devices attached\nQUEST123 device model:Quest\n",
                "-s QUEST123 shell dumpsys power" => "mStayOn=true\nmWakefulness=Awake\nDisplay Power: state=ON\n",
                "-s QUEST123 shell dumpsys vrpowermanager" =>
                    $"Virtual proximity state: CLOSE\n1.0s (1.0s ago) - received com.oculus.vrpowermanager.prox_close broadcast: duration={duration}\n",
                _ => ""
            };
            return Task.FromResult(new CommandResult(fileName, arguments, 0, output, "", TimeSpan.Zero));
        }
    }

    private sealed class ConnectRunner : ICommandRunner
    {
        public List<string[]> Calls { get; } = [];
        private bool _connected;
        public Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(arguments.ToArray());
            string output;
            if (arguments.SequenceEqual(new[] { "connect", "192.0.2.1:5555" }))
            {
                _connected = true;
                output = "connected to 192.0.2.1:5555";
            }
            else
            {
                Assert.Equal(new[] { "devices", "-l" }, arguments);
                output = "List of devices attached\n" + (_connected ? "192.0.2.1:5555 device model:Quest\n" : "");
            }
            return Task.FromResult(new CommandResult(fileName, arguments, 0, output, "", TimeSpan.Zero));
        }
    }
}
