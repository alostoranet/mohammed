using Microsoft.Data.Sqlite;
using System.IO;

namespace Alostora.RestHouse;

public sealed record SeriesRecord(string SeriesId, string Title, string Description, int? Year, string Genre, string? PosterPath, string? FolderPath);
public sealed record SeasonRecord(long SeasonId, string SeriesId, int SeasonNumber, string Title, string? FolderPath);
public sealed record EpisodeRecord(long EpisodeId, long SeasonId, string VideoId, int EpisodeNumber, string Title);
public sealed record MissingEpisodeRecord(long EpisodeId, string SeriesId, string SeriesTitle, int SeasonNumber, int EpisodeNumber, string Title, string VideoId);
public sealed record SaveSeriesRequest(string SeriesId, string Title, string? Description, int? Year, string? Genre, string? PosterPath, string? FolderPath = null);
public sealed record SaveSeasonRequest(string SeriesId, int SeasonNumber, string? Title, string? FolderPath = null);
public sealed record SaveEpisodeRequest(long SeasonId, string VideoId, int EpisodeNumber, string? Title);

public sealed class SeriesCatalogService
{
    private readonly string _connectionString;
    public SeriesCatalogService(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, ForeignKeys = true, DefaultTimeout = 10, Pooling = true }.ToString();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
          CREATE TABLE IF NOT EXISTS SeriesCatalog(
            SeriesId TEXT PRIMARY KEY, Title TEXT NOT NULL, Description TEXT NOT NULL DEFAULT '',
            Year INTEGER NULL, Genre TEXT NOT NULL DEFAULT '', PosterPath TEXT NULL, FolderPath TEXT NULL, UpdatedUtc TEXT NOT NULL
          );
          CREATE TABLE IF NOT EXISTS SeriesSeasons(
            SeasonId INTEGER PRIMARY KEY AUTOINCREMENT, SeriesId TEXT NOT NULL REFERENCES SeriesCatalog(SeriesId) ON DELETE CASCADE,
            SeasonNumber INTEGER NOT NULL CHECK(SeasonNumber > 0), Title TEXT NOT NULL DEFAULT '', FolderPath TEXT NULL,
            UNIQUE(SeriesId, SeasonNumber)
          );
          CREATE TABLE IF NOT EXISTS SeriesEpisodes(
            EpisodeId INTEGER PRIMARY KEY AUTOINCREMENT, SeasonId INTEGER NOT NULL REFERENCES SeriesSeasons(SeasonId) ON DELETE CASCADE,
            VideoId TEXT NOT NULL UNIQUE, EpisodeNumber INTEGER NOT NULL CHECK(EpisodeNumber > 0), Title TEXT NOT NULL DEFAULT '',
            UNIQUE(SeasonId, EpisodeNumber)
          );
          CREATE INDEX IF NOT EXISTS IX_SeriesSeasons_SeriesId ON SeriesSeasons(SeriesId);
          CREATE INDEX IF NOT EXISTS IX_SeriesEpisodes_SeasonId ON SeriesEpisodes(SeasonId);
          """;
        cmd.ExecuteNonQuery();
        EnsureColumn(c, "SeriesCatalog", "FolderPath", "TEXT NULL");
        EnsureColumn(c, "SeriesSeasons", "FolderPath", "TEXT NULL");
    }
    private static void EnsureColumn(SqliteConnection c, string table, string column, string type)
    {
        using var check = c.CreateCommand();
        check.CommandText = $"PRAGMA table_info({table})";
        using var reader = check.ExecuteReader();
        while (reader.Read()) if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
        reader.Close();
        using var alter = c.CreateCommand(); alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type}"; alter.ExecuteNonQuery();
    }
    private SqliteConnection Open() { var c = new SqliteConnection(_connectionString); c.Open(); return c; }

    public IReadOnlyList<SeriesRecord> ListSeries()
    {
        using var c=Open(); using var cmd=c.CreateCommand();
        cmd.CommandText="SELECT SeriesId,Title,Description,Year,Genre,PosterPath,FolderPath FROM SeriesCatalog ORDER BY Title COLLATE NOCASE";
        using var r=cmd.ExecuteReader(); var rows=new List<SeriesRecord>();
        while(r.Read()) rows.Add(new(r.GetString(0),r.GetString(1),r.GetString(2),r.IsDBNull(3)?null:r.GetInt32(3),r.GetString(4),r.IsDBNull(5)?null:r.GetString(5),r.IsDBNull(6)?null:r.GetString(6)));
        return rows;
    }
    public IReadOnlyList<SeasonRecord> ListSeasons(string seriesId)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT SeasonId,SeriesId,SeasonNumber,Title,FolderPath FROM SeriesSeasons WHERE SeriesId=$id ORDER BY SeasonNumber";
        cmd.Parameters.AddWithValue("$id", seriesId);
        using var r = cmd.ExecuteReader(); var rows = new List<SeasonRecord>();
        while (r.Read()) rows.Add(new(r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4)));
        return rows;
    }

    public SeasonRecord? GetSeason(long seasonId)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT SeasonId,SeriesId,SeasonNumber,Title,FolderPath FROM SeriesSeasons WHERE SeasonId=$id";
        cmd.Parameters.AddWithValue("$id", seasonId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? new SeasonRecord(r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4)) : null;
    }

    public IReadOnlyList<EpisodeRecord> ListEpisodes(long seasonId)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT EpisodeId,SeasonId,VideoId,EpisodeNumber,Title FROM SeriesEpisodes WHERE SeasonId=$id ORDER BY EpisodeNumber";
        cmd.Parameters.AddWithValue("$id", seasonId);
        using var r = cmd.ExecuteReader(); var rows = new List<EpisodeRecord>();
        while (r.Read()) rows.Add(new EpisodeRecord(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetInt32(3), r.GetString(4)));
        return rows;
    }

    public SeriesRecord? GetSeries(string id)
    {
        using var c=Open(); using var cmd=c.CreateCommand(); cmd.CommandText="SELECT SeriesId,Title,Description,Year,Genre,PosterPath,FolderPath FROM SeriesCatalog WHERE SeriesId=$id"; cmd.Parameters.AddWithValue("$id",id);
        using var r=cmd.ExecuteReader(); return r.Read()?new(r.GetString(0),r.GetString(1),r.GetString(2),r.IsDBNull(3)?null:r.GetInt32(3),r.GetString(4),r.IsDBNull(5)?null:r.GetString(5),r.IsDBNull(6)?null:r.GetString(6)):null;
    }
    public void SaveSeries(SaveSeriesRequest x)
    {
        using var c=Open(); using var cmd=c.CreateCommand();
        cmd.CommandText="""INSERT INTO SeriesCatalog(SeriesId,Title,Description,Year,Genre,PosterPath,FolderPath,UpdatedUtc) VALUES($id,$title,$description,$year,$genre,$poster,$folder,$updated) ON CONFLICT(SeriesId) DO UPDATE SET Title=excluded.Title,Description=excluded.Description,Year=excluded.Year,Genre=excluded.Genre,PosterPath=COALESCE(excluded.PosterPath, SeriesCatalog.PosterPath),FolderPath=excluded.FolderPath,UpdatedUtc=excluded.UpdatedUtc""";
        cmd.Parameters.AddWithValue("$id",x.SeriesId); cmd.Parameters.AddWithValue("$title",x.Title.Trim()); cmd.Parameters.AddWithValue("$description",x.Description??""); cmd.Parameters.AddWithValue("$year",(object?)x.Year??DBNull.Value); cmd.Parameters.AddWithValue("$genre",x.Genre??""); cmd.Parameters.AddWithValue("$poster",(object?)x.PosterPath??DBNull.Value); cmd.Parameters.AddWithValue("$folder",(object?)x.FolderPath??DBNull.Value); cmd.Parameters.AddWithValue("$updated",DateTime.UtcNow.ToString("O")); cmd.ExecuteNonQuery();
    }
    public long AddSeason(SaveSeasonRequest x)
    {
        using var c=Open(); using var cmd=c.CreateCommand();
        cmd.CommandText="INSERT INTO SeriesSeasons(SeriesId,SeasonNumber,Title,FolderPath) VALUES($series,$number,$title,$folder); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$series",x.SeriesId); cmd.Parameters.AddWithValue("$number",x.SeasonNumber); cmd.Parameters.AddWithValue("$title",x.Title??""); cmd.Parameters.AddWithValue("$folder",(object?)x.FolderPath??DBNull.Value);
        return (long)cmd.ExecuteScalar()!;
    }
    public void DeleteSeries(string seriesId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM SeriesCatalog WHERE SeriesId=$id";
        cmd.Parameters.AddWithValue("$id", seriesId);
        if (cmd.ExecuteNonQuery() == 0) throw new KeyNotFoundException("المسلسل غير موجود");
    }

    public void DeleteSeason(long seasonId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM SeriesSeasons WHERE SeasonId=$id";
        cmd.Parameters.AddWithValue("$id", seasonId);
        if (cmd.ExecuteNonQuery() == 0) throw new KeyNotFoundException("الموسم غير موجود");
    }

    public void UpdateSeasonFolder(long seasonId, string? folderPath)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE SeriesSeasons SET FolderPath=$folder WHERE SeasonId=$id";
        cmd.Parameters.AddWithValue("$folder", (object?)folderPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", seasonId);
        if (cmd.ExecuteNonQuery() == 0) throw new KeyNotFoundException("الموسم غير موجود");
    }

    public long AddEpisode(SaveEpisodeRequest x)
    {
        using var c=Open(); using var cmd=c.CreateCommand();
        cmd.CommandText="INSERT INTO SeriesEpisodes(SeasonId,VideoId,EpisodeNumber,Title) VALUES($season,$video,$number,$title); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$season",x.SeasonId); cmd.Parameters.AddWithValue("$video",x.VideoId); cmd.Parameters.AddWithValue("$number",x.EpisodeNumber); cmd.Parameters.AddWithValue("$title",x.Title??"");
        return (long)cmd.ExecuteScalar()!;
    }
    public string UpdateEpisodeVideo(long episodeId, string videoId)
    {
        using var c = Open();
        using var transaction = c.BeginTransaction();
        string oldVideoId;
        using (var find = c.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText = "SELECT VideoId FROM SeriesEpisodes WHERE EpisodeId=$id";
            find.Parameters.AddWithValue("$id", episodeId);
            oldVideoId = find.ExecuteScalar() as string ?? throw new KeyNotFoundException("الحلقة غير موجودة");
        }
        using (var update = c.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE SeriesEpisodes SET VideoId=$video WHERE EpisodeId=$id";
            update.Parameters.AddWithValue("$video", videoId);
            update.Parameters.AddWithValue("$id", episodeId);
            update.ExecuteNonQuery();
        }
        transaction.Commit();
        return oldVideoId;
    }

    public IReadOnlyList<MissingEpisodeRecord> ListMissingEpisodes(LibraryService library)
    {
        var available = library.List().Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT se.EpisodeId, sc.SeriesId, sc.Title, ss.SeasonNumber,
                   se.EpisodeNumber, se.Title, se.VideoId
            FROM SeriesEpisodes se
            JOIN SeriesSeasons ss ON ss.SeasonId=se.SeasonId
            JOIN SeriesCatalog sc ON sc.SeriesId=ss.SeriesId
            ORDER BY sc.Title COLLATE NOCASE, ss.SeasonNumber, se.EpisodeNumber
            """;
        using var r = cmd.ExecuteReader();
        var rows = new List<MissingEpisodeRecord>();
        while (r.Read())
        {
            var videoId = r.GetString(6);
            if (!available.Contains(videoId))
                rows.Add(new MissingEpisodeRecord(r.GetInt64(0), r.GetString(1), r.GetString(2),
                    r.GetInt32(3), r.GetInt32(4), r.GetString(5), videoId));
        }
        return rows;
    }

    public object GetCatalog(LibraryService library, VideoMetadataService metadata)
    {
        // Load each source into memory before composing the response. This avoids
        // opening nested SQLite readers on the same connection while one is active.
        var files = library.List().ToDictionary(x => x.Id);
        var metadataById = metadata.List().ToDictionary(x => x.VideoId);
        var seriesRows = ListSeries();

        var seasonMap = new Dictionary<long, (SeasonRecord Season, List<EpisodeRecord> Episodes)>();
        using (var c = Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                SELECT ss.SeasonId, ss.SeriesId, ss.SeasonNumber, ss.Title, ss.FolderPath,
                       se.EpisodeId, se.VideoId, se.EpisodeNumber, se.Title
                FROM SeriesSeasons ss
                LEFT JOIN SeriesEpisodes se ON se.SeasonId = ss.SeasonId
                ORDER BY ss.SeriesId, ss.SeasonNumber, se.EpisodeNumber
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var seasonId = r.GetInt64(0);
                if (!seasonMap.ContainsKey(seasonId))
                {
                    var season = new SeasonRecord(seasonId, r.GetString(1), r.GetInt32(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4));
                    seasonMap.Add(seasonId, (season, new List<EpisodeRecord>()));
                }

                if (!r.IsDBNull(5))
                {
                    seasonMap[seasonId].Episodes.Add(new EpisodeRecord(
                        r.GetInt64(5), seasonId, r.GetString(6), r.GetInt32(7), r.GetString(8)));
                }
            }
        }

        return seriesRows.Select(series =>
        {
            var seasons = seasonMap.Values
                .Where(x => x.Season.SeriesId == series.SeriesId)
                .OrderBy(x => x.Season.SeasonNumber)
                .Select(x => new
                {
                    seasonId = x.Season.SeasonId,
                    seriesId = x.Season.SeriesId,
                    seasonNumber = x.Season.SeasonNumber,
                    title = x.Season.Title,
                    episodes = x.Episodes
                        .Select(episode =>
                        {
                            var hasFile = files.TryGetValue(episode.VideoId, out var file);
                            metadataById.TryGetValue(episode.VideoId, out var videoMetadata);
                            var title = string.IsNullOrWhiteSpace(episode.Title)
                                ? (videoMetadata?.Title ?? file?.Title ?? "حلقة")
                                : episode.Title;
                            return new
                            {
                                episodeId = episode.EpisodeId,
                                videoId = episode.VideoId,
                                episodeNumber = episode.EpisodeNumber,
                                title,
                                url = hasFile ? file!.Url : null,
                                isMissing = !hasFile,
                                director = videoMetadata?.Director,
                                cast = videoMetadata?.Cast,
                                rating = videoMetadata?.Rating,
                                runtimeMinutes = videoMetadata?.RuntimeMinutes,
                                releaseDate = videoMetadata?.ReleaseDate,
                                tags = videoMetadata?.Tags
                            };
                        })
                        .ToArray()
                })
                .ToArray();

            return new
            {
                seriesId = series.SeriesId,
                title = series.Title,
                description = series.Description,
                year = series.Year,
                genre = series.Genre,
                posterPath = series.PosterPath,
                seasons
            };
        }).ToArray();
    }
}
