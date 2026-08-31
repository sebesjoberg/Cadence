using System.Globalization;
using ClosedXML.Excel;
using Microsoft.Extensions.Logging;

namespace Cadence.Sample.ClusteredWorker;

/// <summary>What a report is asked for. Bound from <see cref="JobContext.Payload"/>, so null today.</summary>
/// <param name="Customer">Whose sales to report on.</param>
/// <param name="Days">How many days back to cover.</param>
public sealed record ReportRequest(string? Customer, int Days);

/// <summary>
/// Builds a real spreadsheet and hands it back, so the dashboard has something to download.
/// </summary>
/// <remarks>
/// <para>
/// The job Cadence's result support exists for, in its shortest form. It implements
/// <see cref="IResultJob{TRequest, TResult}"/> with <see cref="JobResult"/> as the result type,
/// which means no serializer of its own: returning bytes with a media type and a filename is the
/// whole contract, and Cadence stores them, ages them out on
/// <c>Retention.ResultMaxAge</c>, and serves them from <c>GET /cadence/api/runs/{id}/result</c>.
/// </para>
/// <para>
/// It is scheduled <em>and</em> triggerable on purpose. Neither supplies a payload today —
/// <c>POST /jobs/{name}/trigger</c> passes <c>payload: null</c> on both trees, because design plan
/// §13.2 keeps the trigger from widening into "run any job with arbitrary input" — so
/// <paramref name="request"/> arrives null and the job falls back to its default. That is the case
/// every result job on a cron has to answer for itself, and it stays the case until submitted work
/// items give a request somewhere of its own to arrive from.
/// </para>
/// <para>
/// ClosedXML is the sample's dependency, not Cadence's. Cadence never learns what a spreadsheet is;
/// it moves bytes it was told the media type of.
/// </para>
/// </remarks>
[ScheduledJob(
    Name = "sales-report",
    Cron = "0 */2 * * * *",
    MaxDuration = "00:01:00",
    Triggers = TriggerKind.Schedule | TriggerKind.Api | TriggerKind.Manual)]
public sealed class SalesReportJob(ILogger<SalesReportJob> logger)
    : IResultJob<ReportRequest, JobResult>
{
    private static readonly string[] Products =
        ["Widget", "Sprocket", "Gasket", "Flange", "Bearing"];

    public async Task<JobResult> ExecuteAsync(
        ReportRequest request,
        JobContext context,
        CancellationToken cancellationToken)
    {
        // A scheduled occurrence carries no payload, so the request is null. Every result job that
        // is also on a cron has to decide what that means; here it is the nightly default.
        var customer = request?.Customer ?? "All customers";
        var days = request?.Days is > 0 and <= 365 ? request.Days : 7;

        logger.ReportStarting(customer, days, context.InstanceId);

        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Sales");

        sheet.Cell(1, 1).Value = "Date";
        sheet.Cell(1, 2).Value = "Product";
        sheet.Cell(1, 3).Value = "Units";
        sheet.Cell(1, 4).Value = "Revenue";
        sheet.Row(1).Style.Font.Bold = true;

        var row = 2;
        var today = DateTime.UtcNow.Date;

        // Deterministic from the run id, so two runs of the same request differ and one run is
        // reproducible from what history already records.
        var random = new Random(context.RunId.GetHashCode());

        for (var day = 0; day < days; day++)
        {
            foreach (var product in Products)
            {
                var units = random.Next(1, 250);

                sheet.Cell(row, 1).Value = today.AddDays(-day);
                sheet.Cell(row, 2).Value = product;
                sheet.Cell(row, 3).Value = units;
                sheet.Cell(row, 4).Value = Math.Round(units * random.NextDouble() * 90, 2);
                row++;
            }

            // Progress the dashboard renders live, and the reason a result job still takes a
            // JobContext: producing something does not stop it being a run somebody watches.
            context.Report($"built day {day + 1} of {days}", new Dictionary<string, object?>
            {
                ["customer"] = customer,
                ["day"] = day + 1,
            });

            await Task.Delay(TimeSpan.FromMilliseconds(120), cancellationToken);
        }

        sheet.Cell(row, 3).FormulaA1 = $"SUM(C2:C{row - 1})";
        sheet.Cell(row, 4).FormulaA1 = $"SUM(D2:D{row - 1})";
        sheet.Row(row).Style.Font.Bold = true;

        // Before AdjustToContents, not after: a date carrying no explicit format is measured as
        // the serial number underneath it, which fits in a column the rendered "2026-08-31" does
        // not -- and Excel renders a date too wide for its column as ########.
        sheet.Column(1).Style.DateFormat.Format = "yyyy-MM-dd";
        sheet.Column(4).Style.NumberFormat.Format = "#,##0.00";

        sheet.Columns().AdjustToContents();

        using var buffer = new MemoryStream();
        workbook.SaveAs(buffer);

        var fileName = string.Create(
            CultureInfo.InvariantCulture,
            $"sales-{Slug(customer)}-{today:yyyy-MM-dd}.xlsx");

        logger.ReportFinished(fileName, buffer.Length, context.InstanceId);

        return JobResult.Xlsx(buffer.ToArray(), fileName);
    }

    private static string Slug(string value)
        => string.Concat(value.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-'))
            .Trim('-');
}
