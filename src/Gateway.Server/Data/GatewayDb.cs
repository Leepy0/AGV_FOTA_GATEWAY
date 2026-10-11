using Microsoft.Data.Sqlite;

namespace Gateway.Server.Data;

/// <summary>
/// 서버 데이터베이스 — SQLite 파일 하나 ({DataDir}/gateway.db, WAL). 펌웨어 목록·배포 작업·호기 소속·최근 위치·마지막 접속을 담는다.
/// 펌웨어 파일 자체는 {DataDir}/firmware/ 에 그대로 둔다 (DB 에는 경로·해시만).
/// 각 저장소는 메모리에 캐시를 두고 바뀔 때 바로 써 넣는다(write-through) — 읽기는 DB 를 거치지 않는다.
/// 스키마 변경은 Migrate() 의 버전 단계로만 (기존 열은 지우지 않는다).
/// </summary>
public sealed class GatewayDb
{
    public const int SchemaVersion = 2;
    public string Path { get; }
    readonly string _cs;

    public GatewayDb(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        Path = System.IO.Path.Combine(dataDir, "gateway.db");
        // 잠금 대기는 5초까지 (기본 30초) — 다른 프로그램이 DB 를 쥐고 있어도 화면·서비스가 오래 멈추지 않게. 수신 루프·작업 저장은 따로 더 짧게 쓴다
        _cs = new SqliteConnectionStringBuilder { DataSource = Path, Pooling = true, DefaultTimeout = 5 }.ToString();
        using var c = Open();
        Exec(c, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;");
        Migrate(c);
    }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        Exec(c, "PRAGMA foreign_keys=ON;");
        return c;
    }

    public static void Exec(SqliteConnection c, string sql, params (string name, object? value)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public static SqliteCommand Cmd(SqliteConnection c, string sql, params (string name, object? value)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd;
    }

    public long Count(string table)
    {
        using var c = Open();
        using var cmd = Cmd(c, $"SELECT COUNT(*) FROM {table}");
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    public long FileSize => File.Exists(Path) ? new FileInfo(Path).Length + (File.Exists(Path + "-wal") ? new FileInfo(Path + "-wal").Length : 0) : 0;

    /// <summary>온라인 백업 (WAL 포함 일관된 사본) → 바이트. 화면의 "DB 백업 내려받기"</summary>
    public byte[] Backup()
    {
        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"gateway-backup-{Guid.NewGuid():N}.db");
        try
        {
            using (var src = Open())
            using (var dst = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = tmp, Pooling = false }.ToString()))
            {
                dst.Open();
                src.BackupDatabase(dst);
            }
            return File.ReadAllBytes(tmp);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    void Migrate(SqliteConnection c)
    {
        Exec(c, "CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL)");
        int ver = 0;
        using (var cmd = Cmd(c, "SELECT value FROM meta WHERE key='schema_version'"))
            if (cmd.ExecuteScalar() is string s && int.TryParse(s, out var v)) ver = v;
        if (ver < 1)
        {
            Exec(c, """
                CREATE TABLE IF NOT EXISTS firmware(
                    id TEXT PRIMARY KEY, kind TEXT NOT NULL, version TEXT NOT NULL, project TEXT NOT NULL DEFAULT '', notes TEXT NOT NULL DEFAULT '',
                    uploaded_at TEXT NOT NULL, slot TEXT NOT NULL, file_name TEXT NOT NULL, size INTEGER NOT NULL, sha256 TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS jobs(
                    id TEXT PRIMARY KEY, name TEXT NOT NULL, firmware_id TEXT NOT NULL, kind TEXT NOT NULL, firmware_version TEXT NOT NULL,
                    scheduled_at TEXT, stop_on_failure INTEGER NOT NULL, state TEXT NOT NULL, created_at TEXT NOT NULL, started_at TEXT, finished_at TEXT);
                CREATE INDEX IF NOT EXISTS ix_jobs_state ON jobs(state);
                CREATE INDEX IF NOT EXISTS ix_jobs_created ON jobs(created_at);
                CREATE TABLE IF NOT EXISTS job_targets(
                    job_id TEXT NOT NULL REFERENCES jobs(id) ON DELETE CASCADE, agv INTEGER NOT NULL, seq INTEGER NOT NULL,
                    state TEXT NOT NULL, percent INTEGER NOT NULL DEFAULT 0, message TEXT NOT NULL DEFAULT '',
                    version_before TEXT NOT NULL DEFAULT '', version_after TEXT NOT NULL DEFAULT '', started_at TEXT, finished_at TEXT,
                    log TEXT NOT NULL DEFAULT '', PRIMARY KEY(job_id, agv));
                CREATE TABLE IF NOT EXISTS agv_groups(
                    agv INTEGER PRIMARY KEY, name TEXT NOT NULL DEFAULT '', product_family TEXT NOT NULL DEFAULT '', floor TEXT NOT NULL DEFAULT '',
                    line TEXT NOT NULL DEFAULT '', process TEXT NOT NULL DEFAULT '', route TEXT NOT NULL DEFAULT '',
                    source TEXT NOT NULL, updated_at TEXT NOT NULL, note TEXT NOT NULL DEFAULT '');
                CREATE TABLE IF NOT EXISTS agv_positions(
                    agv INTEGER PRIMARY KEY, at TEXT NOT NULL, x REAL, y REAL, heading REAL, state TEXT NOT NULL, speed REAL,
                    rotating INTEGER NOT NULL DEFAULT 0, last_tag TEXT, last_tag_at TEXT, map TEXT, online INTEGER NOT NULL DEFAULT 0, note TEXT);
                CREATE TABLE IF NOT EXISTS agv_last_seen(
                    agv INTEGER PRIMARY KEY, last_seen TEXT NOT NULL, endpoint TEXT, fw TEXT, part TEXT, dsp TEXT, dsp_boot TEXT, status_json TEXT);
                """);
            Exec(c, "INSERT OR REPLACE INTO meta(key, value) VALUES('schema_version', '1')");
        }
        if (ver < 2)
        {
            // v2: 메인보드 작업의 적용 방식(승인 후 적용/바로 적용)·마감 시각·승인 시각, 대상별 스테이징 작업 번호.
            // 기존 작업 기록은 예전 동작(스테이징 직후 바로 적용)이었으므로 apply_mode 기본값은 'Immediate'
            using var tx = c.BeginTransaction();
            AddColumn(c, "jobs", "apply_mode", "TEXT NOT NULL DEFAULT 'Immediate'");
            AddColumn(c, "jobs", "not_after", "TEXT");
            AddColumn(c, "jobs", "approved_at", "TEXT");
            AddColumn(c, "job_targets", "stage_job", "INTEGER");
            Exec(c, "INSERT OR REPLACE INTO meta(key, value) VALUES('schema_version', '2')");
            tx.Commit();
        }
    }

    /// <summary>열이 없을 때만 추가 (중간에 끊긴 마이그레이션을 다시 돌려도 안전)</summary>
    static void AddColumn(SqliteConnection c, string table, string column, string definition)
    {
        using (var cmd = Cmd(c, $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name=$n", ("$n", column)))
            if ((long)(cmd.ExecuteScalar() ?? 0L) > 0) return;
        Exec(c, $"ALTER TABLE {table} ADD COLUMN {column} {definition}");
    }

    // ── 날짜·값 변환 (DB 는 ISO 8601 UTC 문자열) ──
    public static string Ts(DateTime d) => d.ToUniversalTime().ToString("o");
    public static string? Ts(DateTime? d) => d is null ? null : Ts(d.Value);
    public static string Ts(DateTimeOffset d) => d.ToUniversalTime().ToString("o");
    public static string? Ts(DateTimeOffset? d) => d is null ? null : Ts(d.Value);
    public static DateTime Dt(object? v) => v is string s ? DateTime.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime() : DateTime.MinValue;
    public static DateTime? DtN(object? v) => v is string s && s.Length > 0 ? Dt(s) : null;
    public static DateTimeOffset Dto(object? v) => v is string s ? DateTimeOffset.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind) : DateTimeOffset.MinValue;
    public static DateTimeOffset? DtoN(object? v) => v is string s && s.Length > 0 ? Dto(s) : null;
    public static string S(object? v) => v is string s ? s : "";
    public static string? SN(object? v) => v is string s ? s : null;
    public static double? DN(object? v) => v is double d ? d : v is long l ? l : null;
}
