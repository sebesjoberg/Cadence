using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;

namespace Cadence.Api.Internal;

/// <summary>
/// §4.5's CSRF rule: a request the ticket cookie authenticated is accepted, on every method, only
/// when it also carries <see cref="CadenceApiDefaults.SessionHeader"/>.
/// </summary>
internal sealed class SessionHeaderFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var http = context.HttpContext;

        if (http.Request.Headers.ContainsKey(CadenceApiDefaults.SessionHeader))
        {
            return await next(context);
        }

        var ticket = await http.AuthenticateAsync(CadenceApiDefaults.CookieScheme);

        return ticket.Succeeded
            ? ProblemMapper.AsResult(ProblemMapper.MissingSessionHeader())
            : await next(context);
    }
}
