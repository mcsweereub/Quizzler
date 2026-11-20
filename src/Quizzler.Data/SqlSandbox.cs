using System.Globalization;
using Microsoft.Data.Sqlite;
using Quizzler.Core.Questions;

namespace Quizzler.Data;

/// <summary>A small table of display values (NULL shown as "NULL"), either a setup table or a query result.</summary>
public sealed record SqlTable(string Name, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows)
{
    /// <summary>The rows in answer format: one per line, values separated by ", ".</summary>
    public string RowsText => Rows.Count == 0
        ? SqlSandbox.NoRows
        : string.Join('\n', Rows.Select(r => string.Join(", ", r)));
}

/// <summary>
/// The outcome of running a predict-result question. <see cref="RowMatters"/> (when analysed) says, per setup table and
/// row, whether deleting that row would change the query's result: the rows that "take part" in the answer.
/// </summary>
public sealed record SqlRun(IReadOnlyList<SqlTable> Tables, SqlTable Result, IReadOnlyList<IReadOnlyList<bool>>? RowMatters)
{
    public string AnswerFor(ResultExpectation expect) =>
        expect == ResultExpectation.RowCount ? Result.Rows.Count.ToString(CultureInfo.InvariantCulture) : Result.RowsText;
}

public sealed class SqlRunException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Runs a question's setup + query in a throwaway in-memory SQLite database. Nothing touches disk.
/// </summary>
public static class SqlSandbox
{
    public const string NoRows = "(no rows)";
    public const string Null = "NULL";

    public static SqlRun Run(Question question, bool analyseRows = false) =>
        Run(question.Setup ?? "", question.Code ?? "", analyseRows);

    public static SqlRun Run(string setup, string query, bool analyseRows = false)
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Pooling=False");
        connection.Open();

        Execute(connection, setup, "Setup");

        var tableNames = new List<string>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_schema WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY rowid";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) tableNames.Add(reader.GetString(0));
        }

        var tables = new List<SqlTable>();
        var rowIds = new List<List<long>>();
        foreach (var name in tableNames)
        {
            var (table, ids) = ReadTable(connection, name);
            tables.Add(table);
            rowIds.Add(ids);
        }

        var result = Query(connection, query, "Result");

        List<IReadOnlyList<bool>>? matters = null;
        if (analyseRows)
        {
            var baseline = Fingerprint(result);
            matters = [];
            for (var t = 0; t < tables.Count; t++)
            {
                var flags = new List<bool>();
                foreach (var rowId in rowIds[t])
                {
                    Execute(connection, "SAVEPOINT probe", "Analysis");
                    Execute(connection, $"DELETE FROM {Quote(tables[t].Name)} WHERE rowid = {rowId}", "Analysis");
                    flags.Add(Fingerprint(Query(connection, query, "Result")) != baseline);
                    Execute(connection, "ROLLBACK TO probe; RELEASE probe", "Analysis");
                }
                matters.Add(flags);
            }
        }

        return new SqlRun(tables, result, matters);
    }

    /// <summary>Order-insensitive, so a query without ORDER BY can't look "changed" just because rows moved.</summary>
    private static string Fingerprint(SqlTable t) => string.Join('\n', t.Rows.Select(r => string.Join('\u001F', r)).Order(StringComparer.Ordinal));

    private static (SqlTable Table, List<long> RowIds) ReadTable(SqliteConnection connection, string name)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT rowid, * FROM {Quote(name)} ORDER BY rowid";
        using var reader = cmd.ExecuteReader();
        var columns = Enumerable.Range(1, reader.FieldCount - 1).Select(reader.GetName).ToList();
        var rows = new List<IReadOnlyList<string>>();
        var ids = new List<long>();
        while (reader.Read())
        {
            ids.Add(reader.GetInt64(0));
            rows.Add(Enumerable.Range(1, reader.FieldCount - 1).Select(i => Format(reader.GetValue(i))).ToList());
        }
        return (new SqlTable(name, columns, rows), ids);
    }

    private static SqlTable Query(SqliteConnection connection, string sql, string name)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            using var reader = cmd.ExecuteReader();
            if (reader.FieldCount == 0) throw new SqlRunException("The query doesn't return any columns (is it a SELECT?).");
            var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
            var rows = new List<IReadOnlyList<string>>();
            while (reader.Read())
                rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => Format(reader.GetValue(i))).ToList());
            return new SqlTable(name, columns, rows);
        }
        catch (SqliteException ex)
        {
            throw new SqlRunException($"Query failed: {ex.Message}", ex);
        }
    }

    private static void Execute(SqliteConnection connection, string sql, string stage)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            throw new SqlRunException($"{stage} failed: {ex.Message}", ex);
        }
    }

    private static string Format(object value) => value switch
    {
        DBNull => Null,
        double d => d.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
}
