using Microsoft.Data.Sqlite;

namespace Quizzler.Data;

/// <summary>
/// Schema migrations, tracked with SQLite's built-in <c>PRAGMA user_version</c>.
/// Append new scripts to the end; never edit one that has shipped.
/// </summary>
internal static class Migrations
{
    private static readonly string[] Scripts =
    [
        // 1: initial schema. Timestamps are UTC Unix milliseconds.
        """
        CREATE TABLE questions (
            id            TEXT PRIMARY KEY,
            topic         TEXT NOT NULL,
            type          TEXT NOT NULL,
            difficulty    INTEGER NOT NULL,
            json          TEXT NOT NULL,
            content_hash  TEXT NOT NULL,
            source_file   TEXT NOT NULL,
            retired       INTEGER NOT NULL DEFAULT 0,
            imported_at   INTEGER NOT NULL,
            updated_at    INTEGER NOT NULL
        );

        CREATE TABLE cards (
            question_id       TEXT PRIMARY KEY REFERENCES questions(id),
            stage             TEXT NOT NULL,
            repetitions       INTEGER NOT NULL,
            ease              REAL NOT NULL,
            interval_days     REAL NOT NULL,
            due_at            INTEGER NOT NULL,
            lapses            INTEGER NOT NULL,
            review_count      INTEGER NOT NULL,
            first_reviewed_at INTEGER NOT NULL,
            last_reviewed_at  INTEGER NOT NULL
        );
        CREATE INDEX ix_cards_due_at ON cards(due_at);

        CREATE TABLE reviews (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            question_id TEXT NOT NULL,
            reviewed_at INTEGER NOT NULL,
            stage       TEXT NOT NULL,
            rating      TEXT NOT NULL,
            response    TEXT,
            elapsed_ms  INTEGER NOT NULL
        );
        CREATE INDEX ix_reviews_question_id ON reviews(question_id);
        CREATE INDEX ix_reviews_reviewed_at ON reviews(reviewed_at);

        CREATE TABLE flags (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            question_id TEXT NOT NULL,
            flagged_at  INTEGER NOT NULL,
            note        TEXT,
            resolved_at INTEGER
        );

        CREATE TABLE settings (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        """,
    ];

    public static int LatestVersion => Scripts.Length;

    public static void Apply(SqliteConnection connection)
    {
        var current = Convert.ToInt32(Scalar(connection, "PRAGMA user_version;"));
        if (current > LatestVersion)
            throw new InvalidOperationException(
                $"Database schema v{current} is newer than this version of Quizzler supports (v{LatestVersion}). Update Quizzler.");

        for (var version = current + 1; version <= LatestVersion; version++)
        {
            using var tx = connection.BeginTransaction();
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = Scripts[version - 1] + $"\nPRAGMA user_version = {version};";
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }
}
