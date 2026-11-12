using System.Data;
using Microsoft.EntityFrameworkCore;

namespace TwilioCaller.Api.Data;

/// <summary>
/// The database is created with EnsureCreated, which does nothing once the file
/// exists — so a deployment that predates a schema change would otherwise crash on
/// its first query. This brings such a database up to the current model in place:
/// additive changes only, each one safe to run on every start.
/// </summary>
public static class SchemaUpgrader
{
    private static readonly (string Table, string Column, string Definition)[] AddedColumns =
    [
        ("Connections", "Provider", "TEXT NOT NULL DEFAULT 'twilio'"),
        ("Devices", "PlivoEndpointId", "TEXT NULL"),
        ("Devices", "PlivoEndpointUsername", "TEXT NULL"),
        ("Devices", "PlivoEndpointPasswordCipher", "TEXT NULL"),
    ];

    private static readonly string[] AddedTables = ["StoredMessages"];

    public static void Upgrade(AppDbContext db)
    {
        if (!db.Database.IsSqlite())
        {
            return;
        }

        var connection = db.Database.GetDbConnection();
        var wasClosed = connection.State != ConnectionState.Open;
        if (wasClosed)
        {
            connection.Open();
        }

        try
        {
            foreach (var (table, column, definition) in AddedColumns)
            {
                if (!Columns(connection, table).Contains(column, StringComparer.OrdinalIgnoreCase))
                {
                    Execute(connection, $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition}");
                }
            }

            foreach (var table in AddedTables)
            {
                if (Columns(connection, table).Count > 0)
                {
                    continue;
                }

                // Let EF write the DDL so the table always matches the model, and
                // run just the statements that belong to the missing table.
                var statements = db.Database.GenerateCreateScript()
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(s => s.Contains($"\"{table}\"", StringComparison.Ordinal));

                foreach (var statement in statements)
                {
                    Execute(connection, statement);
                }
            }
        }
        finally
        {
            if (wasClosed)
            {
                connection.Close();
            }
        }
    }

    private static List<string> Columns(System.Data.Common.DbConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\")";
        using var reader = command.ExecuteReader();

        var names = new List<string>();
        while (reader.Read())
        {
            names.Add(reader.GetString(1));
        }

        return names;
    }

    private static void Execute(System.Data.Common.DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
