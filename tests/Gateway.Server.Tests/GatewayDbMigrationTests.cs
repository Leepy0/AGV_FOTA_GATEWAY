using Gateway.Server.Data;
using Gateway.Server.Services;
using Microsoft.Data.Sqlite;

namespace Gateway.Server.Tests;

public class GatewayDbMigrationTests
{
    [Fact]
    public void V1_database_is_migrated_to_v2_and_old_jobs_read_as_immediate()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gw-dbmig-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            // v1 스키마(펌웨어·작업 표만) + 예전 작업 한 건
            var cs = new SqliteConnectionStringBuilder { DataSource = Path.Combine(dir, "gateway.db"), Pooling = false }.ToString();
            using (var c = new SqliteConnection(cs))
            {
                c.Open();
                GatewayDb.Exec(c, """
                    CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
                    INSERT INTO meta VALUES('schema_version', '1');
                    CREATE TABLE firmware(
                        id TEXT PRIMARY KEY, kind TEXT NOT NULL, version TEXT NOT NULL, project TEXT NOT NULL DEFAULT '', notes TEXT NOT NULL DEFAULT '',
                        uploaded_at TEXT NOT NULL, slot TEXT NOT NULL, file_name TEXT NOT NULL, size INTEGER NOT NULL, sha256 TEXT NOT NULL);
                    CREATE TABLE jobs(
                        id TEXT PRIMARY KEY, name TEXT NOT NULL, firmware_id TEXT NOT NULL, kind TEXT NOT NULL, firmware_version TEXT NOT NULL,
                        scheduled_at TEXT, stop_on_failure INTEGER NOT NULL, state TEXT NOT NULL, created_at TEXT NOT NULL, started_at TEXT, finished_at TEXT);
                    CREATE TABLE job_targets(
                        job_id TEXT NOT NULL REFERENCES jobs(id) ON DELETE CASCADE, agv INTEGER NOT NULL, seq INTEGER NOT NULL,
                        state TEXT NOT NULL, percent INTEGER NOT NULL DEFAULT 0, message TEXT NOT NULL DEFAULT '',
                        version_before TEXT NOT NULL DEFAULT '', version_after TEXT NOT NULL DEFAULT '', started_at TEXT, finished_at TEXT,
                        log TEXT NOT NULL DEFAULT '', PRIMARY KEY(job_id, agv));
                    INSERT INTO jobs VALUES('old1', '예전 작업', 'fw1', 'Dsp', '1.0.0', NULL, 1, 'Done', '2026-10-01T00:00:00.0000000Z', NULL, '2026-10-01T00:10:00.0000000Z');
                    INSERT INTO job_targets(job_id, agv, seq, state, percent) VALUES('old1', 7, 0, 'Done', 100);
                    """);
            }

            var db = new GatewayDb(dir);
            using (var c = db.Open())
            {
                using (var cmd = GatewayDb.Cmd(c, "SELECT value FROM meta WHERE key='schema_version'")) Assert.Equal("2", cmd.ExecuteScalar());
                using (var cmd = GatewayDb.Cmd(c, "SELECT apply_mode, not_after, approved_at FROM jobs WHERE id='old1'"))
                using (var r = cmd.ExecuteReader())
                {
                    Assert.True(r.Read());
                    Assert.Equal("Immediate", r.GetString(0));
                    Assert.True(r.IsDBNull(1)); Assert.True(r.IsDBNull(2));
                }
                using (var cmd = GatewayDb.Cmd(c, "SELECT stage_job FROM job_targets WHERE job_id='old1'")) Assert.Equal(DBNull.Value, cmd.ExecuteScalar());
            }
            Assert.Equal(2, GatewayDb.SchemaVersion);
            // 두 번 열어도(이미 v2) 문제없음
            _ = new GatewayDb(dir);

            var store = new FirmwareStore(dir, db);
            var svc = new JobService(Microsoft.Extensions.Options.Options.Create(new Gateway.Server.Options.GatewayOptions { DataDir = dir }),
                                     store, new AgvRegistry(), new FakeBridges(new AgvRegistry()), db,
                                     Microsoft.Extensions.Logging.Abstractions.NullLogger<JobService>.Instance);
            var j = svc.Get("old1")!;
            Assert.Equal(ApplyMode.Immediate, j.ApplyMode);
            Assert.Equal(JobState.Done, j.State);
            Assert.Equal(0, j.Results[0].StageJob);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void New_database_starts_at_v2()
    {
        using var k = new JobTestKit();
        using var c = k.Db.Open();
        using var cmd = GatewayDb.Cmd(c, "SELECT COUNT(*) FROM pragma_table_info('jobs') WHERE name IN ('apply_mode','not_after','approved_at')");
        Assert.Equal(3L, cmd.ExecuteScalar());
    }
}
