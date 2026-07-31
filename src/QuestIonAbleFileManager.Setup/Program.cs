using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using QuestIonAbleFileManager.Core;
using Windows.Foundation;
using Windows.Management.Deployment;

namespace QuestIonAbleFileManager.Setup;

internal static class DistributionIdentity
{
    private static readonly IReadOnlyDictionary<string, string> Metadata =
        Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(attribute => attribute.Key, attribute => attribute.Value ?? string.Empty);

    internal static string ProductChannel => Required("QuestIonAbleFileManager.ProductChannel");
    internal static string Maturity => Required("QuestIonAbleFileManager.Maturity");
    internal static string DistributionTrack => Required("QuestIonAbleFileManager.DistributionTrack");
    internal static string PackageIdentity => Required("QuestIonAbleFileManager.PackageIdentity");
    internal static string DisplayName => Required("QuestIonAbleFileManager.DistributionDisplayName");
    internal static string AssetStem => Required("QuestIonAbleFileManager.SetupAssetStem");
    internal static string ReleaseTag => Required("QuestIonAbleFileManager.ReleaseTag");
    internal static bool IsLabs
    {
        get
        {
            ValidateAxes();
            return ProductChannel == "labs";
        }
    }
    internal static bool FleetReplayProtectionEnabled => !IsLabs;

    private static void ValidateAxes()
    {
        if (ProductChannel is not ("stable" or "labs") ||
            Maturity is not ("alpha" or "beta" or "rc" or "released") ||
            DistributionTrack is not ("github-release" or "github-prerelease") ||
            (ProductChannel == "stable" && DistributionTrack != "github-release") ||
            (ProductChannel == "labs" && DistributionTrack != "github-prerelease"))
        {
            throw new InvalidOperationException(
                "The embedded product_channel, maturity, and distribution_track axes are invalid.");
        }
    }

    internal static void RejectLabsFleetReplayOperation(
        bool repair,
        bool destructiveReset)
    {
        if (IsLabs && (repair || destructiveReset))
        {
            throw new ArgumentException(
                "Labs Setup cannot read, repair, reset, or provision the stable Fleet replay authority.");
        }
    }

    internal static void RejectLabsFleetReplayArguments(string[] args)
    {
        if (IsLabs &&
            args.Any(argument =>
                argument.StartsWith(
                    "--fleet-replay",
                    StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "Labs Setup cannot invoke stable Fleet replay authority routes.");
        }
    }

    private static string Required(string key) =>
        Metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Required closed distribution metadata is missing: {key}.");
}

internal sealed record InstallerOptions(
    string CertificateSource,
    string AppInstallerSource,
    bool PlanOnly,
    bool Quiet,
    bool NoLaunch,
    bool RepairFleetReplayProtection,
    bool DestructiveResetFleetReplayProtection,
    bool Json)
{
    private static string DefaultCertificateSource => DistributionIdentity.IsLabs
        ? ExactLabsAssetUri($"{DistributionIdentity.AssetStem}.cer")
        : "https://github.com/MesmerPrism/QuestIonAble-File-Manager/releases/latest/download/QuestIonAbleFileManager.cer";

    private static string DefaultAppInstallerSource => DistributionIdentity.IsLabs
        ? ExactLabsAssetUri($"{DistributionIdentity.AssetStem}.appinstaller")
        : "https://github.com/MesmerPrism/QuestIonAble-File-Manager/releases/latest/download/QuestIonAbleFileManager.appinstaller";

    private static string ExactLabsAssetUri(string asset) =>
        $"https://github.com/MesmerPrism/QuestIonAble-File-Manager/releases/download/{DistributionIdentity.ReleaseTag}/{asset}";

    public static InstallerOptions Parse(string[] args)
    {
        var certificateSource = DefaultCertificateSource;
        var appInstallerSource = DefaultAppInstallerSource;
        var planOnly = false;
        var quiet = false;
        var noLaunch = false;
        var repairFleetReplayProtection = false;
        var destructiveResetFleetReplayProtection = false;
        var json = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--certificate-source":
                    certificateSource = ReadValue(args, ref index);
                    break;
                case "--appinstaller-source":
                    appInstallerSource = ReadValue(args, ref index);
                    break;
                case "--plan":
                    planOnly = true;
                    break;
                case "--quiet":
                    quiet = true;
                    break;
                case "--no-launch":
                    noLaunch = true;
                    break;
                case "--repair-fleet-replay-protection":
                    repairFleetReplayProtection = true;
                    break;
                case "--destructive-reset-fleet-replay-protection":
                    destructiveResetFleetReplayProtection = true;
                    break;
                case "--json":
                    json = true;
                    break;
                case "--help":
                case "-h":
                    throw new InstallerHelpException();
                default:
                    throw new ArgumentException($"Unknown setup option: {args[index]}");
            }
        }
        if (repairFleetReplayProtection &&
            destructiveResetFleetReplayProtection)
        {
            throw new ArgumentException(
                "Fleet replay repair and destructive reset are mutually exclusive.");
        }
        DistributionIdentity.RejectLabsFleetReplayOperation(
            repairFleetReplayProtection,
            destructiveResetFleetReplayProtection);
        if (DistributionIdentity.IsLabs)
        {
            ValidateLabsSource(
                certificateSource,
                DefaultCertificateSource,
                planOnly);
            ValidateLabsSource(
                appInstallerSource,
                DefaultAppInstallerSource,
                planOnly);
        }

        return new InstallerOptions(
            certificateSource,
            appInstallerSource,
            planOnly,
            quiet,
            noLaunch,
            repairFleetReplayProtection,
            destructiveResetFleetReplayProtection,
            json);
    }

    private static void ValidateLabsSource(
        string source,
        string exactSource,
        bool planOnly)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps)
        {
            if (!string.Equals(source, exactSource, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Labs Setup accepts only its embedded exact-tag HTTPS asset URL.");
            }
            return;
        }
        if (!planOnly)
        {
            throw new ArgumentException(
                "Local Labs assets are accepted only by the non-mutating plan route.");
        }
    }

    public static string HelpText => """
        QuestIonAble File Manager Setup

        Usage:
          QuestIonAbleFileManager-Setup.exe [options]

        Options:
          --certificate-source <uri-or-path>  Override the release certificate source.
          --appinstaller-source <uri-or-path> Override the App Installer feed source.
          --plan                              Stage and validate assets without trusting or installing.
          --quiet                             Run without the guided window.
          --no-launch                         Do not launch the app after installation.
          --repair-fleet-replay-protection    Repair local replay files only from protected machine authority.
          --destructive-reset-fleet-replay-protection
                                              Explicitly discard replay history and create an empty authority.
          --json                              Emit a machine-readable result in quiet mode.
          --help                              Show this help.
        """;

    private static string ReadValue(string[] args, ref int index)
    {
        if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
        {
            throw new ArgumentException("An option value is missing.");
        }

        return args[index];
    }
}

internal sealed class InstallerHelpException : Exception;

internal sealed record InstallerProgress(string Status, string Detail, int Percent);

internal sealed record InstallerResult(
    string Status,
    string PackageName,
    string PackageVersion,
    string Publisher,
    string AppInstallerSourceKind,
    string AppInstallerSha256,
    string CertificateThumbprint,
    bool CertificateTrusted,
    bool Installed,
    bool Launched,
    string FleetReplayProtectionAction);

internal sealed record InstallerFailureResult(
    string Status,
    string ErrorCode,
    string HResult,
    string? InnerHResult);

internal sealed record AppInstallerIdentity(string Name, string Publisher, string Version);

internal sealed class SetupStagingDirectory : IDisposable
{
    private readonly string _expectedParent;

    private SetupStagingDirectory(
        string path,
        string expectedParent)
    {
        Path = path;
        _expectedParent = expectedParent;
    }

    public string Path { get; }

    public static SetupStagingDirectory Create(
        string purpose,
        bool protectedMachineStaging)
    {
        if (purpose.Length is < 1 or > 64 ||
            purpose.Any(static character =>
                !char.IsAsciiLetterOrDigit(character)))
        {
            throw new InvalidOperationException(
                "The Setup staging purpose is invalid.");
        }
        if (!protectedMachineStaging)
        {
            var parent = System.IO.Path.GetFullPath(
                System.IO.Path.GetTempPath());
            var unprotectedPath = System.IO.Path.Combine(
                parent,
                purpose + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(unprotectedPath);
            RejectReparse(unprotectedPath);
            return new SetupStagingDirectory(unprotectedPath, parent);
        }

        var root = System.IO.Path.GetFullPath(
            Environment.GetFolderPath(
                DistributionIdentity.IsLabs
                    ? Environment.SpecialFolder.LocalApplicationData
                    : Environment.SpecialFolder.ProgramFiles));
        var vendorDirectory = System.IO.Path.Combine(
            root,
            "MesmerPrism");
        var productDirectory = System.IO.Path.Combine(
            vendorDirectory,
            DistributionIdentity.IsLabs
                ? "QuestIonAbleFileManagerLabs"
                : "QuestIonAbleFileManager");
        var parentDirectory = System.IO.Path.Combine(
            productDirectory,
            "SetupStaging");
        Directory.CreateDirectory(parentDirectory);
        ValidatePathChain(
            root,
            vendorDirectory,
            productDirectory,
            parentDirectory);
        var security = CreateProtectedSecurity();
        new DirectoryInfo(parentDirectory).SetAccessControl(security);
        ValidateProtectedSecurity(
            new DirectoryInfo(parentDirectory).GetAccessControl(
                AccessControlSections.Access | AccessControlSections.Owner));
        var path = System.IO.Path.Combine(
            parentDirectory,
            purpose + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        new DirectoryInfo(path).SetAccessControl(security);
        RejectReparse(path);
        ValidateProtectedSecurity(
            new DirectoryInfo(path).GetAccessControl(
                AccessControlSections.Access | AccessControlSections.Owner));
        return new SetupStagingDirectory(path, parentDirectory);
    }

    public void Dispose()
    {
        var fullPath = System.IO.Path.GetFullPath(Path);
        var parent = System.IO.Path.TrimEndingDirectorySeparator(
            System.IO.Path.GetFullPath(_expectedParent)) +
            System.IO.Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(
                parent,
                StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(fullPath))
        {
            return;
        }
        RejectReparse(fullPath);
        Directory.Delete(fullPath, recursive: true);
    }

    internal static DirectorySecurity CreateProtectedSecurity()
    {
        var administrators = new SecurityIdentifier(
            WellKnownSidType.BuiltinAdministratorsSid,
            null);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(
            isProtected: true,
            preserveInheritance: false);
        security.SetOwner(administrators);
        foreach (var identity in new[]
                 {
                     new SecurityIdentifier(
                         WellKnownSidType.LocalSystemSid,
                         null),
                     administrators
                 })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                identity,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit |
                InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }
        return security;
    }

    internal static void ValidateProtectedSecurity(
        DirectorySecurity security)
    {
        var administrators = new SecurityIdentifier(
            WellKnownSidType.BuiltinAdministratorsSid,
            null);
        var system = new SecurityIdentifier(
            WellKnownSidType.LocalSystemSid,
            null);
        if (!security.AreAccessRulesProtected ||
            security.GetOwner(typeof(SecurityIdentifier))
                is not SecurityIdentifier owner ||
            owner != administrators)
        {
            throw new InvalidOperationException(
                "The protected Setup staging ACL is invalid.");
        }
        var expected = new HashSet<string>(
            [system.Value, administrators.Value],
            StringComparer.Ordinal);
        var rules = security.GetAccessRules(
                includeExplicit: true,
                includeInherited: false,
                typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        if (rules.Length != expected.Count ||
            rules.Any(rule =>
                rule.AccessControlType != AccessControlType.Allow ||
                rule.FileSystemRights != FileSystemRights.FullControl ||
                rule.InheritanceFlags !=
                    (InheritanceFlags.ContainerInherit |
                     InheritanceFlags.ObjectInherit) ||
                rule.IdentityReference is not SecurityIdentifier sid ||
                !expected.Contains(sid.Value)))
        {
            throw new InvalidOperationException(
                "The protected Setup staging ACL is invalid.");
        }
    }

    private static void ValidatePathChain(
        string root,
        params string[] paths)
    {
        RejectReparse(root);
        foreach (var path in paths)
        {
            var fullPath = System.IO.Path.GetFullPath(path);
            if (!fullPath.StartsWith(
                    System.IO.Path.TrimEndingDirectorySeparator(root) +
                    System.IO.Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The protected Setup staging path escaped its distribution root.");
            }
            RejectReparse(fullPath);
        }
    }

    private static void RejectReparse(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                "The Setup staging path must not be a reparse point.");
        }
    }
}

internal sealed class GuidedInstaller
{
    // Stable signed identity retained from releases published before the rename.
    internal static string ExpectedPackageName => DistributionIdentity.PackageIdentity;
    internal const string ExpectedPublisher = "CN=MesmerPrism";
    private static string DownloadDirectoryName => DistributionIdentity.IsLabs
        ? "QuestIonAbleFileManagerLabsSetup"
        : "QuestIonAbleFileManagerSetup";

    public async Task<InstallerResult> RunAsync(
        InstallerOptions options,
        IProgress<InstallerProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var staging = SetupStagingDirectory.Create(
            DownloadDirectoryName,
            protectedMachineStaging: IsAdministrator());
        var stagingDirectory = staging.Path;
        var certificatePath = Path.Combine(stagingDirectory, $"{DistributionIdentity.AssetStem}.cer");
        var appInstallerPath = Path.Combine(stagingDirectory, $"{DistributionIdentity.AssetStem}.appinstaller");

        progress?.Report(new InstallerProgress("Preparing setup", "Creating the local staging area.", 5));
        using var httpClient = new HttpClient();
        await StageSourceAsync(httpClient, options.CertificateSource, certificatePath, cancellationToken);
        progress?.Report(new InstallerProgress("Certificate ready", "The public signing certificate was staged.", 25));
        await StageSourceAsync(httpClient, options.AppInstallerSource, appInstallerPath, cancellationToken);
        progress?.Report(new InstallerProgress("Update feed ready", "The App Installer feed was staged.", 45));

        using var certificate = X509CertificateLoader.LoadCertificateFromFile(certificatePath);
        var identity = ParseAndValidateAppInstaller(appInstallerPath);

        if (options.PlanOnly)
        {
            progress?.Report(new InstallerProgress("Plan validated", "No trust or package state was changed.", 100));
            return new InstallerResult(
                "planned",
                identity.Name,
                identity.Version,
                identity.Publisher,
                SourceKind(options.AppInstallerSource),
                FileSha256(appInstallerPath),
                certificate.Thumbprint,
                CertificateTrusted: IsCertificateTrusted(certificate),
                Installed: false,
                Launched: false,
                FleetReplayProtectionAction: "not_run_plan");
        }

        progress?.Report(new InstallerProgress(
            "Trusting certificate",
            "Adding the public certificate to your Windows Trusted People store.",
            55));
        var addedCertificate = TrustCertificateForCurrentUser(certificate);

        progress?.Report(new InstallerProgress(
            "Installing application",
            $"Windows is installing or updating QuestIonAble File Manager {identity.Version}.",
            65));
        await InstallFromAppInstallerAsync(appInstallerPath, progress, cancellationToken);

        var package = new PackageManager()
            .FindPackages()
            .Where(candidate =>
                string.Equals(candidate.Id.Name, identity.Name, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.Id.Publisher, identity.Publisher, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(candidate => candidate.Id.Version)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("Windows completed setup but the installed package registration could not be found.");

        var fleetReplayProtection = DistributionIdentity.FleetReplayProtectionEnabled
            ? FleetInstallerReplayProtectionSetup
                .ProvisionOrRepairEmbeddedRelease(
                    options.RepairFleetReplayProtection,
                    options.DestructiveResetFleetReplayProtection)
            : new FleetReplayProtectionSetupResult(
                "labs_disabled",
                StateRootSha256: null);

        var launched = false;
        if (!options.NoLaunch)
        {
            progress?.Report(new InstallerProgress("Launching application", "Opening the installed app.", 98));
            launched = TryLaunch(package.Id.FamilyName);
        }

        progress?.Report(new InstallerProgress("Setup complete", "QuestIonAble File Manager is ready.", 100));
        return new InstallerResult(
            "installed",
            identity.Name,
            identity.Version,
            identity.Publisher,
            SourceKind(options.AppInstallerSource),
            FileSha256(appInstallerPath),
            certificate.Thumbprint,
            CertificateTrusted: addedCertificate || IsCertificateTrusted(certificate),
            Installed: true,
            Launched: launched,
            FleetReplayProtectionAction: fleetReplayProtection.Action);
    }

    internal static AppInstallerIdentity ParseAndValidateAppInstaller(string path)
    {
        var document = XDocument.Load(path, LoadOptions.PreserveWhitespace);
        var root = document.Root ?? throw new InvalidOperationException("The App Installer feed is empty.");
        var mainPackage = root.Element(root.Name.Namespace + "MainPackage")
            ?? throw new InvalidOperationException("The App Installer feed has no MainPackage entry.");
        var identity = new AppInstallerIdentity(
            mainPackage.Attribute("Name")?.Value.Trim() ?? string.Empty,
            mainPackage.Attribute("Publisher")?.Value.Trim() ?? string.Empty,
            mainPackage.Attribute("Version")?.Value.Trim() ?? string.Empty);

        if (!string.Equals(identity.Name, ExpectedPackageName, StringComparison.Ordinal) ||
            !string.Equals(identity.Publisher, ExpectedPublisher, StringComparison.Ordinal) ||
            !Version.TryParse(identity.Version, out _))
        {
            throw new InvalidOperationException(
                $"The feed identity is not the expected public package ({ExpectedPackageName}, {ExpectedPublisher}).");
        }
        if (DistributionIdentity.IsLabs)
        {
            var exactPrefix =
                $"https://github.com/MesmerPrism/QuestIonAble-File-Manager/releases/download/{DistributionIdentity.ReleaseTag}/";
            var feedUri = root.Attribute("Uri")?.Value.Trim() ?? string.Empty;
            var packageUri = mainPackage.Attribute("Uri")?.Value.Trim() ?? string.Empty;
            if (!feedUri.StartsWith(exactPrefix, StringComparison.Ordinal) ||
                !packageUri.StartsWith(exactPrefix, StringComparison.Ordinal) ||
                feedUri.Contains("/latest/", StringComparison.Ordinal) ||
                packageUri.Contains("/latest/", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The Labs feed is not bound to its embedded exact release tag.");
            }
        }

        return identity;
    }

    private static string SourceKind(string source) =>
        Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps
            ? "https"
            : "local_file";

    private static string FileSha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
            .ToLowerInvariant();

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(
            WindowsBuiltInRole.Administrator);
    }

    private static async Task StageSourceAsync(
        HttpClient httpClient,
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var output = File.Create(destination);
            await response.Content.CopyToAsync(output, cancellationToken);
            return;
        }

        var localPath = uri is { IsFile: true } ? uri.LocalPath : source;
        File.Copy(Path.GetFullPath(localPath), destination, overwrite: true);
    }

    private static bool TrustCertificateForCurrentUser(X509Certificate2 certificate)
    {
        using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        var exists = store.Certificates
            .Find(X509FindType.FindByThumbprint, certificate.Thumbprint, validOnly: false)
            .Count > 0;
        if (!exists)
        {
            store.Add(certificate);
        }

        return !exists;
    }

    private static bool IsCertificateTrusted(X509Certificate2 certificate)
    {
        foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            using var store = new X509Store(StoreName.TrustedPeople, location);
            store.Open(OpenFlags.ReadOnly);
            if (store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, validOnly: false).Count > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static async Task InstallFromAppInstallerAsync(
        string appInstallerPath,
        IProgress<InstallerProgress>? progress,
        CancellationToken cancellationToken)
    {
        var packageManager = new PackageManager();
        var operation = packageManager.AddPackageByAppInstallerFileAsync(
            new Uri(Path.GetFullPath(appInstallerPath)),
            AddPackageByAppInstallerOptions.ForceTargetAppShutdown,
            packageManager.GetDefaultPackageVolume());

        operation.Progress = new AsyncOperationProgressHandler<DeploymentResult, DeploymentProgress>((_, update) =>
        {
            var percent = 65 + (int)Math.Round(update.percentage * 0.32);
            progress?.Report(new InstallerProgress("Installing application", $"Windows package state: {update.state}.", Math.Clamp(percent, 65, 97)));
        });

        using var registration = cancellationToken.Register(operation.Cancel);
        var result = await operation;
        if (!string.IsNullOrWhiteSpace(result.ErrorText))
        {
            var exception = new InvalidOperationException(result.ErrorText, result.ExtendedErrorCode);
            if (result.ExtendedErrorCode is not null)
            {
                exception.HResult = result.ExtendedErrorCode.HResult;
            }

            throw exception;
        }
    }

    private static bool TryLaunch(string? packageFamilyName)
    {
        if (string.IsNullOrWhiteSpace(packageFamilyName))
        {
            return false;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $@"shell:AppsFolder\{packageFamilyName}!App",
            UseShellExecute = true
        });
        return true;
    }
}

internal sealed class InstallerForm : Form
{
    private readonly InstallerOptions _options;
    private readonly Label _status = new() { AutoSize = false, Font = new Font("Segoe UI", 17, FontStyle.Bold), Height = 38, Dock = DockStyle.Top };
    private readonly Label _detail = new() { AutoSize = false, Font = new Font("Segoe UI", 10), Height = 74, Dock = DockStyle.Top };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, Height = 22, Dock = DockStyle.Top };
    private readonly Button _close = new() { Text = "Close", Width = 110, Height = 34, Enabled = false, Dock = DockStyle.Bottom };

    public InstallerForm(InstallerOptions options)
    {
        _options = options;
        Text = "QuestIonAble File Manager Setup";
        Width = 580;
        Height = 300;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        Padding = new Padding(26);
        Controls.Add(_close);
        Controls.Add(_progress);
        Controls.Add(_detail);
        Controls.Add(_status);
        _close.Click += (_, _) => Close();
        Shown += async (_, _) => await RunAsync();
    }

    public int ExitCode { get; private set; }

    private async Task RunAsync()
    {
        try
        {
            var progress = new Progress<InstallerProgress>(update =>
            {
                _status.Text = update.Status;
                _detail.Text = update.Detail;
                _progress.Value = Math.Clamp(update.Percent, 0, 100);
            });
            await new GuidedInstaller().RunAsync(_options, progress, CancellationToken.None);
            _close.Enabled = true;
        }
        catch (Exception exception)
        {
            ExitCode = 1;
            _status.Text = "Setup could not finish";
            _detail.Text = exception.Message;
            _close.Enabled = true;
        }
    }
}

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            DistributionIdentity.RejectLabsFleetReplayArguments(args);
            if (args is ["--fleet-release-configuration-proof"])
            {
                return FleetInstallerReleaseProof.Write();
            }
            if (args is ["--fleet-replay-security-self-test"])
            {
                return FleetInstallerReplayProtectionSetup
                    .WriteSecuritySelfTest();
            }
            if (args is
                [
                    "--fleet-replay-lock-test-child",
                    var token,
                    var testDescriptorId,
                    var testVersion,
                    var holdMilliseconds,
                    var mode,
                    var ready
                ])
            {
                return FleetInstallerReplayProtectionSetup
                    .RunLockTestChild(
                        token,
                        testDescriptorId,
                        testVersion,
                        holdMilliseconds,
                        mode,
                        ready);
            }
            if (args is
                [
                    "--fleet-replay-accept",
                    var stateRootSha256,
                    var descriptorId,
                    var version,
                    var payloadSha256
                ])
            {
                if (!IsAdministrator())
                {
                    return RelaunchElevated(args);
                }
                return FleetInstallerReplayProtectionSetup
                    .AcceptEmbeddedRelease(
                        stateRootSha256,
                        descriptorId,
                        version,
                        payloadSha256);
            }
            var options = InstallerOptions.Parse(args);
            if (!options.PlanOnly && !IsAdministrator())
            {
                return RelaunchElevated(args);
            }

            if (options.Quiet || options.PlanOnly)
            {
                return RunHeadlessAsync(options).GetAwaiter().GetResult();
            }

            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using var form = new InstallerForm(options);
            Application.Run(form);
            return form.ExitCode;
        }
        catch (InstallerHelpException)
        {
            Console.WriteLine(InstallerOptions.HelpText);
            return 0;
        }
        catch (Exception exception)
        {
            _ = exception;
            Console.Error.WriteLine("Setup failed.");
            return 2;
        }
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static int RelaunchElevated(string[] args)
    {
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The setup executable path could not be resolved for elevation.");
        var startInfo = new ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = true,
            Verb = "runas"
        };
        foreach (var argument in args)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows did not start the elevated setup process.");
        process.WaitForExit();
        return process.ExitCode;
    }

    private static async Task<int> RunHeadlessAsync(InstallerOptions options)
    {
        try
        {
            var result = await new GuidedInstaller().RunAsync(options, progress: null, CancellationToken.None);
            Console.WriteLine(options.Json
                ? JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })
                : $"{result.Status}: {result.PackageName} {result.PackageVersion}");
            return 0;
        }
        catch (Exception exception)
        {
            if (options.Json)
            {
                Console.Error.WriteLine(JsonSerializer.Serialize(
                    new InstallerFailureResult(
                        "failed",
                        "setup_failed",
                        $"0x{exception.HResult:X8}",
                        exception.InnerException is null
                            ? null
                            : $"0x{exception.InnerException.HResult:X8}")));
            }
            else
            {
                Console.Error.WriteLine("Setup failed.");
            }

            return 1;
        }
    }
}
