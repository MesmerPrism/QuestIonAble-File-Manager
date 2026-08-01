namespace QuestIonAbleFileManager.Core;

internal sealed class ImmutableApkAdmission : IDisposable
{
    public const long MaximumInspectedApkBytes = 512L * 1024 * 1024;

    private static readonly SemaphoreSlim ProcessGate = new(1, 1);
    private static readonly TimeSpan OwnerWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OwnerRetry = TimeSpan.FromMilliseconds(50);

    private readonly LocalApiArtifactStager _stager;
    private LocalApiStagedArtifact? _artifact;
    private bool _disposed;

    private ImmutableApkAdmission(
        LocalApiArtifactStager stager,
        LocalApiStagedArtifact artifact)
    {
        _stager = stager;
        _artifact = artifact;
    }

    public string Path => _artifact?.Path
        ?? throw new ObjectDisposedException(nameof(ImmutableApkAdmission));

    public static async Task<ImmutableApkAdmission> CreateAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        await ProcessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        LocalApiArtifactStager? stager = null;
        try
        {
            var settings = LocalApiStateSettings.CreateForTests(
                System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "QuestIonAbleFileManager.ApkAdmission"),
                new LocalApiStateLimits(
                    MaximumRetainedOperations: 1,
                    MaximumRunningOperations: 1,
                    MaximumStagedBytes: MaximumInspectedApkBytes,
                    MaximumStagedFiles: 1));
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
            stager.CleanupOrphanedArtifacts();
            var artifact = await stager.StageAsync(
                sourcePath,
                cancellationToken).ConfigureAwait(false);
            return new ImmutableApkAdmission(stager, artifact);
        }
        catch
        {
            stager?.Dispose();
            ProcessGate.Release();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _artifact?.TryDelete(out _);
            _artifact = null;
            _stager.Dispose();
        }
        finally
        {
            ProcessGate.Release();
        }
    }
}
