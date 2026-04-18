using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SharePointRag.Core.Configuration;
using SharePointRag.Core.Interfaces;
using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;

namespace SharePointRag.Core.Connectors;

/// <summary>
/// Connector for any ADO.NET-compatible relational database.
/// Supports SQL Server, PostgreSQL, MySQL, and SQLite.
///
/// Required properties:
///   ConnectionString  – ADO.NET connection string
///   Query             – SELECT that returns at least Id + Content columns
///   IdColumn          – column name for the stable record ID (default "Id")
///   ContentColumn     – column name for the text to embed (default "Content")
///
/// Optional properties:
///   TitleColumn       – display name column (default "Title")
///   UrlColumn         – deep-link column   (default "Url")
///   ModifiedAtColumn  – last-modified column (default "ModifiedAt")
///   DeltaFilter       – WHERE clause appended for delta runs (default "ModifiedAt > @since")
///   Provider          – SqlServer (default) | Postgres | MySql | Sqlite
///
/// Example appsettings entry:
/// {
///   "Name": "ContractsDB",
///   "Type": "SqlDatabase",
///   "Properties": {
///     "ConnectionString": "Server=sql01;Database=Contracts;Integrated Security=true",
///     "Query": "SELECT ContractId AS Id, ContractNumber + ' ' + Description AS Content,
///                      ContractNumber AS Title, ContractUrl AS Url,
///                      ModifiedDate AS ModifiedAt FROM dbo.Contracts WHERE IsActive=1",
///     "IdColumn":       "Id",
///     "ContentColumn":  "Content",
///     "TitleColumn":    "Title",
///     "UrlColumn":      "Url",
///     "ModifiedAtColumn":"ModifiedAt",
///     "DeltaFilter":    "ModifiedDate > @since",
///     "Provider":       "SqlServer"
///   }
/// }
/// </summary>
public sealed class SqlConnector : IDataSourceConnector
{
    private readonly DataSourceDefinition _def;
    private readonly ILogger              _logger;

    // Column name defaults
    private string IdCol       => _def.Get(SqlProps.IdColumn,         "Id");
    private string ContentCol  => _def.Get(SqlProps.ContentColumn,    "Content");
    private string TitleCol    => _def.Get(SqlProps.TitleColumn,      "Title");
    private string UrlCol      => _def.Get(SqlProps.UrlColumn,        "Url");
    private string ModifiedCol => _def.Get(SqlProps.ModifiedAtColumn, "ModifiedAt");
    private string BaseQuery   => _def.Get(SqlProps.Query);
    private string DeltaFilter => _def.Get(SqlProps.DeltaFilter,      $"{ModifiedCol} > @since");

    public string DataSourceName => _def.Name;
    public DataSourceType ConnectorType => DataSourceType.SqlDatabase;

    public SqlConnector(DataSourceDefinition def, ILogger logger)
    {
        _def    = def;
        _logger = logger;
    }

    public async IAsyncEnumerable<SourceRecord> GetRecordsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var r in ExecuteQueryAsync(BaseQuery, null, ct))
            yield return r;
    }

    public async IAsyncEnumerable<SourceRecord> GetModifiedRecordsAsync(
        DateTimeOffset since,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Append delta filter to the base query
        var baseQuery = BaseQuery.TrimEnd().TrimEnd(';');
        var deltaQuery = baseQuery.Contains("WHERE", StringComparison.OrdinalIgnoreCase)
            ? $"{baseQuery} AND {DeltaFilter}"
            : $"{baseQuery} WHERE {DeltaFilter}";

        await foreach (var r in ExecuteQueryAsync(deltaQuery, since, ct))
            yield return r;
    }

    public async Task<string> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            await using var conn = CreateConnection();
            await conn.OpenAsync(ct);
            return $"Connected to {conn.Database} on {conn.DataSource}";
        }
        catch (Exception ex)
        {
            return $"Connection failed: {ex.Message}";
        }
    }

    private async IAsyncEnumerable<SourceRecord> ExecuteQueryAsync(
        string sql,
        DateTimeOffset? since,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText    = sql;
        cmd.CommandTimeout = 120;

        if (since.HasValue)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = "@since";
            p.Value         = since.Value.UtcDateTime;
            cmd.Parameters.Add(p);
        }

        _logger.LogDebug("[{Src}] Executing: {Sql}", _def.Name, sql);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var schema = GetSchemaColumns(reader);

        while (await reader.ReadAsync(ct))
        {
            var id      = reader.GetValue(schema.IdOrd).ToString() ?? Guid.NewGuid().ToString();
            var content = schema.ContentOrd >= 0
                ? reader.GetValue(schema.ContentOrd).ToString() ?? string.Empty
                : string.Empty;

            if (string.IsNullOrWhiteSpace(content)) continue;

            var title   = schema.TitleOrd   >= 0 ? reader.GetValue(schema.TitleOrd).ToString()   ?? id    : id;
            var url     = schema.UrlOrd      >= 0 ? reader.GetValue(schema.UrlOrd).ToString()     ?? ""    : "";
            var modAt   = schema.ModifiedOrd >= 0 && !reader.IsDBNull(schema.ModifiedOrd)
                ? reader.GetDateTime(schema.ModifiedOrd)
                : DateTime.UtcNow;

            // Collect all remaining columns as metadata
            var meta = new Dictionary<string, string>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                if (i == schema.IdOrd || i == schema.ContentOrd) continue;
                var colName = reader.GetName(i);
                var val     = reader.IsDBNull(i) ? "" : reader.GetValue(i).ToString() ?? "";
                meta[colName] = val;
            }

            yield return new SourceRecord
            {
                Id             = $"{_def.Name}::{id}",
                Title          = title,
                Url            = url,
                LastModified   = new DateTimeOffset(modAt, TimeSpan.Zero),
                Content        = content,
                DataSourceName = _def.Name,
                Metadata       = meta
            };
        }
    }

    private (int IdOrd, int ContentOrd, int TitleOrd, int UrlOrd, int ModifiedOrd)
        GetSchemaColumns(DbDataReader reader)
    {
        int Find(string name)
        {
            for (int i = 0; i < reader.FieldCount; i++)
                if (reader.GetName(i).Equals(name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }
        return (Find(IdCol), Find(ContentCol), Find(TitleCol), Find(UrlCol), Find(ModifiedCol));
    }

    private DbConnection CreateConnection()
    {
        var cs       = _def.Get(SqlProps.ConnectionString);
        var provider = _def.Get(SqlProps.Provider, "SqlServer").ToLowerInvariant();

        return provider switch
        {
            "postgres" or "postgresql" => CreatePostgresConnection(cs),
            "mysql"                    => CreateMySqlConnection(cs),
            "sqlite"                   => CreateSqliteConnection(cs),
            _                          => new SqlConnection(cs)   // SQL Server default
        };
    }

    // Lazy factory methods — packages are optional; throw clear errors if missing
    private static DbConnection CreatePostgresConnection(string cs)
    {
        var type = Type.GetType("Npgsql.NpgsqlConnection, Npgsql")
                   ?? throw new InvalidOperationException(
                       "Add the 'Npgsql' NuGet package for PostgreSQL support.");
        return (DbConnection)Activator.CreateInstance(type, cs)!;
    }

    private static DbConnection CreateMySqlConnection(string cs)
    {
        var type = Type.GetType("MySql.Data.MySqlClient.MySqlConnection, MySql.Data")
                   ?? Type.GetType("MySqlConnector.MySqlConnection, MySqlConnector")
                   ?? throw new InvalidOperationException(
                       "Add 'MySql.Data' or 'MySqlConnector' NuGet package for MySQL support.");
        return (DbConnection)Activator.CreateInstance(type, cs)!;
    }

    private static DbConnection CreateSqliteConnection(string cs)
    {
        var type = Type.GetType("Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite")
                   ?? throw new InvalidOperationException(
                       "Add 'Microsoft.Data.Sqlite' NuGet package for SQLite support.");
        return (DbConnection)Activator.CreateInstance(type, cs)!;
    }
}

public sealed class SqlConnectorFactory(
    ILogger<SqlConnector> logger) : IDataSourceConnectorFactory
{
    public bool CanCreate(DataSourceType type) => type == DataSourceType.SqlDatabase;
    public IDataSourceConnector Create(DataSourceDefinition def) =>
        new SqlConnector(def, logger);
}
