using AutoVJ.Models;
using Microsoft.Data.Sqlite;

namespace AutoVJ.Services;

public sealed class DatabaseService(AppConfig config)
{
    private string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = Path.GetFullPath(config.Storage.DatabasePath)
    }.ToString();

    // データ保存先を作成し、M0で必要なテーブルを初期化します。
    public async Task InitializeAsync()
    {
        var databasePath = Path.GetFullPath(config.Storage.DatabasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS tracks (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                audio_path TEXT NOT NULL UNIQUE,
                video_path TEXT NOT NULL,
                duration_seconds REAL NOT NULL,
                fingerprint BLOB NOT NULL,
                updated_at TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync();
    }

    // 素材再解析結果で登録曲をトランザクション内にて置き換えます。
    public async Task ReplaceTracksAsync(IEnumerable<TrackRecord> tracks)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

        var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM tracks;";
        await delete.ExecuteNonQueryAsync();

        foreach (var track in tracks)
        {
            var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO tracks (name, audio_path, video_path, duration_seconds, fingerprint, updated_at)
                VALUES ($name, $audio, $video, $duration, $fingerprint, $updated);
                """;
            insert.Parameters.AddWithValue("$name", track.Name);
            insert.Parameters.AddWithValue("$audio", track.AudioPath);
            insert.Parameters.AddWithValue("$video", track.VideoPath);
            insert.Parameters.AddWithValue("$duration", track.DurationSeconds);
            insert.Parameters.Add("$fingerprint", SqliteType.Blob).Value = track.Fingerprint;
            insert.Parameters.AddWithValue("$updated", DateTimeOffset.Now.ToString("O"));
            await insert.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    // DBに保存された全登録曲と指紋データを読み込みます。
    public async Task<List<TrackRecord>> GetTracksAsync()
    {
        var tracks = new List<TrackRecord>();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, audio_path, video_path, duration_seconds, fingerprint FROM tracks ORDER BY id;";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tracks.Add(new TrackRecord(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetDouble(4),
                (byte[])reader[5]));
        }
        return tracks;
    }
}
