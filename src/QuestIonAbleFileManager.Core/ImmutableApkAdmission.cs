namespace QuestIonAbleFileManager.Core;

internal sealed class ImmutableApkAdmission : IDisposable
{
    private static readonly SemaphoreSlim ProcessGate = new(1, 1);
    private static readonly TimeSpan OwnerWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OwnerRetry = TimeSpan.FromMilliseconds(50);

    private readonly LocalApiArtifactStager _stager;
    private readonly LocalApiStateSettings _settings;
    private IReadOnlyList<LocalApiStagedArtifact> _artifacts;
    private bool _disposed;

    private ImmutableApkAdmission(
        LocalApiArtifactStager stager,
        IReadOnlyList<LocalApiStagedArtifact> artifacts,
        LocalApiStateSettings settings)
    {
        _stager = stager;
        _artifacts = artifacts;
        _settings = settings;
    }

    public string Path => Paths.Single();

    internal IReadOnlyList<string> Paths => !_disposed
        ? _artifacts.Select(static artifact => artifact.Path).ToArray()
        : throw new ObjectDisposedException(nameof(ImmutableApkAdmission));

    public static async Task<ImmutableApkAdmission> CreateAsync(
        string sourcePath,
        CancellationToken cancellationToken) =>
        await CreateManyAsync([sourcePath], cancellationToken).ConfigureAwait(false);

    internal static async Task<ImmutableApkAdmission> CreateManyAsync(
        IReadOnlyList<string> sourcePaths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        if (sourcePaths.Count is < 1 or > 16 ||
            sourcePaths.Any(static path => string.IsNullOrWhiteSpace(path)))
        {
            throw new ArgumentException(
                "Immutable APK admission requires 1..16 non-empty source paths.",
                nameof(sourcePaths));
        }
        var sources = sourcePaths
            .Select(static path => System.IO.Path.GetFullPath(path))
            .ToArray();
        await ProcessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        LocalApiArtifactStager? stager = null;
        var artifacts = new List<LocalApiStagedArtifact>(sources.Length);
        try
        {
            var settings = LocalApiStateSettings.CreateForTests(
                System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "QuestIonAbleFileManager.ApkAdmission"),
                new LocalApiStateLimits(
                    MaximumRetainedOperations: 1,
                    MaximumRunningOperations: 1,
                    MaximumStagedBytes: LocalApiStateLimits.DefaultMaximumStagedBytes,
                    MaximumStagedFiles: LocalApiStateLimits.DefaultMaximumStagedFiles,
                    MaximumSingleArtifactBytes: LocalApiStateLimits.DefaultMaximumSingleArtifactBytes));
            var deadline = DateTimeOffset.UtcNow + OwnerWait;
            while (stager is null)
            {
                try
                {
                    stager = new LocalApiArtifactStager(settings);
                }
                catch (LocalApiException exception) when (
                    exception.Code == "state_in_use" &&
                    DateTimeOffset.UtcNow < deadline)
                {
                    await Task.Delay(OwnerRetry, cancellationToken).ConfigureAwait(false);
                }
            }
            stager.CleanupTransientArtifacts();
            foreach (var sourcePath in sources)
            {
                artifacts.Add(await stager.StageAsync(
                    sourcePath,
                    cancellationToken).ConfigureAwait(false));
            }
            // Retain immutable file and ancestor handles for the command, while
            // releasing inventory ownership after the local copy phase.
            stager.ReleaseTransientOwnerLease();
            return new ImmutableApkAdmission(stager, artifacts, settings);
        }
        catch
        {
            foreach (var artifact in artifacts)
            {
                artifact.TryDelete(out _);
            }
            stager?.Dispose();
            throw;
        }
        finally
        {
            ProcessGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            // Serialize deletion with admission inventories, including other
            // processes. The original stager still pins the ancestor identities.
            using var cleanup = new LocalApiArtifactStager(_settings);
            foreach (var artifact in _artifacts) artifact.TryDelete(out _);
        }
        catch (Exception exception) when (exception is LocalApiException or
            System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            // Cleanup contention cannot replace a command's authoritative result.
            // Closing our handles makes this exact file reclaimable next admission.
        }
        finally
        {
            // A failed cleanup becomes deletable transient debt, not a held
            // artifact or an attempt to delete another admission's files.
            foreach (var artifact in _artifacts) artifact.Dispose();
            _artifacts = [];
            _stager.Dispose();
        }
    }
}
