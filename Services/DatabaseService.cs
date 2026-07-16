using AutoVJ.Models;
using Microsoft.Data.Sqlite;

namespace AutoVJ.Services;

public sealed class DatabaseService(AppConfig config)
{
    private string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = Path.GetFullPath(config.Storage.DatabasePath),
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared,
        DefaultTimeout = 5
    }.ToString();

    // DBを開き、別プロセスの短い書き込みを待てる接続にします。
    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 5000;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    // データ保存先を作成し、再生アプリと解析アプリが共有するスキーマを初期化します。
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var databasePath = Path.GetFullPath(config.Storage.DatabasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var connection = await OpenConnectionAsync(cancellationToken);

        var journalCommand = connection.CreateCommand();
        journalCommand.CommandText = "PRAGMA journal_mode = WAL;";
        await journalCommand.ExecuteScalarAsync(cancellationToken);

        var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(await versionCommand.ExecuteScalarAsync(cancellationToken));
        if (version < 2)
        {
            var reset = connection.CreateCommand();
            reset.CommandText = "DROP TABLE IF EXISTS tracks;";
            await reset.ExecuteNonQueryAsync(cancellationToken);
            version = 0;
        }

        var create = connection.CreateCommand();
        create.CommandText = """
            CREATE TABLE IF NOT EXISTS tracks (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                video_path TEXT NOT NULL UNIQUE,
                duration_seconds REAL NOT NULL,
                fingerprint BLOB NOT NULL,
                file_size INTEGER NOT NULL DEFAULT 0,
                file_modified_utc TEXT NOT NULL DEFAULT '',
                fingerprint_method TEXT NOT NULL DEFAULT 'band-peaks',
                fingerprint_version INTEGER NOT NULL DEFAULT 1,
                tempo_fingerprint BLOB,
                bpm REAL,
                bpm_confidence REAL NOT NULL DEFAULT 0,
                bpm_source TEXT NOT NULL DEFAULT 'unknown',
                updated_at TEXT NOT NULL
            );
            """;
        await create.ExecuteNonQueryAsync(cancellationToken);

        if (version == 2)
        {
            var migrate = connection.CreateCommand();
            migrate.CommandText = """
                ALTER TABLE tracks ADD COLUMN file_size INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE tracks ADD COLUMN file_modified_utc TEXT NOT NULL DEFAULT '';
                """;
            await migrate.ExecuteNonQueryAsync(cancellationToken);
        }

        if (version < 4)
        {
            // 旧指紋を残したままM0.5用列だけを追加し、再解析まで互換動作させます。
            var columns = await GetTrackColumnsAsync(connection, cancellationToken);
            var additions = new Dictionary<string, string>
            {
                ["fingerprint_method"] = "TEXT NOT NULL DEFAULT 'band-peaks'",
                ["fingerprint_version"] = "INTEGER NOT NULL DEFAULT 1",
                ["tempo_fingerprint"] = "BLOB",
                ["bpm"] = "REAL",
                ["bpm_confidence"] = "REAL NOT NULL DEFAULT 0",
                ["bpm_source"] = "TEXT NOT NULL DEFAULT 'unknown'"
            };
            foreach (var addition in additions.Where(item => !columns.Contains(item.Key)))
            {
                var migrate = connection.CreateCommand();
                migrate.CommandText = $"ALTER TABLE tracks ADD COLUMN {addition.Key} {addition.Value};";
                await migrate.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        var setVersion = connection.CreateCommand();
        setVersion.CommandText = "PRAGMA user_version = 4;";
        await setVersion.ExecuteNonQueryAsync(cancellationToken);
    }

    // 既存DBへ不足列だけを安全に追加するため、tracks列名を取得します。
    private static async Task<HashSet<string>> GetTrackColumnsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(tracks);";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(reader.GetString(1));
        }
        return columns;
    }

    // 解析済み動画をパス単位で追加または更新し、既存IDを維持します。
    public async Task UpsertTrackAsync(TrackRecord track, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO tracks (
                name, video_path, duration_seconds, fingerprint,
                file_size, file_modified_utc, fingerprint_method, fingerprint_version,
                tempo_fingerprint, bpm, bpm_confidence, bpm_source, updated_at)
            VALUES (
                $name, $video, $duration, $fingerprint,
                $fileSize, $fileModifiedUtc, $fingerprintMethod, $fingerprintVersion,
                $tempoFingerprint, $bpm, $bpmConfidence, $bpmSource, $updated)
            ON CONFLICT(video_path) DO UPDATE SET
                name = excluded.name,
                duration_seconds = excluded.duration_seconds,
                fingerprint = excluded.fingerprint,
                file_size = excluded.file_size,
                file_modified_utc = excluded.file_modified_utc,
                fingerprint_method = excluded.fingerprint_method,
                fingerprint_version = excluded.fingerprint_version,
                tempo_fingerprint = excluded.tempo_fingerprint,
                bpm = excluded.bpm,
                bpm_confidence = excluded.bpm_confidence,
                bpm_source = excluded.bpm_source,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$name", track.Name);
        command.Parameters.AddWithValue("$video", track.VideoPath);
        command.Parameters.AddWithValue("$duration", track.DurationSeconds);
        command.Parameters.Add("$fingerprint", SqliteType.Blob).Value = track.Fingerprint;
        command.Parameters.AddWithValue("$fileSize", track.FileSize);
        command.Parameters.AddWithValue("$fileModifiedUtc", track.FileModifiedUtc);
        command.Parameters.AddWithValue("$fingerprintMethod", track.FingerprintMethod);
        command.Parameters.AddWithValue("$fingerprintVersion", track.FingerprintVersion);
        command.Parameters.Add("$tempoFingerprint", SqliteType.Blob).Value = (object?)track.TempoFingerprint ?? DBNull.Value;
        command.Parameters.AddWithValue("$bpm", (object?)track.Bpm ?? DBNull.Value);
        command.Parameters.AddWithValue("$bpmConfidence", track.BpmConfidence);
        command.Parameters.AddWithValue("$bpmSource", track.BpmSource);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.Now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    // 素材フォルダから削除された動画だけをDBから取り除きます。
    public async Task<int> DeleteTracksNotInAsync(IReadOnlyCollection<string> videoPaths, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        if (videoPaths.Count == 0)
        {
            command.CommandText = "DELETE FROM tracks;";
        }
        else
        {
            var parameterNames = new List<string>();
            var index = 0;
            foreach (var path in videoPaths)
            {
                var parameterName = $"$path{index++}";
                parameterNames.Add(parameterName);
                command.Parameters.AddWithValue(parameterName, path);
            }
            command.CommandText = $"DELETE FROM tracks WHERE video_path NOT IN ({string.Join(", ", parameterNames)});";
        }
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // 差分解析の判定に必要な軽量メタデータだけを読み込みます。
    public async Task<List<CatalogEntry>> GetCatalogEntriesAsync(CancellationToken cancellationToken = default)
    {
        var entries = new List<CatalogEntry>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT id, video_path, file_size, file_modified_utc, fingerprint_method, fingerprint_version FROM tracks ORDER BY id;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(new CatalogEntry(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3), reader.GetString(4), reader.GetInt32(5)));
        }
        return entries;
    }

    // Web UI表示用に指紋BLOBを含まない動画一覧を読み込みます。
    public async Task<List<TrackSummary>> GetTrackSummariesAsync(CancellationToken cancellationToken = default)
    {
        var tracks = new List<TrackSummary>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, video_path, duration_seconds, bpm, bpm_confidence, bpm_source, fingerprint_method, fingerprint_version FROM tracks ORDER BY id;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tracks.Add(new TrackSummary(
                reader.GetInt64(0), reader.GetString(1), Path.GetFileName(reader.GetString(2)), reader.GetDouble(3),
                reader.IsDBNull(4) ? null : reader.GetDouble(4), reader.GetDouble(5), reader.GetString(6), reader.GetString(7), reader.GetInt32(8)));
        }
        return tracks;
    }

    // DBに保存された全登録曲と指紋データを照合用に読み込みます。
    public async Task<List<TrackRecord>> GetTracksAsync(CancellationToken cancellationToken = default)
    {
        var tracks = new List<TrackRecord>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, video_path, duration_seconds, fingerprint,
                   file_size, file_modified_utc, fingerprint_method, fingerprint_version,
                   tempo_fingerprint, bpm, bpm_confidence, bpm_source
            FROM tracks ORDER BY id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tracks.Add(new TrackRecord(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetDouble(3),
                (byte[])reader[4],
                reader.GetInt64(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetInt32(8),
                reader.IsDBNull(9) ? null : (byte[])reader[9],
                reader.IsDBNull(10) ? null : reader.GetDouble(10),
                reader.GetDouble(11),
                reader.GetString(12)));
        }
        return tracks;
    }
}
