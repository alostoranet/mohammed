using System.IO;
using Microsoft.Data.Sqlite;

namespace Alostora.RestHouse;

public sealed record WatchProgressRequest(string ViewerId, string VideoId, double PositionSeconds, double DurationSeconds);
public sealed record WatchProgressItem(string VideoId, double PositionSeconds, double DurationSeconds, DateTime UpdatedUtc);

public sealed class WatchProgressService
{
    private readonly string _connectionString;

    public WatchProgressService(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, DefaultTimeout = 10, Pooling = true }.ToString();
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA busy_timeout=10000;
            CREATE TABLE IF NOT EXISTS WatchProgress (
                ViewerId TEXT NOT NULL,
                VideoId TEXT NOT NULL,
                PositionSeconds REAL NOT NULL,
                DurationSeconds REAL NOT NULL,
                UpdatedUtc TEXT NOT NULL,
                PRIMARY KEY (ViewerId, VideoId)
            );
            CREATE INDEX IF NOT EXISTS IX_WatchProgress_UpdatedUtc ON WatchProgress(UpdatedUtc);
            """;
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<WatchProgressItem> Get(string viewerId)
    {
        if (string.IsNullOrWhiteSpace(viewerId)) return [];
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT VideoId, PositionSeconds, DurationSeconds, UpdatedUtc FROM WatchProgress WHERE ViewerId = $viewer ORDER BY UpdatedUtc DESC";
        command.Parameters.AddWithValue("$viewer", viewerId.Trim());
        using var reader = command.ExecuteReader();
        var items = new List<WatchProgressItem>();
        while (reader.Read())
            items.Add(new(reader.GetString(0), reader.GetDouble(1), reader.GetDouble(2),
                DateTime.Parse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        return items;
    }

    public void MoveVideoProgress(string oldVideoId, string newVideoId)
    {
        if (string.IsNullOrWhiteSpace(oldVideoId) || string.IsNullOrWhiteSpace(newVideoId) ||
            oldVideoId.Length != 24 || newVideoId.Length != 24 ||
            !oldVideoId.All(Uri.IsHexDigit) || !newVideoId.All(Uri.IsHexDigit) ||
            string.Equals(oldVideoId, newVideoId, StringComparison.OrdinalIgnoreCase))
            return;

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using (var copy = connection.CreateCommand())
        {
            copy.Transaction = transaction;
            copy.CommandText = """
                INSERT INTO WatchProgress(ViewerId,VideoId,PositionSeconds,DurationSeconds,UpdatedUtc)
                SELECT ViewerId,$newVideo,PositionSeconds,DurationSeconds,UpdatedUtc
                FROM WatchProgress WHERE VideoId=$oldVideo
                ON CONFLICT(ViewerId,VideoId) DO UPDATE SET
                    PositionSeconds=excluded.PositionSeconds,
                    DurationSeconds=excluded.DurationSeconds,
                    UpdatedUtc=excluded.UpdatedUtc
                WHERE excluded.UpdatedUtc > WatchProgress.UpdatedUtc;
                """;
            copy.Parameters.AddWithValue("$newVideo", newVideoId);
            copy.Parameters.AddWithValue("$oldVideo", oldVideoId);
            copy.ExecuteNonQuery();
        }
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM WatchProgress WHERE VideoId=$oldVideo";
            delete.Parameters.AddWithValue("$oldVideo", oldVideoId);
            delete.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public void DeleteVideo(string videoId)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM WatchProgress WHERE VideoId=$video";
        command.Parameters.AddWithValue("$video", videoId);
        command.ExecuteNonQuery();
    }

    public void Save(WatchProgressRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ViewerId) || request.ViewerId.Length > 80 ||
            string.IsNullOrWhiteSpace(request.VideoId) || request.VideoId.Length != 24 ||
            !request.VideoId.All(Uri.IsHexDigit) ||
            !double.IsFinite(request.PositionSeconds) || !double.IsFinite(request.DurationSeconds) ||
            request.PositionSeconds < 0 || request.DurationSeconds <= 0 ||
            request.PositionSeconds > request.DurationSeconds + 5)
            throw new ArgumentException("بيانات تقدم المشاهدة غير صالحة.", nameof(request));

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO WatchProgress(ViewerId, VideoId, PositionSeconds, DurationSeconds, UpdatedUtc)
            VALUES($viewer, $video, $position, $duration, $updated)
            ON CONFLICT(ViewerId, VideoId) DO UPDATE SET
                PositionSeconds = excluded.PositionSeconds,
                DurationSeconds = excluded.DurationSeconds,
                UpdatedUtc = excluded.UpdatedUtc;
            """;
        command.Parameters.AddWithValue("$viewer", request.ViewerId.Trim());
        command.Parameters.AddWithValue("$video", request.VideoId.Trim());
        command.Parameters.AddWithValue("$position", Math.Clamp(request.PositionSeconds, 0, request.DurationSeconds));
        command.Parameters.AddWithValue("$duration", Math.Max(0, request.DurationSeconds));
        command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }
}
