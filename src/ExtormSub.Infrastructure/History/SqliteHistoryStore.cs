using ExtormSub.Core.History;
using Microsoft.Data.Sqlite;

namespace ExtormSub.Infrastructure.History;

/// <summary>Subtitle history in a local SQLite file (WAL). Writes come from one background consumer.</summary>
public sealed class SqliteHistoryStore(string databasePath) : IHistoryStore
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared,
    }.ToString();

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        await using var c = await OpenAsync(ct);
        await ExecAsync(c, """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS sessions (
                id                TEXT PRIMARY KEY,
                started_at        TEXT NOT NULL,
                ended_at          TEXT,
                device            TEXT,
                asr_model         TEXT,
                translation_model TEXT
            );
            CREATE TABLE IF NOT EXISTS segments (
                id                     INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id             TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                seq                    INTEGER NOT NULL,
                timestamp              TEXT NOT NULL,
                start_ms               INTEGER NOT NULL,
                end_ms                 INTEGER NOT NULL,
                original_text          TEXT NOT NULL,
                translated_text        TEXT,
                detected_language      TEXT,
                confidence             REAL,
                asr_latency_ms         INTEGER,
                translation_latency_ms INTEGER
            );
            CREATE INDEX IF NOT EXISTS ix_segments_session ON segments(session_id, start_ms);
            """, ct);
    }

    public async Task StartSessionAsync(SessionRecord s, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await ExecAsync(c, """
            INSERT OR REPLACE INTO sessions(id, started_at, ended_at, device, asr_model, translation_model)
            VALUES ($id, $start, NULL, $device, $asr, $tr)
            """, ct, ("$id", s.Id), ("$start", s.StartedAt.ToString("O")), ("$device", s.Device),
            ("$asr", s.AsrModel), ("$tr", s.TranslationModel));
    }

    public async Task EndSessionAsync(string sessionId, DateTimeOffset endedAt, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await ExecAsync(c, "UPDATE sessions SET ended_at = $end WHERE id = $id", ct, ("$id", sessionId), ("$end", endedAt.ToString("O")));
        // Sessions where nothing was said are noise in the history list.
        await ExecAsync(c, "DELETE FROM sessions WHERE id = $id AND NOT EXISTS (SELECT 1 FROM segments WHERE session_id = $id)", ct, ("$id", sessionId));
    }

    public async Task AddSegmentAsync(SegmentRecord r, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await ExecAsync(c, """
            INSERT INTO segments(session_id, seq, timestamp, start_ms, end_ms, original_text, translated_text,
                                 detected_language, confidence, asr_latency_ms, translation_latency_ms)
            VALUES ($sid, $seq, $ts, $start, $end, $orig, $tr, $lang, $conf, $asr, $trl)
            """, ct,
            ("$sid", r.SessionId), ("$seq", r.Seq), ("$ts", r.Timestamp.ToString("O")),
            ("$start", (long)r.Start.TotalMilliseconds), ("$end", (long)r.End.TotalMilliseconds),
            ("$orig", r.OriginalText), ("$tr", r.TranslatedText), ("$lang", r.DetectedLanguage),
            ("$conf", r.Confidence), ("$asr", r.AsrLatencyMs), ("$trl", r.TranslationLatencyMs));
    }

    public async Task<IReadOnlyList<SessionRecord>> ListSessionsAsync(string? search = null, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT s.id, s.started_at, s.ended_at, s.device, s.asr_model, s.translation_model,
                   (SELECT COUNT(*) FROM segments g WHERE g.session_id = s.id),
                   (SELECT COALESCE(g.translated_text, g.original_text) FROM segments g WHERE g.session_id = s.id ORDER BY g.start_ms LIMIT 1)
            FROM sessions s
            WHERE $q IS NULL OR EXISTS (
                SELECT 1 FROM segments g WHERE g.session_id = s.id
                AND (g.original_text LIKE $q ESCAPE '\' OR g.translated_text LIKE $q ESCAPE '\'))
            ORDER BY s.started_at DESC
            """;
        cmd.Parameters.AddWithValue("$q", string.IsNullOrWhiteSpace(search) ? DBNull.Value : Like(search));
        var list = new List<SessionRecord>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new SessionRecord(
                r.GetString(0),
                DateTimeOffset.Parse(r.GetString(1)),
                r.IsDBNull(2) ? null : DateTimeOffset.Parse(r.GetString(2)),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5),
                r.GetInt32(6),
                r.IsDBNull(7) ? null : r.GetString(7)));
        }
        return list;
    }

    public async Task<IReadOnlyList<SegmentRecord>> GetSegmentsAsync(string sessionId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT session_id, seq, timestamp, start_ms, end_ms, original_text, translated_text,
                   detected_language, confidence, asr_latency_ms, translation_latency_ms
            FROM segments WHERE session_id = $id ORDER BY start_ms, seq
            """;
        cmd.Parameters.AddWithValue("$id", sessionId);
        var list = new List<SegmentRecord>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new SegmentRecord(
                r.GetString(0), r.GetInt64(1), DateTimeOffset.Parse(r.GetString(2)),
                TimeSpan.FromMilliseconds(r.GetInt64(3)), TimeSpan.FromMilliseconds(r.GetInt64(4)),
                r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6),
                r.IsDBNull(7) ? null : r.GetString(7),
                r.IsDBNull(8) ? null : (float)r.GetDouble(8),
                r.IsDBNull(9) ? null : r.GetInt32(9),
                r.IsDBNull(10) ? null : r.GetInt32(10)));
        }
        return list;
    }

    public async Task DeleteSessionAsync(string sessionId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await ExecAsync(c, "DELETE FROM segments WHERE session_id = $id; DELETE FROM sessions WHERE id = $id;", ct, ("$id", sessionId));
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        await ExecAsync(c, "DELETE FROM segments; DELETE FROM sessions;", ct);
        await ExecAsync(c, "VACUUM;", ct);
    }

    private static string Like(string s) =>
        "%" + s.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var c = new SqliteConnection(_connectionString);
        await c.OpenAsync(ct);
        await ExecAsync(c, "PRAGMA foreign_keys = ON;", ct);
        return c;
    }

    private static async Task ExecAsync(SqliteConnection c, string sql, CancellationToken ct, params (string Name, object? Value)[] args)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
