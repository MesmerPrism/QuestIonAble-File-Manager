using System.Net;
using System.Text.RegularExpressions;
using QuestIonAbleFileManager.Core;

namespace QuestIonAbleFileManager.BrowserHost;

public sealed class BrowserRequestException(string code, int statusCode = 400) : Exception(code)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
    // A consumed ID refers to an earlier admission; its effect remains possible.
    public bool? Dispatched => Code == "request_already_consumed" ? null : false;
}

public sealed class BrowserSecurity
{
    public const string CredentialVariable = "QUESTIONABLE_FILE_MANAGER_BROWSER_BEARER";
    private readonly string _credential;
    public Uri ListenUri { get; }
    public string Origin => ListenUri.GetLeftPart(UriPartial.Authority);
    private static readonly HashSet<string> StaticPaths = new(StringComparer.Ordinal)
    {
        "/", "/index.html", "/agent.html", "/app.js", "/pwa.js", "/retained-view.js", "/sw.js", "/manifest.webmanifest",
        "/icons/qfm-192.png", "/icons/qfm-512.png", "/styles.css", "/usb-proof.js", "/usb-proof-core.js",
        "/vendor/tango-adb.js", "/vendor/TANGO_NOTICES.txt", "/vendor/tango-packages.json",
        "/transfer-view.js", "/verified-download.js", "/apk-read-view.js", "/file-push-view.js", "/kiosk-view.js", "/direct-connection-view.js", "/profile-view.js", "/vendor/noble-sha256.js",
        "/vendor/NOBLE_HASHES_NOTICES.txt", "/vendor/noble-hashes-packages.json",
        "/THIRD_PARTY_NOTICES.md", "/vendor/THIRD_PARTY_NOTICES.md",
        "/vendor/LICENSE", "/vendor/LICENSE.txt"
    };
    public static bool IsStaticPath(string path) => StaticPaths.Contains(path);

    public BrowserSecurity(string listen, string credential)
    {
        if (!Regex.IsMatch(listen, @"^http://(?:[0-9.]+|\[[0-9a-fA-F:]+\]):[0-9]{1,5}/$", RegexOptions.CultureInvariant) ||
            !Uri.TryCreate(listen, UriKind.Absolute, out var uri) ||
            uri.Port is < 1 or > 65535 ||
            !IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) || !IPAddress.IsLoopback(address))
            throw new BrowserRequestException("listen_invalid");
        LocalApiSecurity.ValidateCredential(credential);
        ListenUri = uri;
        _credential = credential;
    }

    public static bool IsStaticNavigation(string method, string path, string? mode, string? destination) =>
        method == "GET" && IsStaticPath(path) && mode == "navigate" && destination == "document";

    public bool AllowsRequest(string host, string? origin, string? fetchSite, IPAddress? remote,
        bool staticNavigation = false) =>
        remote is not null && IPAddress.IsLoopback(remote) &&
        string.Equals(host, ListenUri.Authority, StringComparison.OrdinalIgnoreCase) &&
        (staticNavigation ||
            ((string.IsNullOrEmpty(origin) || string.Equals(origin, Origin, StringComparison.Ordinal)) &&
             (string.IsNullOrEmpty(fetchSite) || fetchSite is "same-origin" or "none")));

    public bool Authenticate(string header) => LocalApiSecurity.AuthenticateBearer(_credential, header);
}
