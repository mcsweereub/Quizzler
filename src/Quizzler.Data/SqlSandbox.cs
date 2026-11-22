using System.Globalization;
using Microsoft.Data.Sqlite;
using Quizzler.Core.Questions;

namespace Quizzler.Data;

/// <summary>
/// A small table of display values (NULL shown as "NULL"), either a setup table or a query result.
/// <see cref="Indexes"/> lists a setup table's primary key and indexes, e.g. "CREATE INDEX IX_Orders_CustomerId ON Orders (CustomerId)".
/// </summary>
public sealed record SqlTable(string Name, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows, IReadOnlyList<string>? Indexes = null)
{
    public IReadOnlyList<string> IndexList => Indexes ?? [];

    /// <summary>The rows in answer format: one per line, values separated by ", ".</summary>
    public string RowsText => Rows.Count == 0
        ? SqlSandbox.NoRows
        : string.Join('\n', Rows.Select(r => string.Join(", ", r)));
}

/// <summary>
/// The outcome of running a predict-result question. <see cref="RowMatters"/> (when analysed) says, per setup table and
/// row, whether deleting that row would change the query's result: the rows that "take part" in the answer.
/// </summary>
public sealed record SqlRun(
    IReadOnlyList<SqlTable> Tables,
    SqlTable Result,
    IReadOnlyList<IReadOnlyList<bool>>? RowMatters,
    IReadOnlyList<PlanStep> Plan)
{
    /// <summary>The correct answer for this question, worked out from the real result or plan.</summary>
    public string AnswerFor(Question question) => question.EffectiveExpect switch
    {
        ResultExpectation.RowCount => Result.Rows.Count.ToString(CultureInfo.InvariantCulture),
        ResultExpectation.TableAccess => AccessFor(question.PlanTable).Access!,
        _ => Result.RowsText,
    };

    /// <summary>The plan step that reads <paramref name="planTable"/> (or the only table read, when null).</summary>
    public PlanStep AccessFor(string? planTable)
    {
        var reads = Plan.Where(s => s.Table is not null).ToList();
        var matches = planTable is null ? reads : reads.Where(s => string.Equals(s.Table, planTable, StringComparison.OrdinalIgnoreCase)).ToList();
        var names = string.Join(", ", reads.Select(s => s.Table));
        if (matches.Count != 1)
        {
            throw new SqlRunException(planTable is null
                ? $"The plan reads {reads.Count} tables ({names}); set \"planTable\" to the one the question is about."
                : $"The plan has {matches.Count} reads of '{planTable}' (it reads: {names}). Use the name as it appears in the plan (the alias if there is one).");
        }
        return matches[0].Access is null
            ? throw new SqlRunException($"Can't classify this plan step as one of the table-access answers: {matches[0].Detail}")
            : matches[0];
    }
}

/// <summary>
/// One line of SQLite's EXPLAIN QUERY PLAN. For lines that read a table, <see cref="Table"/> is the name used in the
/// plan (the alias, if any) and <see cref="Access"/> is the matching <see cref="TableAccessNames"/> term.
/// </summary>
public sealed partial record PlanStep(int Depth, string Detail)
{
    public string? Table => Parse() is { Success: true } m ? m.Groups["table"].Value : null;

    public string? Access
    {
        get
        {
            if (Parse() is not { Success: true } m) return null;
            var search = m.Groups["op"].Value == "SEARCH";
            return m.Groups["using"].Value switch
            {
                "" => search ? null : TableAccessNames.FullTableScan,
                "INDEX" => search ? TableAccessNames.IndexSeek : TableAccessNames.FullIndexScan,
                "COVERING INDEX" => search ? TableAccessNames.CoveringIndexSeek : TableAccessNames.CoveringIndexScan,
                "INTEGER PRIMARY KEY" or "PRIMARY KEY" when search => TableAccessNames.PrimaryKeySeek,
                _ => null,
            };
        }
    }

    // e.g. "SCAN Orders", "SEARCH o USING COVERING INDEX IX_Orders_CustomerId (CustomerId=?)",
    // "SEARCH Orders USING INTEGER PRIMARY KEY (rowid=?)". "USING AUTOMATIC ..." deliberately doesn't match.
    [System.Text.RegularExpressions.GeneratedRegex(@"^(?<op>SCAN|SEARCH) (?<table>\S+)(?: USING (?<using>COVERING INDEX|INDEX|INTEGER PRIMARY KEY|PRIMARY KEY)\b.*)?$")]
    private static partial System.Text.RegularExpressions.Regex Pattern();

    private System.Text.RegularExpressions.Match? Parse() => Pattern().Match(Detail) is { Success: true } m ? m : null;
}

public sealed class SqlRunException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The outcome of a SQL write-code exercise: the user's result against the reference query's result on the same tables.
/// Rows must match exactly (values, column count, and row order when the reference query has ORDER BY); column names don't matter.
/// </summary>
public sealed record SqlExerciseResult(IReadOnlyList<SqlTable> Tables, SqlTable Expected, SqlTable? Actual, string? Error, bool Ordered)
{
    public bool Passed => Mismatch is null;

    /// <summary>Why the user's query doesn't pass, or null when it does.</summary>
    public string? Mismatch
    {
        get
        {
            if (Error is not null) return Error;
            if (Actual is null) return "No result.";
            if (Actual.Columns.Count != Expected.Columns.Count)
                return $"Your query returns {Plural(Actual.Columns.Count, "column")}; the expected result has {Expected.Columns.Count}.";
            if (Actual.Rows.Count != Expected.Rows.Count)
                return $"Your query returns {Plural(Actual.Rows.Count, "row")}; the expected result has {Expected.Rows.Count}.";

            var actualRows = Actual.Rows.Select(Key).ToList();
            var expectedRows = Expected.Rows.Select(Key).ToList();
            if (actualRows.SequenceEqual(expectedRows)) return null;
            if (actualRows.Order(StringComparer.Ordinal).SequenceEqual(expectedRows.Order(StringComparer.Ordinal)))
                return Ordered ? "Right rows, wrong order: check the ORDER BY the exercise asks for." : null;
            return "Some values differ from the expected result.";
        }
    }

    // Exact values: the user's query has to produce the data, so 'ada' vs 'Ada' is a real difference.
    private static string Key(IReadOnlyList<string> row) => string.Join('\u001F', row);

    private static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";
}

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
            tables.Add(table with { Indexes = ReadIndexes(connection, name) });
            rowIds.Add(ids);
        }

        var result = Query(connection, query, "Result");
        var plan = ReadPlan(connection, query);

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

        return new SqlRun(tables, result, matters, plan);
    }

    /// <summary>
    /// Runs a SQL exercise: <paramref name="userQuery"/> and <paramref name="referenceQuery"/> against fresh copies of
    /// <paramref name="setup"/>. The user's query is stopped after <paramref name="timeout"/> (default 5 s), so a runaway
    /// recursive CTE can't hang the app. Throws <see cref="SqlRunException"/> only if the setup or reference query fails.
    /// </summary>
    public static SqlExerciseResult RunExercise(string setup, string userQuery, string referenceQuery, TimeSpan? timeout = null)
    {
        var reference = Run(setup, referenceQuery);
        var ordered = HasTopLevelOrderBy(referenceQuery);

        if (string.IsNullOrWhiteSpace(userQuery))
            return new SqlExerciseResult(reference.Tables, reference.Result, null, "Write a query first.", ordered);

        using var connection = new SqliteConnection("Data Source=:memory:;Pooling=False");
        connection.Open();
        Execute(connection, setup, "Setup");

        var limit = timeout ?? TimeSpan.FromSeconds(5);
        var timedOut = false;
        using var timer = new Timer(_ =>
        {
            timedOut = true;
            SQLitePCL.raw.sqlite3_interrupt(connection.Handle);
        }, null, limit, Timeout.InfiniteTimeSpan);

        try
        {
            var actual = Query(connection, userQuery, "Your result");
            return new SqlExerciseResult(reference.Tables, reference.Result, actual, null, ordered);
        }
        catch (SqlRunException ex)
        {
            var message = timedOut
                ? $"Your query was stopped after {limit.TotalSeconds:0} seconds (an endless recursive CTE?)."
                : ex.Message.Replace("Query failed: ", "SQL error: ");
            return new SqlExerciseResult(reference.Tables, reference.Result, null, message, ordered);
        }
    }

    /// <summary>True when the query's outermost SELECT has an ORDER BY (one inside parentheses, e.g. a window, doesn't count).</summary>
    public static bool HasTopLevelOrderBy(string query)
    {
        var depth = 0;
        var outer = new System.Text.StringBuilder();
        var inString = false;
        foreach (var c in query)
        {
            if (c == '\'') inString = !inString;
            if (inString) continue;
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (depth == 0) outer.Append(c);
        }
        return System.Text.RegularExpressions.Regex.IsMatch(outer.ToString(), @"\bORDER\s+BY\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static List<PlanStep> ReadPlan(SqliteConnection connection, string query)
    {
        var rows = new List<(int Id, int Parent, string Detail)>();
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "EXPLAIN QUERY PLAN " + query;
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) rows.Add((reader.GetInt32(0), reader.GetInt32(1), reader.GetString(3)));
        }
        catch (SqliteException ex)
        {
            throw new SqlRunException($"Query plan failed: {ex.Message}", ex);
        }

        var depth = new Dictionary<int, int>();
        return rows.Select(r =>
        {
            var d = depth.TryGetValue(r.Parent, out var parentDepth) ? parentDepth + 1 : 0;
            depth[r.Id] = d;
            return new PlanStep(d, r.Detail);
        }).ToList();
    }

    private static List<string> ReadIndexes(SqliteConnection connection, string table)
    {
        var result = new List<string>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"SELECT name FROM pragma_table_info({QuoteLiteral(table)}) WHERE pk > 0 ORDER BY pk";
            using var reader = cmd.ExecuteReader();
            var pk = new List<string>();
            while (reader.Read()) pk.Add(reader.GetString(0));
            if (pk.Count > 0) result.Add($"PRIMARY KEY ({string.Join(", ", pk)})");
        }
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT sql FROM sqlite_schema WHERE type = 'index' AND sql IS NOT NULL AND tbl_name = $t ORDER BY rowid";
            cmd.Parameters.AddWithValue("$t", table);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) result.Add(System.Text.RegularExpressions.Regex.Replace(reader.GetString(0), @"\s+", " ").Trim());
        }
        return result;
    }

    private static string QuoteLiteral(string s) => "'" + s.Replace("'", "''") + "'";

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
