using System.Text.RegularExpressions;
using System.Net;
using System.Net.Sockets;

namespace QuestIonAbleFileManager.Core;

public static partial class AndroidInput
{
    public static string RequireSerial(string serial)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        if (!SerialPattern().IsMatch(serial))
        {
            throw new ArgumentException("The ADB serial contains unsupported characters.", nameof(serial));
        }

        return serial;
    }

    public static string RequireUsbSerial(string serial)
    {
        serial = RequireSerial(serial);
        if (TryParseWifiEndpoint(serial, out _, out _))
        {
            throw new ArgumentException(
                "A directly connected USB headset is required to enable Wi-Fi ADB.",
                nameof(serial));
        }

        return serial;
    }

    public static string RequireWifiSerial(string serial)
    {
        serial = RequireSerial(serial);
        if (!TryParseWifiEndpoint(serial, out _, out _))
        {
            throw new ArgumentException(
                "A Wi-Fi ADB target must use the host:port serial shown by adb devices.",
                nameof(serial));
        }

        return serial;
    }

    public static string RequireWifiHost(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        host = host.Trim();
        if (IPAddress.TryParse(host, out var address))
        {
            if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address))
            {
                throw new ArgumentException(
                    "Use the headset's non-loopback IPv4 address.",
                    nameof(host));
            }

            return address.ToString();
        }

        if (!WifiHostPattern().IsMatch(host))
        {
            throw new ArgumentException(
                "The Wi-Fi ADB host must be an IPv4 address or DNS hostname.",
                nameof(host));
        }

        return host;
    }

    public static int RequireTcpPort(int port)
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "The TCP port must be between 1 and 65535.");
        }

        return port;
    }

    public static int RequireParallelism(int parallelism)
    {
        if (parallelism is < 1 or > 16)
        {
            throw new ArgumentOutOfRangeException(
                nameof(parallelism),
                "Parallel installation must use between 1 and 16 workers.");
        }

        return parallelism;
    }

    public static string CreateWifiEndpoint(string host, int port) =>
        $"{RequireWifiHost(host)}:{RequireTcpPort(port)}";

    public static bool TryParseWifiEndpoint(string value, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var separator = value.LastIndexOf(':');
        if (separator <= 0 || separator == value.Length - 1 ||
            !int.TryParse(value[(separator + 1)..], out port))
        {
            return false;
        }

        try
        {
            host = RequireWifiHost(value[..separator]);
            RequireTcpPort(port);
            return true;
        }
        catch (ArgumentException)
        {
            host = string.Empty;
            port = 0;
            return false;
        }
    }

    public static string RequirePackageName(string packageName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);
        if (!PackagePattern().IsMatch(packageName))
        {
            throw new ArgumentException("The Android package name is not valid.", nameof(packageName));
        }

        return packageName;
    }

    public static string RequireRemotePath(string remotePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        if (!remotePath.StartsWith("/", StringComparison.Ordinal))
        {
            throw new ArgumentException("An absolute Android path is required.", nameof(remotePath));
        }

        if (remotePath.IndexOfAny(['\0', '\r', '\n']) >= 0)
        {
            throw new ArgumentException("The Android path contains unsupported control characters.", nameof(remotePath));
        }

        return remotePath.Length > 1 ? remotePath.TrimEnd('/') : remotePath;
    }

    public static string RequireInstalledApkPath(string remotePath)
    {
        remotePath = RequireRemotePath(remotePath);
        var segments = remotePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (!InstalledApkPathPattern().IsMatch(remotePath) ||
            segments.Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException(
                "The installed APK path is not a constrained package-manager /data/app path.",
                nameof(remotePath));
        }

        return remotePath;
    }

    public static string ShellQuote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.IndexOfAny(['\0', '\r', '\n']) >= 0)
        {
            throw new ArgumentException("Shell values cannot contain line breaks or NUL characters.", nameof(value));
        }

        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    public static string CombineRemotePath(string root, string name)
    {
        root = RequireRemotePath(root);
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Contains('/'))
        {
            throw new ArgumentException("The remote entry name is not valid.", nameof(name));
        }

        return root == "/" ? "/" + name : root + "/" + name;
    }

    [GeneratedRegex("^[A-Za-z0-9._:-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SerialPattern();

    [GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9.-]{0,251}[A-Za-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex WifiHostPattern();

    [GeneratedRegex("^[A-Za-z0-9_]+(?:\\.[A-Za-z0-9_]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex PackagePattern();

    [GeneratedRegex("^/data/app/(?:[A-Za-z0-9._~+=-]+/)+[A-Za-z0-9._~+=-]+\\.apk$", RegexOptions.CultureInvariant)]
    private static partial Regex InstalledApkPathPattern();
}
