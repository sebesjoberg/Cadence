namespace Cadence.Storage;

/// <summary>What a token is allowed to do.</summary>
/// <remarks>
/// Two levels rather than a permission set, because the surface has two kinds of endpoint and a
/// finer grain would be a policy language nobody asked for. The failure this prevents is concrete:
/// pause is on the token surface, so without scopes a leaked monitoring token can halt scheduled
/// work across the cluster.
/// </remarks>
public enum ApiTokenScope
{
    /// <summary>The read endpoints only.</summary>
    Read = 0,

    /// <summary>Reads, plus trigger and pause.</summary>
    Operate = 1,
}
