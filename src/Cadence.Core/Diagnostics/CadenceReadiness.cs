namespace Cadence.Diagnostics;

/// <summary>
/// Whether boot completed. Set once by the hosted service and read by the readiness probe.
/// </summary>
/// <remarks>
/// <para>
/// A flag rather than a query against anything, because §13.4 requires that the probe the kubelet
/// reads cannot fail on a store blip. Every replica shares one store, so a readiness probe that is
/// honest about store health takes every pod out of the service at the same moment — and stalls the
/// rolling deploy that would have fixed it.
/// </para>
/// <para>
/// Public deliberately, and not merely because the hosted service sets it: <see cref="Scheduling.ScheduleTicker"/>
/// is public too, so a host may compose the ticker itself and never register
/// <c>CadenceHostedService</c>. Such a host has no other way to make the ready probe truthful, and
/// <c>InternalsVisibleTo</c> would not help it. The flag is one-way - once set it stays set, because
/// the default host behaviour on a failed job is to stop the process, not to go un-ready.
/// </para>
/// </remarks>
public sealed class CadenceReadiness
{
    private volatile bool _ready;

    /// <summary>True once the boot probe has passed and jobs are registered.</summary>
    public bool IsReady => _ready;

    /// <summary>
    /// Records that boot completed. Called once - by <c>CadenceHostedService</c> as the last step of
    /// its start path, or by a host that drives <see cref="Scheduling.ScheduleTicker"/> without it. There is no
    /// way back: readiness is not withdrawn once granted.
    /// </summary>
    public void MarkReady() => _ready = true;
}
