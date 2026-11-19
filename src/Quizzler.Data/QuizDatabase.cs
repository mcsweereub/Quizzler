using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Quizzler.Core.Questions;
using Quizzler.Core.Scheduling;
using Quizzler.Core.Study;

namespace Quizzler.Data;

public sealed record ImportReport(
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Updated,
    IReadOnlyList<string> Retired,
    IReadOnlyList<string> Restored,
    int Unchanged);

public sealed record FlagEntry(long Id, string QuestionId, DateTimeOffset FlaggedAt, string? Note, DateTimeOffset? ResolvedAt);

/// <summary>
/// SQLite store for the question bank (imported from content/) and the user's progress.
/// Everything lives in one file so it can be copied between machines.
/// </summary>
public sealed class QuizDatabase : IQuizStore
{
    private static readonly JsonSerializerOptions StorageJson = new(ContentLoader.JsonOptions)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _connectionString;

    public string Path { get; }

    private QuizDatabase(string path)
    {
        Path = path;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            ForeignKeys = true,
            // No pooling: the file isn't held open between operations, so it can be copied or deleted freely.
            Pooling = false,
        }.ToString();
    }

    /// <summary>Opens (creating if needed) the database at <paramref name="path"/> and applies any pending migrations.</summary>
    public static QuizDatabase Open(string path)
    {
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var db = new QuizDatabase(path);
        using var connection = db.Connect();
        Migrations.Apply(connection);
        return db;
    }

    public static string DefaultPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Quizzler", "quizzler.db");

    // ---------------------------------------------------------------- questions

    public ImportReport ImportQuestions(IEnumerable<LoadedQuestion> questions, DateTimeOffset now)
    {
        using var connection = Connect();
        using var tx = connection.BeginTransaction();

        var existing = new Dictionary<string, (string Hash, bool Retired)>();
        using (var cmd = Command(connection, tx, "SELECT id, content_hash, retired FROM questions"))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
                existing[reader.GetString(0)] = (reader.GetString(1), reader.GetBoolean(2));
        }

        List<string> added = [], updated = [], restored = [];
        var unchanged = 0;
        var seen = new HashSet<string>();
        var nowMs = now.ToUnixTimeMilliseconds();

        foreach (var (q, sourceFile) in questions)
        {
            seen.Add(q.Id);
            var json = JsonSerializer.Serialize(q, StorageJson);
            var hash = Hash(json);

            if (!existing.TryGetValue(q.Id, out var row))
            {
                Execute(connection, tx,
                    """
                    INSERT INTO questions (id, topic, type, difficulty, json, content_hash, source_file, retired, imported_at, updated_at)
                    VALUES ($id, $topic, $type, $difficulty, $json, $hash, $source, 0, $now, $now)
                    """,
                    ("$id", q.Id), ("$topic", q.Topic), ("$type", TypeName(q.Type)), ("$difficulty", q.Difficulty),
                    ("$json", json), ("$hash", hash), ("$source", sourceFile), ("$now", nowMs));
                added.Add(q.Id);
                continue;
            }

            if (row.Hash == hash && !row.Retired)
            {
                unchanged++;
                continue;
            }

            Execute(connection, tx,
                """
                UPDATE questions SET topic = $topic, type = $type, difficulty = $difficulty, json = $json,
                    content_hash = $hash, source_file = $source, retired = 0, updated_at = $now
                WHERE id = $id
                """,
                ("$id", q.Id), ("$topic", q.Topic), ("$type", TypeName(q.Type)), ("$difficulty", q.Difficulty),
                ("$json", json), ("$hash", hash), ("$source", sourceFile), ("$now", nowMs));

            if (row.Retired) restored.Add(q.Id);
            if (row.Hash != hash) updated.Add(q.Id);
        }

        // Questions no longer in content are retired, not deleted, so progress survives if they come back.
        var retired = existing.Where(e => !e.Value.Retired && !seen.Contains(e.Key)).Select(e => e.Key).ToList();
        foreach (var id in retired)
            Execute(connection, tx, "UPDATE questions SET retired = 1, updated_at = $now WHERE id = $id", ("$id", id), ("$now", nowMs));

        tx.Commit();
        return new ImportReport(added, updated, retired, restored, unchanged);
    }

    public IReadOnlyList<Question> GetActiveQuestions()
    {
        using var connection = Connect();
        using var cmd = Command(connection, null, "SELECT json FROM questions WHERE retired = 0 ORDER BY id");
        using var reader = cmd.ExecuteReader();
        var result = new List<Question>();
        while (reader.Read())
            result.Add(JsonSerializer.Deserialize<Question>(reader.GetString(0), StorageJson)!);
        return result;
    }

    public Question? GetQuestion(string id)
    {
        using var connection = Connect();
        using var cmd = Command(connection, null, "SELECT json FROM questions WHERE id = $id", ("$id", id));
        return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<Question>(json, StorageJson) : null;
    }

    // ---------------------------------------------------------------- progress

    public IReadOnlyList<CardState> GetCards()
    {
        using var connection = Connect();
        using var cmd = Command(connection, null,
            """
            SELECT question_id, stage, repetitions, ease, interval_days, due_at, lapses, review_count,
                   first_reviewed_at, last_reviewed_at
            FROM cards
            """);
        using var reader = cmd.ExecuteReader();
        var result = new List<CardState>();
        while (reader.Read())
        {
            result.Add(new CardState
            {
                QuestionId = reader.GetString(0),
                Stage = Enum.Parse<CardStage>(reader.GetString(1)),
                Repetitions = reader.GetInt32(2),
                Ease = reader.GetDouble(3),
                IntervalDays = reader.GetDouble(4),
                DueAt = FromMs(reader.GetInt64(5)),
                Lapses = reader.GetInt32(6),
                ReviewCount = reader.GetInt32(7),
                FirstReviewedAt = FromMs(reader.GetInt64(8)),
                LastReviewedAt = FromMs(reader.GetInt64(9)),
            });
        }
        return result;
    }

    public void SaveReview(CardState card, ReviewLog log)
    {
        using var connection = Connect();
        using var tx = connection.BeginTransaction();

        Execute(connection, tx,
            """
            INSERT INTO cards (question_id, stage, repetitions, ease, interval_days, due_at, lapses, review_count,
                               first_reviewed_at, last_reviewed_at)
            VALUES ($id, $stage, $reps, $ease, $interval, $due, $lapses, $count, $first, $last)
            ON CONFLICT(question_id) DO UPDATE SET
                stage = excluded.stage, repetitions = excluded.repetitions, ease = excluded.ease,
                interval_days = excluded.interval_days, due_at = excluded.due_at, lapses = excluded.lapses,
                review_count = excluded.review_count, first_reviewed_at = excluded.first_reviewed_at,
                last_reviewed_at = excluded.last_reviewed_at
            """,
            ("$id", card.QuestionId), ("$stage", card.Stage.ToString()), ("$reps", card.Repetitions),
            ("$ease", card.Ease), ("$interval", card.IntervalDays), ("$due", card.DueAt.ToUnixTimeMilliseconds()),
            ("$lapses", card.Lapses), ("$count", card.ReviewCount),
            ("$first", card.FirstReviewedAt.ToUnixTimeMilliseconds()), ("$last", card.LastReviewedAt.ToUnixTimeMilliseconds()));

        Execute(connection, tx,
            """
            INSERT INTO reviews (question_id, reviewed_at, stage, rating, response, elapsed_ms)
            VALUES ($id, $at, $stage, $rating, $response, $elapsed)
            """,
            ("$id", log.QuestionId), ("$at", log.ReviewedAt.ToUnixTimeMilliseconds()), ("$stage", log.Stage.ToString()),
            ("$rating", log.Rating.ToString()), ("$response", log.Response), ("$elapsed", (long)log.Elapsed.TotalMilliseconds));

        tx.Commit();
    }

    public IReadOnlyList<ReviewLog> GetReviewsSince(DateTimeOffset since)
    {
        using var connection = Connect();
        using var cmd = Command(connection, null,
            """
            SELECT question_id, reviewed_at, stage, rating, response, elapsed_ms
            FROM reviews WHERE reviewed_at >= $since ORDER BY reviewed_at
            """,
            ("$since", since.ToUnixTimeMilliseconds()));
        using var reader = cmd.ExecuteReader();
        var result = new List<ReviewLog>();
        while (reader.Read())
        {
            result.Add(new ReviewLog(
                reader.GetString(0),
                FromMs(reader.GetInt64(1)),
                Enum.Parse<CardStage>(reader.GetString(2)),
                Enum.Parse<Rating>(reader.GetString(3)),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                TimeSpan.FromMilliseconds(reader.GetInt64(5))));
        }
        return result;
    }

    // ---------------------------------------------------------------- flags

    public void FlagQuestion(string questionId, string? note, DateTimeOffset now)
    {
        using var connection = Connect();
        Execute(connection, null,
            "INSERT INTO flags (question_id, flagged_at, note) VALUES ($id, $at, $note)",
            ("$id", questionId), ("$at", now.ToUnixTimeMilliseconds()), ("$note", string.IsNullOrWhiteSpace(note) ? null : note.Trim()));
    }

    public IReadOnlyList<FlagEntry> GetFlags(bool includeResolved = false)
    {
        using var connection = Connect();
        using var cmd = Command(connection, null,
            "SELECT id, question_id, flagged_at, note, resolved_at FROM flags" +
            (includeResolved ? "" : " WHERE resolved_at IS NULL") + " ORDER BY flagged_at");
        using var reader = cmd.ExecuteReader();
        var result = new List<FlagEntry>();
        while (reader.Read())
        {
            result.Add(new FlagEntry(
                reader.GetInt64(0),
                reader.GetString(1),
                FromMs(reader.GetInt64(2)),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : FromMs(reader.GetInt64(4))));
        }
        return result;
    }

    /// <summary>Marks every open flag on the question resolved. Returns how many were resolved.</summary>
    public int ResolveFlags(string questionId, DateTimeOffset now)
    {
        using var connection = Connect();
        return Execute(connection, null,
            "UPDATE flags SET resolved_at = $now WHERE question_id = $id AND resolved_at IS NULL",
            ("$id", questionId), ("$now", now.ToUnixTimeMilliseconds()));
    }

    // ---------------------------------------------------------------- settings

    public StudySettings GetStudySettings()
    {
        var defaults = new StudySettings();
        return new StudySettings
        {
            NewPerDay = GetIntSetting("new_per_day") ?? defaults.NewPerDay,
            DayRolloverHour = GetIntSetting("day_rollover_hour") ?? defaults.DayRolloverHour,
        };
    }

    public void SaveStudySettings(StudySettings settings)
    {
        if (settings.NewPerDay < 0) throw new ArgumentOutOfRangeException(nameof(settings), "NewPerDay must be >= 0.");
        if (settings.DayRolloverHour is < 0 or > 23) throw new ArgumentOutOfRangeException(nameof(settings), "DayRolloverHour must be 0-23.");

        using var connection = Connect();
        using var tx = connection.BeginTransaction();
        SetSetting(connection, tx, "new_per_day", settings.NewPerDay.ToString(CultureInfo.InvariantCulture));
        SetSetting(connection, tx, "day_rollover_hour", settings.DayRolloverHour.ToString(CultureInfo.InvariantCulture));
        tx.Commit();
    }

    public NotificationSettings GetNotificationSettings()
    {
        var defaults = new NotificationSettings();
        return new NotificationSettings
        {
            Enabled = GetSetting("notifications_enabled") is { } e ? e == "1" : defaults.Enabled,
            ReminderIntervalMinutes = GetIntSetting("reminder_interval_minutes") ?? defaults.ReminderIntervalMinutes,
            QuietStartHour = GetIntSetting("quiet_start_hour") ?? defaults.QuietStartHour,
            QuietEndHour = GetIntSetting("quiet_end_hour") ?? defaults.QuietEndHour,
        };
    }

    public void SaveNotificationSettings(NotificationSettings settings)
    {
        if (settings.ReminderIntervalMinutes < 1) throw new ArgumentOutOfRangeException(nameof(settings), "ReminderIntervalMinutes must be >= 1.");
        if (settings.QuietStartHour is < 0 or > 23 || settings.QuietEndHour is < 0 or > 23)
            throw new ArgumentOutOfRangeException(nameof(settings), "Quiet hours must be 0-23.");

        using var connection = Connect();
        using var tx = connection.BeginTransaction();
        SetSetting(connection, tx, "notifications_enabled", settings.Enabled ? "1" : "0");
        SetSetting(connection, tx, "reminder_interval_minutes", settings.ReminderIntervalMinutes.ToString(CultureInfo.InvariantCulture));
        SetSetting(connection, tx, "quiet_start_hour", settings.QuietStartHour.ToString(CultureInfo.InvariantCulture));
        SetSetting(connection, tx, "quiet_end_hour", settings.QuietEndHour.ToString(CultureInfo.InvariantCulture));
        tx.Commit();
    }

    /// <summary>Raw key/value access for app-level flags that don't warrant their own settings record.</summary>
    public string? GetSetting(string key)
    {
        using var connection = Connect();
        using var cmd = Command(connection, null, "SELECT value FROM settings WHERE key = $key", ("$key", key));
        return cmd.ExecuteScalar() as string;
    }

    public void SetSetting(string key, string value)
    {
        using var connection = Connect();
        SetSetting(connection, null, key, value);
    }

    private int? GetIntSetting(string key) =>
        GetSetting(key) is { } s && int.TryParse(s, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static void SetSetting(SqliteConnection connection, SqliteTransaction? tx, string key, string value) =>
        Execute(connection, tx,
            "INSERT INTO settings (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            ("$key", key), ("$value", value));

    // ---------------------------------------------------------------- helpers

    private SqliteConnection Connect()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] parameters)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private static int Execute(SqliteConnection connection, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = Command(connection, tx, sql, parameters);
        return cmd.ExecuteNonQuery();
    }

    private static DateTimeOffset FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);

    private static string TypeName(QuestionType type) => JsonNamingPolicy.KebabCaseLower.ConvertName(type.ToString());

    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
}
