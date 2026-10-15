using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace RoslynQuery;

/// <summary>
/// Ref-counted lease wrapping one <see cref="MSBuildWorkspace"/>.
/// The workspace is disposed exactly once — when the lease is retired AND the ref-count reaches zero.
///
/// The lease starts with an implicit ref-count of 1 held by the daemon (the "current" ref).
/// <see cref="Retire"/> releases the daemon's implicit ref and marks the lease as superseded.
/// Additional callers acquire refs via <see cref="AddRef"/> and release them via <see cref="Release"/>.
/// Disposal is deferred until all refs (including the daemon's) are released — even refs acquired
/// after <see cref="Retire"/> returns.
///
/// <para>
/// Encoding: a single <c>int _state</c> packs both the retired bit and the ref-count atomically.
/// Bit 31 is the "retired" flag; bits 0–30 hold the ref-count (max ~2 billion concurrent queries).
/// Any operation that leaves the state as exactly <see cref="RetiredBit"/> (retired + count = 0)
/// triggers disposal. <c>Interlocked.CompareExchange</c> on <c>_disposed</c> ensures
/// exactly-once disposal even when two threads race to that state simultaneously.
/// </para>
/// </summary>
public sealed class WorkspaceLease
{
    private const int RetiredBit = unchecked((int)0x80000000);

    private readonly MSBuildWorkspace? _workspace;
    private readonly IDisposable _target;

    // Bit 31 = retired flag; bits 0-30 = ref-count.
    // Initial value is 1 (daemon's implicit ref, not retired).
    private int _state = 1;
    private int _disposed;

    /// <summary>
    /// Wraps a real <see cref="MSBuildWorkspace"/> for production use.
    /// </summary>
    public WorkspaceLease(MSBuildWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        _workspace = workspace;
        _target = workspace;
    }

    /// <summary>
    /// Internal seam: accepts any <see cref="IDisposable"/> so unit tests can observe disposal
    /// without constructing a real <see cref="MSBuildWorkspace"/>.
    /// Earns its shape from the production path — the production constructor delegates here with
    /// the workspace as both its <see cref="MSBuildWorkspace"/> reference and its disposal target.
    /// </summary>
    internal WorkspaceLease(IDisposable target)
    {
        ArgumentNullException.ThrowIfNull(target);
        _target = target;
    }

    /// <summary>Exposes the current solution from the underlying workspace.</summary>
    public Solution CurrentSolution => _workspace!.CurrentSolution;

    /// <summary>Increments the ref-count. Must be balanced by a call to <see cref="Release"/>.</summary>
    public void AddRef() => Interlocked.Increment(ref _state);

    /// <summary>
    /// Decrements the ref-count. When the count reaches zero and the lease has been retired,
    /// disposes the workspace exactly once. Double-release is idempotent.
    /// </summary>
    public void Release()
    {
        int before = Interlocked.Decrement(ref _state);

        // If the decrement underflowed the count bits (was already 0), restore it.
        if ((before & ~RetiredBit) == ~RetiredBit)
        {
            Interlocked.Increment(ref _state);
            return;
        }

        MaybeDispose(before);
    }

    /// <summary>
    /// Marks this lease as superseded and releases the daemon's implicit ref.
    /// After this call the daemon no longer holds the lease as "current".
    /// Idempotent — a second call is a no-op.
    /// </summary>
    public void Retire()
    {
        // Atomically set the retired bit. If it was already set, nothing to do.
        int before = Interlocked.Or(ref _state, RetiredBit);
        if ((before & RetiredBit) != 0)
        {
            return; // already retired
        }

        // Release the daemon's implicit ref now that we're marked as retired.
        int afterDecrement = Interlocked.Decrement(ref _state);
        MaybeDispose(afterDecrement);
    }

    private void MaybeDispose(int stateAfterChange)
    {
        // Dispose only when retired (bit 31 set) and count is 0 (bits 0–30 are 0),
        // i.e. state == RetiredBit exactly.
        if (stateAfterChange == RetiredBit
            && Interlocked.CompareExchange(ref _disposed, 1, 0) == 0)
        {
            _target.Dispose();
        }
    }
}
