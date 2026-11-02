using Microsoft.Data.Sqlite;
using System.IO;

namespace Alostora.RestHouse;

public sealed record VideoMetadata(
    string VideoId, string Title, string Description, int? Year, string Genre, string Kind, string? PosterPath,
    string? Director, string? Cast, double? Rating, int? RuntimeMinutes, string? ReleaseDate, string? Tags);

public sealed record SaveVideoMetadataRequest(
    string VideoId, string Title, string? Description, int? Year, string? Genre, string? Kind, string? PosterPath,
    string? Director = null, string? Cast = null, double? Rating = null, int? RuntimeMinutes = null,
    string? ReleaseDate = null, string? Tags = null);

public sealed class VideoMetadataService
{
    private readonly string _connectionString;

    public VideoMetadataService(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, DefaultTimeout = 10, Pooling = true }.ToString();
        using var c = new SqliteConnection(_connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS VideoMetadata(
              VideoId TEXT PRIMARY KEY,
              Title TEXT NOT NULL,
              Description TEXT NOT NULL DEFAULT '',
              Year INTEGER NULL,
              Genre TEXT NOT NULL DEFAULT '',
              Kind TEXT NOT NULL DEFAULT 'Movie',
              PosterPath TEXT NULL,
              Director TEXT NULL,
              CastMembers TEXT NULL,
              Rating REAL NULL,
              RuntimeMinutes INTEGER NULL,
              ReleaseDate TEXT NULL,
              Tags TEXT NULL,
              UpdatedUtc TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
        EnsureColumn(c, "Director", "TEXT NULL");
        EnsureColumn(c, "CastMembers", "TEXT NULL");
        EnsureColumn(c, "Rating", "REAL NULL");
        EnsureColumn(c, "RuntimeMinutes", "INTEGER NULL");
        EnsureColumn(c, "ReleaseDate", "TEXT NULL");
        EnsureColumn(c, "Tags", "TEXT NULL");
    }

    private static void EnsureColumn(SqliteConnection connection, string column, string type)
    {
        using var check = connection.CreateCommand();
        check.CommandText = "PRAGMA table_info(VideoMetadata)";
        using var reader = check.ExecuteReader();
        while (reader.Read())
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return;
        reader.Close();
        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE VideoMetadata ADD COLUMN {column} {type}";
        alter.ExecuteNonQuery();
    }

    public VideoMetadata? Get(string id)
    {
        using var c = new SqliteConnection(_connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT VideoId,Title,Description,Year,Genre,Kind,PosterPath,
                   Director,CastMembers,Rating,RuntimeMinutes,ReleaseDate,Tags
            FROM VideoMetadata WHERE VideoId=$id
            """;
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    public IReadOnlyList<VideoMetadata> List()
    {
        using var c = new SqliteConnection(_connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT VideoId,Title,Description,Year,Genre,Kind,PosterPath,
                   Director,CastMembers,Rating,RuntimeMinutes,ReleaseDate,Tags
            FROM VideoMetadata ORDER BY Title COLLATE NOCASE
            """;
        using var r = cmd.ExecuteReader();
        var rows = new List<VideoMetadata>();
        while (r.Read()) rows.Add(Read(r));
        return rows;
    }

    private static VideoMetadata Read(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2),
            r.IsDBNull(3) ? null : r.GetInt32(3), r.GetString(4), r.GetString(5),
            r.IsDBNull(6) ? null : r.GetString(6),
            r.IsDBNull(7) ? null : r.GetString(7),
            r.IsDBNull(8) ? null : r.GetString(8),
            r.IsDBNull(9) ? null : r.GetDouble(9),
            r.IsDBNull(10) ? null : r.GetInt32(10),
            r.IsDBNull(11) ? null : r.GetString(11),
            r.IsDBNull(12) ? null : r.GetString(12));

    public void Delete(string videoId)
    {
        using var c = new SqliteConnection(_connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM VideoMetadata WHERE VideoId=$id";
        cmd.Parameters.AddWithValue("$id", videoId);
        cmd.ExecuteNonQuery();
    }

    public void Save(SaveVideoMetadataRequest x)
    {
        using var c = new SqliteConnection(_connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO VideoMetadata(
              VideoId,Title,Description,Year,Genre,Kind,PosterPath,
              Director,CastMembers,Rating,RuntimeMinutes,ReleaseDate,Tags,UpdatedUtc)
            VALUES($id,$title,$description,$year,$genre,$kind,$poster,
              $director,$castMembers,$rating,$runtime,$releaseDate,$tags,$updated)
            ON CONFLICT(VideoId) DO UPDATE SET
              Title=excluded.Title,
              Description=excluded.Description,
              Year=excluded.Year,
              Genre=excluded.Genre,
              Kind=excluded.Kind,
              PosterPath=COALESCE(excluded.PosterPath, VideoMetadata.PosterPath),
              Director=excluded.Director,
              CastMembers=excluded.CastMembers,
              Rating=excluded.Rating,
              RuntimeMinutes=excluded.RuntimeMinutes,
              ReleaseDate=excluded.ReleaseDate,
              Tags=excluded.Tags,
              UpdatedUtc=excluded.UpdatedUtc;
            """;
        cmd.Parameters.AddWithValue("$id", x.VideoId);
        cmd.Parameters.AddWithValue("$title", x.Title.Trim());
        cmd.Parameters.AddWithValue("$description", x.Description ?? "");
        cmd.Parameters.AddWithValue("$year", (object?)x.Year ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$genre", x.Genre ?? "");
        cmd.Parameters.AddWithValue("$kind", x.Kind == "Series" ? "Series" : "Movie");
        cmd.Parameters.AddWithValue("$poster", (object?)x.PosterPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$director", (object?)x.Director ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$castMembers", (object?)x.Cast ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rating", (object?)x.Rating ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$runtime", (object?)x.RuntimeMinutes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$releaseDate", (object?)x.ReleaseDate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tags", (object?)x.Tags ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }
}