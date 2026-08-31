using System.Net.Mime;
using Cadence.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Cadence.Api.Internal;

/// <summary>Streams a stored result to the caller and releases what the tier held open for it.</summary>
/// <remarks>
/// Written by hand rather than through <c>TypedResults.Stream</c> because a
/// <see cref="StoredJobResult"/> owns more than its stream — a SQL tier holds a connection and a
/// reader open behind it — and only disposing the stream would leak the rest.
/// </remarks>
internal sealed class JobResultDownload : IResult
{
    private readonly StoredJobResult _result;

    /// <summary>Creates the response.</summary>
    /// <param name="result">The opened result, whose disposal this takes over.</param>
    public JobResultDownload(StoredJobResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        _result = result;
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        await using (_result)
        {
            var response = httpContext.Response;

            response.ContentType = _result.Info.ContentType;
            response.ContentLength = _result.Info.Length;

            if (_result.Info.FileName is { } fileName)
            {
                // Through ContentDispositionHeaderValue so a filename with a comma, a quote or a
                // non-ASCII character is encoded rather than truncating the header at it.
                var disposition = new ContentDispositionHeaderValue(DispositionTypeNames.Attachment);
                disposition.SetHttpFileName(fileName);

                response.Headers.ContentDisposition = disposition.ToString();
            }

            await _result.Content.CopyToAsync(response.Body, httpContext.RequestAborted)
                .ConfigureAwait(false);
        }
    }
}
