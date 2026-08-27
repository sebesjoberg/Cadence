using System.Net;
using Microsoft.AspNetCore.Http;

namespace Cadence.Api.Internal;

/// <summary>
/// Refuses non-loopback callers on the one branch of the gate that authenticates nobody:
/// <c>Development</c> with no token, no policy and <see cref="CadenceApiOptions.AllowUnauthenticated"/>
/// unset.
/// </summary>
/// <remarks>
/// <para>
/// A developer on localhost sees no difference. What changes is the container that shipped with
/// <c>ASPNETCORE_ENVIRONMENT=Development</c> — among the commonest .NET misconfigurations, and
/// before this branch it exposed nothing. It now exposes "run any registered job" and "halt
/// scheduling cluster-wide" to anything that can reach the port, so that mistake is made
/// unexploitable rather than merely warned about.
/// </para>
/// <para>
/// Deliberately not applied to <see cref="CadenceApiOptions.AllowUnauthenticated"/>, which is an
/// operator's explicit decision to put an authenticating proxy or an mTLS mesh in front — where
/// every caller is legitimately non-loopback — nor to a request the applied policy has already
/// authenticated.
/// </para>
/// </remarks>
internal sealed class LoopbackOnlyFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        if (!IsLoopback(context.HttpContext.Connection.RemoteIpAddress))
        {
            return ProblemMapper.AsResult(ProblemMapper.NotLoopback());
        }

        return await next(context);
    }

    /// <remarks>
    /// A null address is treated as loopback. Kestrel over TCP always fills this in, so no caller
    /// arriving over the network can be null; what produces null is a transport with no IP peer at
    /// all — the in-memory <c>TestServer</c>, a Unix domain socket, a named pipe — none of which is
    /// the exposed TCP port this filter exists to close. Refusing null instead would 403 every
    /// in-memory test host and every socket-fronted deployment while closing nothing.
    /// </remarks>
    private static bool IsLoopback(IPAddress? address)
    {
        if (address is null)
        {
            return true;
        }

        // ::ffff:127.0.0.1 is what a dual-stack listener reports for an IPv4 loopback caller.
        var normalized = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        return IPAddress.IsLoopback(normalized);
    }
}
