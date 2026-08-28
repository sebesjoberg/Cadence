using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;

namespace Cadence.Storage.Sql.Internal;

/// <summary>
/// Stores the Data Protection key ring in the Cadence database, one row per key.
/// </summary>
/// <remarks>
/// Hand-written ADO rather than <c>PersistKeysToDbContext</c>, which is EF Core. What it stores is
/// the framework's own key XML, unencrypted at rest; no cryptography happens here.
/// </remarks>
internal sealed class SqlXmlRepository : IXmlRepository
{
    /// <summary>What the FriendlyName column holds, and it is this table's primary key.</summary>
    private const int MaxFriendlyNameLength = 200;

    private readonly SqlDatabase _database;

    public SqlXmlRepository(SqlDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        // Blocking: IXmlRepository has no async surface, and this runs at key-ring load rather than
        // per request.
        var rows = _database.QueryAsync(
            $"SELECT Xml FROM {_database.Table("CadenceDataProtectionKey")};",
            bind: null,
            reader => reader.GetString(0),
            CancellationToken.None).GetAwaiter().GetResult();

        return [.. rows.Select(XElement.Parse)];
    }

    /// <inheritdoc />
    public void StoreElement(XElement element, string friendlyName)
    {
        ArgumentNullException.ThrowIfNull(element);

        // The framework supplies a friendly name only sometimes, and it is the primary key here, so
        // an unnamed element falls back to the key id and then to a fresh one.
        var name = string.IsNullOrWhiteSpace(friendlyName)
            ? element.Attribute("id")?.Value ?? Guid.NewGuid().ToString("N")
            : friendlyName;

        // The parameter would otherwise truncate at the column's width, and two keys truncating to
        // one name overwrite each other in the primary key -- a key ring that silently loses a key.
        // The framework's own names are "key-{guid}", so nothing reaches this.
        if (name.Length > MaxFriendlyNameLength)
        {
            throw new InvalidOperationException(
                $"A Data Protection key's name is longer than {MaxFriendlyNameLength} characters, " +
                "which is what this table's primary key holds.");
        }

        // Transacted, and the range lock is what the insert path needs: replicas boot together, and
        // two storing one name under plain read-committed can both update nothing and both insert.
        var sql = $"""
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;

            UPDATE {_database.Table("CadenceDataProtectionKey")} WITH (UPDLOCK, HOLDLOCK)
            SET Xml = @Xml
            WHERE FriendlyName = @FriendlyName;

            IF @@ROWCOUNT = 0
                INSERT INTO {_database.Table("CadenceDataProtectionKey")}
                    (FriendlyName, Xml, CreatedAtUtc)
                VALUES (@FriendlyName, @Xml, SYSUTCDATETIME());

            COMMIT TRANSACTION;
            """;

        _database.ExecuteAsync(
            sql,
            command =>
            {
                SqlValues.AddText(command, "@FriendlyName", name, 200);
                SqlValues.AddText(command, "@Xml", element.ToString(SaveOptions.DisableFormatting), -1);
            },
            CancellationToken.None).GetAwaiter().GetResult();
    }
}
