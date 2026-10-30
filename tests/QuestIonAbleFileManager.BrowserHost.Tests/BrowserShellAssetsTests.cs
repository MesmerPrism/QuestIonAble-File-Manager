using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuestIonAbleFileManager.BrowserHost;

namespace QuestIonAbleFileManager.BrowserHost.Tests;

public sealed class BrowserShellAssetsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qfm-shell-test-" + Guid.NewGuid().ToString("N"));
    private const string Template = "const shell = /*QFM_SHELL_MANIFEST*/ null;\n// fixed worker";

    public BrowserShellAssetsTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "sw.js"), Template);
        foreach (var path in BrowserShellAssets.Paths)
        {
            var file = Path.Combine(_root, path[1..]);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "public fixture " + path);
        }
    }

    [Fact]
    public void WorkerBindsOnlyTheFixedStaticPublicAssetsToTheirExactHashes()
    {
        var first = BrowserShellAssets.RenderWorker(_root);
        Assert.Equal(first, BrowserShellAssets.RenderWorker(_root));
        Assert.DoesNotContain("QFM_SHELL_MANIFEST", first);
        using var manifest = Manifest(first);
        var assets = manifest.RootElement.GetProperty("assets");
        Assert.Contains("/apk-set-view.js", BrowserShellAssets.Paths);
        Assert.True(BrowserSecurity.IsStaticPath("/apk-set-view.js"));
        Assert.Contains("/apk-install-view.js", BrowserShellAssets.Paths);
        Assert.True(BrowserSecurity.IsStaticPath("/apk-install-view.js"));
        Assert.Equal(BrowserShellAssets.Paths.Count, assets.EnumerateObject().Count());
        var identity = new StringBuilder(Template).Append('\n');
        foreach (var path in BrowserShellAssets.Paths)
        {
            Assert.True(BrowserSecurity.IsStaticPath(path));
            Assert.DoesNotContain("/browser/", path);
            var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(_root, path[1..]))));
            Assert.Equal(hash, assets.GetProperty(path).GetString());
            identity.Append(path).Append('\t').Append(hash).Append('\n');
        }
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString()))),
            manifest.RootElement.GetProperty("revision").GetString());
        File.WriteAllText(Path.Combine(_root, "private.apk"), "private data");
        Assert.Equal(first, BrowserShellAssets.RenderWorker(_root));
    }

    [Fact]
    public void AnyShellOrWorkerChangeUpdatesTheWorkerAndRevision()
    {
        var first = BrowserShellAssets.RenderWorker(_root);
        File.AppendAllText(Path.Combine(_root, "apk-set-view.js"), "\n// revision two");
        var second = BrowserShellAssets.RenderWorker(_root);
        Assert.NotEqual(first, second);
        Assert.NotEqual(Revision(first), Revision(second));
        File.AppendAllText(Path.Combine(_root, "apk-install-view.js"), "\n// single install revision");
        var third = BrowserShellAssets.RenderWorker(_root);
        Assert.NotEqual(Revision(second), Revision(third));
        File.AppendAllText(Path.Combine(_root, "sw.js"), "\n// worker correction");
        Assert.NotEqual(Revision(third), Revision(BrowserShellAssets.RenderWorker(_root)));
    }

    [Fact]
    public void MissingAssetOrUnrenderableTemplateCannotProduceAWorker()
    {
        File.Delete(Path.Combine(_root, "app.js"));
        Assert.Throws<FileNotFoundException>(() => BrowserShellAssets.RenderWorker(_root));
        File.WriteAllText(Path.Combine(_root, "sw.js"), "no manifest placeholder");
        Assert.Throws<InvalidDataException>(() => BrowserShellAssets.RenderWorker(_root));
        File.WriteAllText(Path.Combine(_root, "sw.js"), Template + Template);
        Assert.Throws<InvalidDataException>(() => BrowserShellAssets.RenderWorker(_root));
    }

    private static JsonDocument Manifest(string worker) => JsonDocument.Parse(worker["const shell = ".Length..worker.IndexOf(';')]);
    private static string? Revision(string worker)
    {
        using var manifest = Manifest(worker);
        return manifest.RootElement.GetProperty("revision").GetString();
    }
    public void Dispose() => Directory.Delete(_root, recursive: true);
}
