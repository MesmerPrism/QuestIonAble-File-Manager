namespace QuestIonAbleFileManager.Core;

public static partial class OperatorCommands
{
    public static OperatorCommand InspectRustyKioskInstallation(string serial, RustyKioskProductContract? product = null)
    {
        serial = AndroidInput.RequireSerial(serial); product = Product(product);
        return new(OperatorCommandKind.InspectRustyKioskInstallation,
            ["kiosk", "installation", "--serial", serial, "--product-channel", product.WireName], serial: serial, rustyKioskProduct: product);
    }

    public static OperatorCommand OpenRustyKioskCatalog(string serial, string providerRequestId,
        bool operatorConfirmed = false, RustyKioskProductContract? product = null) =>
        KioskAdbRequest(OperatorCommandKind.OpenRustyKioskCatalog, "catalog-open", serial, providerRequestId,
            RustyKioskCommand.Status, null, operatorConfirmed, product);

    public static OperatorCommand SelectRustyKioskApp(string serial, string key, string providerRequestId,
        bool operatorConfirmed = false, RustyKioskProductContract? product = null) =>
        KioskAdbRequest(OperatorCommandKind.SelectRustyKioskApp, "select", serial, providerRequestId,
            RustyKioskCommand.Select, key, operatorConfirmed, product);

    public static OperatorCommand ReadRustyKioskRequest(string serial, string providerRequestId, RustyKioskCommand expectedCommand,
        string? expectedValue = null, RustyKioskProductContract? product = null) =>
        KioskAdbRequest(OperatorCommandKind.ReadRustyKioskRequest, "original-result", serial, providerRequestId,
            expectedCommand, expectedValue, false, product);

    public static OperatorCommand ParseKioskAdbCliArguments(IReadOnlyList<string> arguments)
    {
        if (arguments.Count < 2 || arguments[0] != "kiosk" || arguments[1] is not ("installation" or "catalog-open" or "select" or "original-result"))
            throw new ArgumentException("A closed Kiosk ADB workflow route is required.", nameof(arguments));
        var action = arguments[1];
        var options = new Dictionary<string, string?>(StringComparer.Ordinal);
        var allowed = new HashSet<string>(["--serial", "--product-channel", "--adb", "--json"], StringComparer.Ordinal);
        if (action != "installation") allowed.Add("--request-id");
        if (action is "catalog-open" or "select") allowed.Add("--confirm-kiosk-control");
        if (action is "select" or "original-result") allowed.Add("--key");
        if (action == "original-result") allowed.Add("--expected-command");
        for (var index = 2; index < arguments.Count; index++)
        {
            var name = arguments[index];
            if (!allowed.Contains(name) || options.ContainsKey(name)) throw new ArgumentException("Unknown or duplicate Kiosk ADB option.", nameof(arguments));
            if (name is "--json" or "--confirm-kiosk-control") options.Add(name, null);
            else
            {
                if (++index >= arguments.Count)
                    throw new ArgumentException("A Kiosk ADB option value is missing.", nameof(arguments));
                options.Add(name, arguments[index]);
            }
        }
        string Required(string name) => options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value :
            throw new ArgumentException("A required Kiosk ADB option is missing.", nameof(arguments));
        var serial = Required("--serial"); var channel = Required("--product-channel");
        if (channel is not ("stable" or "labs")) throw new ArgumentException("An exact stable or labs channel is required.", nameof(arguments));
        var product = RustyKioskProductContract.Parse(channel);
        if (action == "installation") return InspectRustyKioskInstallation(serial, product);
        var id = Required("--request-id");
        if (action == "catalog-open") return OpenRustyKioskCatalog(serial, id, options.ContainsKey("--confirm-kiosk-control"), product);
        if (action == "select") return SelectRustyKioskApp(serial, Required("--key"), id, options.ContainsKey("--confirm-kiosk-control"), product);
        var expected = Required("--expected-command") switch
        { "status" => RustyKioskCommand.Status, "select" => RustyKioskCommand.Select, _ => throw new ArgumentException("Only status or select result facts may be requested.", nameof(arguments)) };
        return ReadRustyKioskRequest(serial, id, expected, options.GetValueOrDefault("--key"), product);
    }

    private static RustyKioskProductContract Product(RustyKioskProductContract? product) =>
        RustyKioskProductContract.RequireKnown(product ?? RustyKioskProductContract.For(RustyKioskProductChannel.Stable));

    private static OperatorCommand KioskAdbRequest(OperatorCommandKind kind, string action, string serial, string providerRequestId,
        RustyKioskCommand command, string? value, bool confirmed, RustyKioskProductContract? product)
    {
        serial = AndroidInput.RequireSerial(serial); product = Product(product);
        providerRequestId = RustyKioskAdbContract.RequireProviderRequestId(providerRequestId);
        value = RustyKioskAdbContract.RequireExpectedValue(command, value);
        var mutates = kind != OperatorCommandKind.ReadRustyKioskRequest;
        if (mutates) RequireApproval(confirmed, "Explicit Kiosk catalog activity or selection request");
        var arguments = new List<string> { "kiosk", action, "--serial", serial, "--product-channel", product.WireName, "--request-id", providerRequestId };
        if (!mutates) arguments.AddRange(["--expected-command", command.ToWireName()]);
        if (value is not null) arguments.AddRange(["--key", value]);
        if (mutates) arguments.Add("--confirm-kiosk-control");
        return new(kind, arguments, serial: serial, operatorConfirmed: confirmed, rustyKioskCommand: command,
            rustyKioskValue: value, rustyKioskProduct: product, rustyKioskProviderRequestId: providerRequestId);
    }
}
