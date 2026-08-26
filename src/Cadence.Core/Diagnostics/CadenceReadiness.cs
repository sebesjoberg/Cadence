namespace Cadence.Diagnostics;

/// <summary>
/// Whether boot completed. Set once by the hosted service and read by the readiness probe.
/// </summary>
/// <remarks>
/// A flag rather than a query against anything, because §13.4 requires that the probe the kubelet
/// reads cannot fail on a store blip. Every replica shares one store, so a readiness probe that is
/// honest about store health takes every pod out of the service at the same moment — and stalls the
/// rolling deploy that would have fixed it.
/// </remarks>
public sealed class CadenceReadiness
{
    private volatile bool _ready;

    /// <summary>True once the boot probe has passed and jobs are registered.</summary>
    public bool IsReady => _ready;

    /// <summary>Records that boot completed. Called once, from the hosted service's start path.</summary>
    public void MarkReady() => _ready = true;
}
