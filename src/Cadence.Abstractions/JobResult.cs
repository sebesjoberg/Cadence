using System.Text;

namespace Cadence;

/// <summary>
/// What a run produced: bytes, and enough about them to hand back over HTTP.
/// </summary>
/// <remarks>
/// <para>
/// Cadence deliberately does not know what a result <em>is</em>. It stores an opaque byte range it
/// was told the content type of, and returns it with that type and, where one was given, a
/// filename. Turning a job's own return type into these three things is
/// <see cref="IJobResultSerializer{TResult}"/>'s job, which is what keeps "a result is a
/// spreadsheet" out of the scheduler and in the application.
/// </para>
/// <para>
/// The content is buffered rather than streamed because a job that produced it held it in memory
/// already. Reading a stored result streams; writing one does not.
/// </para>
/// </remarks>
public sealed record JobResult
{
    /// <summary>The bytes themselves.</summary>
    public required ReadOnlyMemory<byte> Content { get; init; }

    /// <summary>The media type the bytes are served with.</summary>
    public required string ContentType { get; init; }

    /// <summary>
    /// Suggested filename, offered as <c>Content-Disposition</c>. Null serves the bytes inline.
    /// </summary>
    public string? FileName { get; init; }

    /// <summary>How many bytes the result holds.</summary>
    public long Length => Content.Length;

    /// <summary>Creates a result from raw bytes.</summary>
    /// <param name="content">The bytes.</param>
    /// <param name="contentType">The media type to serve them with.</param>
    /// <param name="fileName">Optional suggested filename.</param>
    public static JobResult Bytes(
        ReadOnlyMemory<byte> content,
        string contentType,
        string? fileName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);

        return new JobResult { Content = content, ContentType = contentType, FileName = fileName };
    }

    /// <summary>Creates a UTF-8 text result.</summary>
    /// <param name="text">The text.</param>
    /// <param name="contentType">The media type. Defaults to plain text.</param>
    /// <param name="fileName">Optional suggested filename.</param>
    public static JobResult Text(
        string text,
        string contentType = "text/plain; charset=utf-8",
        string? fileName = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        return Bytes(Encoding.UTF8.GetBytes(text), contentType, fileName);
    }

    /// <summary>Creates a CSV result.</summary>
    /// <param name="csv">The document body.</param>
    /// <param name="fileName">Suggested filename.</param>
    public static JobResult Csv(string csv, string fileName)
        => Text(csv, "text/csv; charset=utf-8", fileName);

    /// <summary>Creates an Office Open XML workbook result.</summary>
    /// <param name="content">The workbook bytes.</param>
    /// <param name="fileName">Suggested filename.</param>
    public static JobResult Xlsx(ReadOnlyMemory<byte> content, string fileName)
        => Bytes(
            content,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            fileName);
}
