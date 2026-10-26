using QuestIonAbleFileManager.BrowserHost;
using Xunit;

namespace QuestIonAbleFileManager.BrowserHost.Tests;

public sealed class PwaSecurityTests
{
    [Theory]
    [InlineData("/manifest.webmanifest")]
    [InlineData("/sw.js")]
    [InlineData("/pwa.js")]
    [InlineData("/icons/qfm-192.png")]
    [InlineData("/icons/qfm-512.png")]
    public void ShippedPwaAssetsAreExplicitlyStatic(string path) => Assert.True(BrowserSecurity.IsStaticPath(path));

    [Theory]
    [InlineData("/browser/v1/session")]
    [InlineData("/browser/v1/execute")]
    [InlineData("/private.apk")]
    [InlineData("/icons/../private.apk")]
    [InlineData("/sw.js/other")]
    public void NoDynamicOrUnlistedAssetBecomesStatic(string path) => Assert.False(BrowserSecurity.IsStaticPath(path));
}
