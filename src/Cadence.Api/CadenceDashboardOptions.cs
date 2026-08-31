namespace Cadence.Api;

/// <summary>
/// Settings for the operator dashboard. Nested on <see cref="CadenceApiOptions"/> because §7
/// answer #6 kept one options object across both trees; it lives in this package because that
/// object is typed as it, and <c>Cadence.Api</c> cannot reference <c>Cadence.Dashboard</c>.
/// </summary>
public sealed class CadenceDashboardOptions
{
    /// <summary>
    /// The name shown in the header. It earns a setting because an operator with production and
    /// staging open in two tabs has to tell them apart before clicking Trigger.
    /// </summary>
    public string Title { get; set; } = "Cadence";
}
