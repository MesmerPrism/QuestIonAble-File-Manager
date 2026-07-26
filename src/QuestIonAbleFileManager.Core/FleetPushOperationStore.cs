using System.Security.Cryptography;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace QuestIonAbleFileManager.Core;

public sealed record FleetMutationAuthorityDecision(
    bool Accepted,
    string? Code,
    string? Reason,
    string? VerifiedAuthorityDigest,
    CancellationToken RevocationToken = default);

public interface IFleetMutationAuthorityVerifier
{
    ValueTask<FleetMutationAuthorityDecision> VerifyCurrentAsync(
        FleetIntegrationOperationRequest request,
        CancellationToken cancellationToken = default);
}

internal sealed record FleetPushJournalEntry(
    string Schema,
    int Sequence,
    FleetIntegrationOperationStatusSnapshot Status,
    string RequestDigest,
    string LocalArtifactPath,
    string RemotePartialName,
    string? VerifiedAuthorityDigest);

internal sealed record FleetPushReservation(
    string Schema,
    FleetIntegrationOperationRequest Request,
    FleetIntegrationOperationStatusSnapshot Status,
    string RequestDigest,
    string LocalArtifactPath,
    string RemotePartialName,
    string VerifiedAuthorityDigest);

internal sealed class FleetPushOperationStore
{
    private const string JournalSchema = "questionable.file_manager.integration.push_journal.v1";
    private const string ReservationSchema = "questionable.file_manager.integration.push_reservation.v1";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string _root;
    private readonly Func<DateTimeOffset> _utcNow;

    public FleetPushOperationStore(string root, Func<DateTimeOffset> utcNow)
    {
        _root = FleetPathPolicy.RequireSafeExistingRoot(root);
        _utcNow = utcNow;
    }

    public FleetPushOperationLease Begin(
        FleetIntegrationOperationRequest request,
        string verifiedAuthorityDigest)
    {
        ArgumentNullException.ThrowIfNull(request);
        var operationsRoot = Path.Combine(_root, "operations");
        Directory.CreateDirectory(operationsRoot);
        using (var operationsHandle = FleetWindowsFileSafety.OpenDirectory(operationsRoot, allowDelete: false))
        {
            FleetWindowsFileSafety.ValidateDirectory(operationsHandle, operationsRoot);
        }

        var reservationPath = Path.Combine(operationsRoot, request.OperationId + ".lock");
        using (var reservation = FleetWindowsFileSafety.CreateNewOwnedFileHandle(reservationPath))
        {
            FleetWindowsFileSafety.ValidateFile(reservation, reservationPath, requireSingleLink: true);
            var reservationStatus = CreateStatus(
                request,
                FleetIntegrationOperationPhase.Accepted,
                FleetIntegrationCleanupState.NotRequired,
                null,
                null,
                destinationMayExist: false,
                partialMayExist: false,
                _utcNow().ToUniversalTime(),
                "The operation ID is durably reserved; no remote transfer has started.");
            var reservationDocument = new FleetPushReservation(
                ReservationSchema,
                request,
                reservationStatus,
                ComputeRequestDigest(request),
                request.Operation.LocalArtifactPath!,
                $".qfm-{request.OperationId}.partial",
                verifiedAuthorityDigest);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(reservationDocument, JsonOptions);
            RandomAccess.Write(reservation, bytes, 0);
            RandomAccess.FlushToDisk(reservation);
            FleetWindowsFileSafety.ValidateFile(reservation, reservationPath, requireSingleLink: true);
        }

        var operationRoot = Path.Combine(operationsRoot, request.OperationId);
        SafeFileHandle? operationHandle = null;
        SafeFileHandle? ownerHandle = null;
        FleetPushOperationLease? lease = null;
        try
        {
            if (Directory.Exists(operationRoot) || File.Exists(operationRoot))
            {
                throw FleetIntegrationException.Input(
                    "destination_collision",
                    "The push operation ID is already present. Operation IDs are one-use.");
            }
            Directory.CreateDirectory(operationRoot);
            operationHandle = FleetWindowsFileSafety.OpenDirectory(operationRoot, allowDelete: false);
            FleetWindowsFileSafety.ValidateDirectory(operationHandle, operationRoot);
            var ownerPath = Path.Combine(operationRoot, "owner.live");
            ownerHandle = FleetWindowsFileSafety.CreateNewOwnedFileHandle(ownerPath);
            FleetWindowsFileSafety.ValidateFile(ownerHandle, ownerPath, requireSingleLink: true);
            var requestDigest = ComputeRequestDigest(request);
            var partialName = $".qfm-{request.OperationId}.partial";
            lease = new FleetPushOperationLease(
                _root,
                operationRoot,
                operationHandle,
                ownerPath,
                ownerHandle,
                request,
                requestDigest,
                partialName,
                verifiedAuthorityDigest,
                _utcNow);
            lease.Append(
                FleetIntegrationOperationPhase.Accepted,
                FleetIntegrationCleanupState.NotRequired,
                null,
                null,
                "The push request was admitted with current mutation authority.");
            operationHandle = null;
            ownerHandle = null;
            return lease;
        }
        catch
        {
            if (lease is not null)
            {
                lease.Dispose();
            }
            else
            {
                ownerHandle?.Dispose();
                operationHandle?.Dispose();
            }
            // The retained reservation is the one-use replay tombstone. Never remove it by path.
            throw;
        }
    }

    public FleetIntegrationOperationStatusSnapshot ReadStatus(string operationId)
    {
        ValidateIdentifier(operationId);
        var operationRoot = Path.Combine(_root, "operations", operationId);
        if (!Directory.Exists(operationRoot))
        {
            var reservationPath = Path.Combine(_root, "operations", operationId + ".lock");
            if (!File.Exists(reservationPath))
            {
                throw new FleetIntegrationException(
                    FleetIntegrationStatus.Absent,
                    "operation_absent",
                    "The durable push operation is absent.",
                    retryable: false);
            }
            try
            {
                var reservation = ReadReservation(reservationPath, operationId);
                return reservation.Status with
                {
                    Phase = FleetIntegrationOperationPhase.RecoveryRequired,
                    CleanupState = FleetIntegrationCleanupState.NotRequired,
                    DestinationMayExist = false,
                    PartialMayExist = false,
                    UpdatedAtUtc = _utcNow().ToUniversalTime(),
                    Reason = "The operation ID reservation survived without an operation journal; no remote transfer was started."
                };
            }
            catch (JsonException exception)
            {
                throw new FleetIntegrationException(
                    FleetIntegrationStatus.Failed,
                    "operation_reservation_invalid",
                    "The operation reservation exists but is not a valid push reservation.",
                    retryable: false,
                    exception);
            }
        }
        using var handle = FleetWindowsFileSafety.OpenDirectory(operationRoot, allowDelete: false);
        FleetWindowsFileSafety.ValidateDirectory(handle, operationRoot);
        if (!Directory.EnumerateFiles(operationRoot, "state-*.json").Any())
        {
            var reservation = ReadReservation(
                Path.Combine(_root, "operations", operationId + ".lock"),
                operationId);
            FleetWindowsFileSafety.ValidateDirectory(handle, operationRoot);
            var ownerLive = IsOwnerLive(operationRoot);
            return reservation.Status with
            {
                Phase = ownerLive
                    ? FleetIntegrationOperationPhase.Accepted
                    : FleetIntegrationOperationPhase.RecoveryRequired,
                CleanupState = FleetIntegrationCleanupState.NotRequired,
                DestinationMayExist = false,
                PartialMayExist = false,
                UpdatedAtUtc = _utcNow().ToUniversalTime(),
                Reason = ownerLive
                    ? "The admitted operation is live but its first journal state is not yet visible; no remote transfer has started."
                    : "The operation directory survived without a journal or live owner; no remote transfer was started."
            };
        }
        var latest = ReadLatest(operationRoot, operationId);
        FleetWindowsFileSafety.ValidateDirectory(handle, operationRoot);
        if (latest.Status.Phase is FleetIntegrationOperationPhase.Accepted or
            FleetIntegrationOperationPhase.Running or
            FleetIntegrationOperationPhase.CancelRequested)
        {
            if (IsOwnerLive(operationRoot))
            {
                return latest.Status;
            }
            return latest.Status with
            {
                Phase = FleetIntegrationOperationPhase.RecoveryRequired,
                CleanupState = FleetIntegrationCleanupState.Unknown,
                DestinationMayExist = true,
                PartialMayExist = true,
                UpdatedAtUtc = _utcNow().ToUniversalTime(),
                Reason = "The prior process ended without a terminal journal entry; inspect the exact device before retry or cleanup."
            };
        }
        return latest.Status;
    }

    public FleetIntegrationOperationStatusSnapshot RequestCancellation(string operationId)
    {
        ValidateIdentifier(operationId);
        var operationRoot = Path.Combine(_root, "operations", operationId);
        using var handle = FleetWindowsFileSafety.OpenDirectory(operationRoot, allowDelete: false);
        FleetWindowsFileSafety.ValidateDirectory(handle, operationRoot);
        var markerPath = Path.Combine(operationRoot, "cancel.request");
        if (!File.Exists(markerPath))
        {
            try
            {
                using var marker = FleetWindowsFileSafety.CreateNewOwnedFileHandle(markerPath);
                FleetWindowsFileSafety.ValidateFile(marker, markerPath, requireSingleLink: true);
            }
            catch (FleetIntegrationException exception) when (
                exception.Code == "destination_collision")
            {
                // Another cancellation caller won the same idempotent creation race.
            }
        }
        var latest = ReadLatest(operationRoot, operationId);
        if (latest.Status.Phase is FleetIntegrationOperationPhase.Completed or
            FleetIntegrationOperationPhase.Cancelled or
            FleetIntegrationOperationPhase.Failed or
            FleetIntegrationOperationPhase.CleanupRequired)
        {
            return latest.Status;
        }
        var status = latest.Status with
        {
            Phase = FleetIntegrationOperationPhase.CancelRequested,
            CleanupState = FleetIntegrationCleanupState.Pending,
            DestinationMayExist = true,
            PartialMayExist = true,
            UpdatedAtUtc = _utcNow().ToUniversalTime(),
            Reason = "Cancellation was durably requested; terminal cleanup remains separately observable."
        };
        Append(operationRoot, latest with { Sequence = 0, Status = status });
        return status;
    }

    public (FleetIntegrationOperationRequest Request, string VerifiedAuthorityDigest)
        ReadOperationAuthority(string operationId)
    {
        ValidateIdentifier(operationId);
        var reservation = ReadReservation(
            Path.Combine(_root, "operations", operationId + ".lock"),
            operationId);
        return (reservation.Request, reservation.VerifiedAuthorityDigest);
    }

    private static FleetPushJournalEntry ReadLatest(
        string operationRoot,
        string expectedOperationId)
    {
        var reservation = ReadReservation(
            Path.Combine(
                Path.GetDirectoryName(operationRoot)
                    ?? throw new InvalidOperationException("Operation root has no parent."),
                expectedOperationId + ".lock"),
            expectedOperationId);
        var candidates = Directory.EnumerateFiles(operationRoot, "state-*.json")
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        if (candidates.Length == 0)
        {
            throw new FleetIntegrationException(
                FleetIntegrationStatus.Failed,
                "operation_journal_missing",
                "The durable operation directory contains no journal state.",
                retryable: false);
        }
        FleetPushJournalEntry? latest = null;
        FleetPushJournalEntry? previous = null;
        for (var index = 0; index < candidates.Length; index++)
        {
            var path = candidates[index];
            using var stream = FleetWindowsFileSafety.OpenReadOnlyFile(path);
            FleetWindowsFileSafety.ValidateFile(stream.SafeFileHandle, path, requireSingleLink: true);
            FleetPushJournalEntry entry;
            try
            {
                entry = DeserializeStrict<FleetPushJournalEntry>(stream)
                    ?? throw new JsonException("The journal entry is empty.");
            }
            catch (JsonException exception)
            {
                throw new FleetIntegrationException(
                    FleetIntegrationStatus.Failed,
                    "operation_journal_invalid",
                    "A durable operation journal entry is not strict JSON.",
                    retryable: false,
                    exception);
            }
            var expectedSequence = index + 1;
            var expectedFileName = $"state-{expectedSequence:D4}.json";
            if (!string.Equals(Path.GetFileName(path), expectedFileName, StringComparison.Ordinal) ||
                !string.Equals(entry.Schema, JournalSchema, StringComparison.Ordinal) ||
                entry.Sequence != expectedSequence ||
                !string.Equals(entry.Status.Schema, FleetIntegrationContract.OperationStatusSchema, StringComparison.Ordinal) ||
                !string.Equals(entry.Status.ContractVersion, FleetIntegrationContract.Version, StringComparison.Ordinal) ||
                !string.Equals(entry.Status.OperationId, expectedOperationId, StringComparison.Ordinal) ||
                !IsLowerHexDigest(entry.RequestDigest) ||
                entry.VerifiedAuthorityDigest is null ||
                !IsLowerHexDigest(entry.VerifiedAuthorityDigest) ||
                !string.Equals(entry.RemotePartialName, $".qfm-{expectedOperationId}.partial", StringComparison.Ordinal))
            {
                throw new FleetIntegrationException(
                    FleetIntegrationStatus.Failed,
                    "operation_journal_invalid",
                    "A durable operation journal entry has an unsupported shape.",
                    retryable: false);
            }
            FleetPathPolicy.ValidatePushArtifactPath(entry.LocalArtifactPath);
            if (previous is not null)
            {
                if (!SameIdentity(previous, entry) ||
                    !IsAllowedTransition(previous.Status.Phase, entry.Status.Phase))
                {
                    throw new FleetIntegrationException(
                        FleetIntegrationStatus.Failed,
                        "operation_journal_invalid",
                        "The durable operation journal identity or phase chain is inconsistent.",
                        retryable: false);
                }
            }
            else if (!SameReservationIdentity(reservation, entry))
            {
                throw new FleetIntegrationException(
                    FleetIntegrationStatus.Failed,
                    "operation_journal_invalid",
                    "The first durable journal entry does not match its one-use reservation.",
                    retryable: false);
            }
            previous = entry;
            latest = entry;
        }
        return latest!;
    }

    private static FleetPushReservation ReadReservation(
        string reservationPath,
        string expectedOperationId)
    {
        using var reservationStream = FleetWindowsFileSafety.OpenReadOnlyFile(reservationPath);
        FleetWindowsFileSafety.ValidateFile(
            reservationStream.SafeFileHandle,
            reservationPath,
            requireSingleLink: true);
        FleetPushReservation? reservation;
        try
        {
            reservation = DeserializeStrict<FleetPushReservation>(reservationStream);
        }
        catch (JsonException exception)
        {
            throw new FleetIntegrationException(
                FleetIntegrationStatus.Failed,
                "operation_reservation_invalid",
                "The durable operation reservation is not strict JSON.",
                retryable: false,
                exception);
        }
        if (reservation is null ||
            !string.Equals(reservation.Schema, ReservationSchema, StringComparison.Ordinal) ||
            !string.Equals(reservation.Status.Schema, FleetIntegrationContract.OperationStatusSchema, StringComparison.Ordinal) ||
            !string.Equals(reservation.Status.OperationId, expectedOperationId, StringComparison.Ordinal) ||
            !string.Equals(reservation.Request.OperationId, expectedOperationId, StringComparison.Ordinal) ||
            !string.Equals(ComputeRequestDigest(reservation.Request), reservation.RequestDigest, StringComparison.Ordinal) ||
            !IsLowerHexDigest(reservation.RequestDigest) ||
            !IsLowerHexDigest(reservation.VerifiedAuthorityDigest))
        {
            throw new FleetIntegrationException(
                FleetIntegrationStatus.Failed,
                "operation_reservation_invalid",
                "The durable operation reservation is invalid or does not bind its request.",
                retryable: false);
        }
        FleetPathPolicy.ValidatePushArtifactPath(reservation.LocalArtifactPath);
        return reservation;
    }

    private static T? DeserializeStrict<T>(Stream stream)
    {
        if (stream.Length is < 1 or > FleetIntegrationContract.MaximumRequestBytes * 2L)
        {
            throw new JsonException("The durable document size is invalid.");
        }
        using var memory = new MemoryStream(checked((int)stream.Length));
        stream.CopyTo(memory);
        var bytes = memory.ToArray();
        using var document = JsonDocument.Parse(
            bytes,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });
        ValidateNoDuplicateProperties(document.RootElement);
        return JsonSerializer.Deserialize<T>(bytes, JsonOptions);
    }

    private static void ValidateNoDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new JsonException($"Duplicate property '{property.Name}'.");
                }
                ValidateNoDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                ValidateNoDuplicateProperties(item);
            }
        }
    }

    private static bool SameReservationIdentity(
        FleetPushReservation reservation,
        FleetPushJournalEntry entry) =>
        string.Equals(reservation.RequestDigest, entry.RequestDigest, StringComparison.Ordinal) &&
        string.Equals(reservation.LocalArtifactPath, entry.LocalArtifactPath, StringComparison.Ordinal) &&
        string.Equals(reservation.RemotePartialName, entry.RemotePartialName, StringComparison.Ordinal) &&
        string.Equals(reservation.VerifiedAuthorityDigest, entry.VerifiedAuthorityDigest, StringComparison.Ordinal) &&
        string.Equals(reservation.Status.OperationId, entry.Status.OperationId, StringComparison.Ordinal) &&
        string.Equals(reservation.Status.RequestId, entry.Status.RequestId, StringComparison.Ordinal) &&
        string.Equals(reservation.Status.AdapterEpoch, entry.Status.AdapterEpoch, StringComparison.Ordinal) &&
        string.Equals(reservation.Status.Serial, entry.Status.Serial, StringComparison.Ordinal) &&
        string.Equals(reservation.Status.Transport, entry.Status.Transport, StringComparison.Ordinal) &&
        string.Equals(reservation.Status.RelativePath, entry.Status.RelativePath, StringComparison.Ordinal) &&
        reservation.Status.ExpectedSizeBytes == entry.Status.ExpectedSizeBytes &&
        string.Equals(reservation.Status.ExpectedSha256, entry.Status.ExpectedSha256, StringComparison.Ordinal);

    private static bool IsOwnerLive(string operationRoot)
    {
        var ownerPath = Path.Combine(operationRoot, "owner.live");
        try
        {
            using var stream = FleetWindowsFileSafety.OpenReadOnlyFile(ownerPath);
            FleetWindowsFileSafety.ValidateFile(stream.SafeFileHandle, ownerPath, requireSingleLink: true);
            return false;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode is 32 or 33)
        {
            return true;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode is 2 or 3)
        {
            return false;
        }
    }

    private static bool SameIdentity(
        FleetPushJournalEntry left,
        FleetPushJournalEntry right) =>
        string.Equals(left.RequestDigest, right.RequestDigest, StringComparison.Ordinal) &&
        string.Equals(left.LocalArtifactPath, right.LocalArtifactPath, StringComparison.Ordinal) &&
        string.Equals(left.RemotePartialName, right.RemotePartialName, StringComparison.Ordinal) &&
        string.Equals(left.VerifiedAuthorityDigest, right.VerifiedAuthorityDigest, StringComparison.Ordinal) &&
        string.Equals(left.Status.OperationId, right.Status.OperationId, StringComparison.Ordinal) &&
        string.Equals(left.Status.RequestId, right.Status.RequestId, StringComparison.Ordinal) &&
        string.Equals(left.Status.AdapterEpoch, right.Status.AdapterEpoch, StringComparison.Ordinal) &&
        string.Equals(left.Status.Serial, right.Status.Serial, StringComparison.Ordinal) &&
        string.Equals(left.Status.Transport, right.Status.Transport, StringComparison.Ordinal) &&
        string.Equals(left.Status.RelativePath, right.Status.RelativePath, StringComparison.Ordinal) &&
        left.Status.ExpectedSizeBytes == right.Status.ExpectedSizeBytes &&
        string.Equals(left.Status.ExpectedSha256, right.Status.ExpectedSha256, StringComparison.Ordinal);

    private static bool IsAllowedTransition(
        FleetIntegrationOperationPhase from,
        FleetIntegrationOperationPhase to) =>
        (from, to) switch
        {
            (FleetIntegrationOperationPhase.Accepted, FleetIntegrationOperationPhase.Running or
                FleetIntegrationOperationPhase.CancelRequested or
                FleetIntegrationOperationPhase.Failed) => true,
            (FleetIntegrationOperationPhase.Running, FleetIntegrationOperationPhase.CancelRequested or
                FleetIntegrationOperationPhase.Completed or
                FleetIntegrationOperationPhase.Cancelled or
                FleetIntegrationOperationPhase.Failed or
                FleetIntegrationOperationPhase.CleanupRequired) => true,
            (FleetIntegrationOperationPhase.CancelRequested, FleetIntegrationOperationPhase.CancelRequested or
                FleetIntegrationOperationPhase.Completed or
                FleetIntegrationOperationPhase.Cancelled or
                FleetIntegrationOperationPhase.Failed or
                FleetIntegrationOperationPhase.CleanupRequired) => true,
            _ => false
        };

    private static bool IsLowerHexDigest(string value) =>
        value.Length == 64 &&
        value.All(static character =>
            char.IsAsciiDigit(character) || character is >= 'a' and <= 'f');

    internal static int Append(string operationRoot, FleetPushJournalEntry entry)
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var existingPaths = Directory.EnumerateFiles(operationRoot, "state-*.json").ToArray();
            var previous = existingPaths.Length == 0
                ? null
                : ReadLatest(operationRoot, entry.Status.OperationId);
            if (previous is not null &&
                (!SameIdentity(previous, entry) ||
                 !IsAllowedTransition(previous.Status.Phase, entry.Status.Phase)))
            {
                throw new FleetIntegrationException(
                    FleetIntegrationStatus.Failed,
                    "operation_journal_transition_rejected",
                    "The durable operation transition is stale or invalid.",
                    retryable: false);
            }
            var sequence = (previous?.Sequence ?? 0) + 1;
            var candidate = entry with { Sequence = sequence };
            var path = Path.Combine(operationRoot, $"state-{sequence:D4}.json");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(candidate, JsonOptions);
            try
            {
                using var stream = FleetWindowsFileSafety.CreateNewOwnedFile(path);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
                FleetWindowsFileSafety.ValidateFile(stream.SafeFileHandle, path, requireSingleLink: true);
                return sequence;
            }
            catch (FleetIntegrationException exception) when (
                exception.Code == "destination_collision")
            {
                // A concurrent cancellation/status transition won this sequence.
            }
        }
        throw new FleetIntegrationException(
            FleetIntegrationStatus.Failed,
            "operation_journal_busy",
            "The durable operation journal did not converge after concurrent transitions.",
            retryable: true);
    }

    private static string ComputeRequestDigest(FleetIntegrationOperationRequest request)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    internal static FleetIntegrationOperationStatusSnapshot CreateStatus(
        FleetIntegrationOperationRequest request,
        FleetIntegrationOperationPhase phase,
        FleetIntegrationCleanupState cleanupState,
        long? observedSizeBytes,
        string? observedSha256,
        bool destinationMayExist,
        bool partialMayExist,
        DateTimeOffset updatedAtUtc,
        string? reason) =>
        new(
            FleetIntegrationContract.OperationStatusSchema,
            FleetIntegrationContract.Version,
            request.OperationId,
            request.RequestId,
            request.AdapterEpoch,
            request.DeviceBinding.Serial,
            request.DeviceBinding.Transport,
            request.Operation.RelativePath,
            phase,
            cleanupState,
            request.Operation.ExpectedSizeBytes!.Value,
            request.Operation.ExpectedSha256!,
            observedSizeBytes,
            observedSha256,
            destinationMayExist,
            partialMayExist,
            updatedAtUtc,
            reason);

    internal static void ValidateIdentifier(string value)
    {
        if (string.IsNullOrEmpty(value) ||
            value.Length > 64 ||
            !char.IsAsciiLetterOrDigit(value[0]) ||
            value.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
        {
            throw FleetIntegrationException.Input(
                "identifier_invalid",
                "The operation ID is invalid.");
        }
    }
}

internal sealed class FleetPushOperationLease : IDisposable
{
    private readonly SafeFileHandle _operationHandle;
    private readonly string _ownerPath;
    private SafeFileHandle? _ownerHandle;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly CancellationTokenSource _durableCancellation = new();
    private readonly Task _pollTask;
    private int _sequence;
    private bool _disposed;

    public FleetPushOperationLease(
        string configuredRoot,
        string operationRoot,
        SafeFileHandle operationHandle,
        string ownerPath,
        SafeFileHandle ownerHandle,
        FleetIntegrationOperationRequest request,
        string requestDigest,
        string remotePartialName,
        string verifiedAuthorityDigest,
        Func<DateTimeOffset> utcNow)
    {
        ConfiguredRoot = configuredRoot;
        OperationRoot = operationRoot;
        _ownerPath = ownerPath;
        _ownerHandle = ownerHandle;
        Request = request;
        RequestDigest = requestDigest;
        RemotePartialName = remotePartialName;
        VerifiedAuthorityDigest = verifiedAuthorityDigest;
        _operationHandle = operationHandle;
        _utcNow = utcNow;
        _pollTask = PollCancellationAsync();
    }

    public string ConfiguredRoot { get; }
    public string OperationRoot { get; }
    public FleetIntegrationOperationRequest Request { get; }
    public string RequestDigest { get; }
    public string RemotePartialName { get; }
    public string VerifiedAuthorityDigest { get; }
    public CancellationToken DurableCancellationToken => _durableCancellation.Token;

    public FleetIntegrationOperationStatusSnapshot Append(
        FleetIntegrationOperationPhase phase,
        FleetIntegrationCleanupState cleanupState,
        long? observedSizeBytes,
        string? observedSha256,
        string? reason,
        bool destinationMayExist = false,
        bool partialMayExist = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        FleetWindowsFileSafety.ValidateDirectory(_operationHandle, OperationRoot);
        var status = FleetPushOperationStore.CreateStatus(
            Request,
            phase,
            cleanupState,
            observedSizeBytes,
            observedSha256,
            destinationMayExist,
            partialMayExist,
            _utcNow().ToUniversalTime(),
            reason);
        var entry = new FleetPushJournalEntry(
            "questionable.file_manager.integration.push_journal.v1",
            ++_sequence,
            status,
            RequestDigest,
            Request.Operation.LocalArtifactPath!,
            RemotePartialName,
            VerifiedAuthorityDigest);
        _sequence = FleetPushOperationStore.Append(OperationRoot, entry);
        FleetWindowsFileSafety.ValidateDirectory(_operationHandle, OperationRoot);
        return status;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _durableCancellation.Cancel();
        try
        {
            _pollTask.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        _durableCancellation.Dispose();
        if (_ownerHandle is not null)
        {
            try
            {
                FleetWindowsFileSafety.ValidateFile(
                    _ownerHandle,
                    _ownerPath,
                    requireSingleLink: true);
                FleetWindowsFileSafety.MarkDelete(_ownerHandle);
            }
            finally
            {
                _ownerHandle.Dispose();
                _ownerHandle = null;
            }
        }
        _operationHandle.Dispose();
        _disposed = true;
    }

    private async Task PollCancellationAsync()
    {
        var marker = Path.Combine(OperationRoot, "cancel.request");
        while (!_durableCancellation.IsCancellationRequested)
        {
            if (CancellationMarkerExistsOrIsUncertain(marker))
            {
                _durableCancellation.Cancel();
                return;
            }
            await Task.Delay(100, _durableCancellation.Token).ConfigureAwait(false);
        }
    }

    private static bool CancellationMarkerExistsOrIsUncertain(string marker)
    {
        try
        {
            using var stream = FleetWindowsFileSafety.OpenReadOnlyFile(marker);
            FleetWindowsFileSafety.ValidateFile(stream.SafeFileHandle, marker, requireSingleLink: true);
            return true;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode is 2 or 3)
        {
            return false;
        }
        catch
        {
            // An unreadable or substituted cancellation marker must stop mutation.
            return true;
        }
    }
}
