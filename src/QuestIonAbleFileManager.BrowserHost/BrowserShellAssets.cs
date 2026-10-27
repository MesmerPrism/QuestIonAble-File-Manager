using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QuestIonAbleFileManager.BrowserHost;

/// <summary>Version binding for the fixed public UI only. Never accepts a caller-selected path.</summary>
public static class BrowserShellAssets
{
    private const string Marker = "/*QFM_SHELL_MANIFEST*/ null";
    public static IReadOnlyList<string> Paths { get; } = Array.AsReadOnly(new[]
    {
        "/index.html", "/agent.html", "/app.js", "/pwa.js", "/retained-view.js", "/styles.css",
        "/usb-proof.js", "/usb-proof-core.js", "/vendor/tango-adb.js",
        "/transfer-view.js", "/verified-download.js", "/apk-read-view.js", "/file-push-view.js", "/kiosk-view.js", "/direct-connection-view.js", "/direct-command-view.js", "/profile-view.js", "/fleet-status-view.js", "/diagnostics-view.js", "/direct-tags-view.js", "/vendor/noble-sha256.js", "/vendor/NOBLE_HASHES_NOTICES.txt",
        "/manifest.webmanifest", "/icons/qfm-192.png", "/icons/qfm-512.png"
    });

    public static string RenderWorker(string webRoot)
    {
        var template = File.ReadAllText(Path.Combine(webRoot, "sw.js"));
        if (template.Split(Marker, StringSplitOptions.None).Length != 2)
            throw new InvalidDataException("Worker requires exactly one shell manifest marker.");
        var assets = new Dictionary<string, string>(StringComparer.Ordinal);
        var identity = new StringBuilder(template).Append('\n');
        foreach (var path in Paths)
        {
            var bytes = File.ReadAllBytes(Path.Combine(webRoot, path[1..].Replace('/', Path.DirectorySeparatorChar)));
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            assets.Add(path, hash);
            identity.Append(path).Append('\t').Append(hash).Append('\n');
        }
        var revision = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString())));
        return template.Replace(Marker, JsonSerializer.Serialize(new { revision, assets }), StringComparison.Ordinal);
    }
}
