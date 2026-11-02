using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Alostora.RestHouse;

public sealed record LibraryVideo(string Id, string Title, string FileName, string Url, long SizeBytes);
public sealed record VideoFileCandidate(string FullPath, string FileName, long SizeBytes);
public sealed record ResolvedVideo(string FullPath, string ContentType);
public sealed record SubtitleTrack(string FileName, string Label, string Language, string Url, bool IsDefault, bool IsForced, bool IsSdh);

public sealed class LibraryService : IDisposable
{
    private readonly string _root;
    private readonly string _connectionString;
    private readonly object _sync = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly FileSystemWatcher _watcher;
    private LibraryVideo[]? _videos;
    private Dictionary<string, ResolvedVideo>? _resolved;

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp4"] = "video/mp4",
        [".m4v"] = "video/mp4",
        [".webm"] = "video/webm",
        [".mkv"] = "video/x-matroska",
        [".avi"] = "video/x-msvideo",
        [".mov"] = "video/quicktime",
        [".ts"] = "video/mp2t",
        [".m2ts"] = "video/mp2t",
        [".mts"] = "video/mp2t",
        [".flv"] = "video/x-flv",
        [".wmv"] = "video/x-ms-wmv",
        [".mpg"] = "video/mpeg",
        [".mpeg"] = "video/mpeg",
        [".3gp"] = "video/3gpp"
    };

    public LibraryService(string root, string databasePath)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            DefaultTimeout = 10,
            Pooling = true
        }.ToString();

        using (var connection = new SqliteConnection(_connectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS LibraryFileIds(
                    RelativePath TEXT PRIMARY KEY COLLATE NOCASE,
                    VideoId TEXT NOT NULL UNIQUE,
                    UpdatedUtc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS LibraryScanRoots(
                    FolderPath TEXT PRIMARY KEY COLLATE NOCASE,
                    AddedUtc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS ExternalLibraryFiles(
                    FullPath TEXT PRIMARY KEY COLLATE NOCASE,
                    VideoId TEXT NOT NULL UNIQUE,
                    UpdatedUtc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS ExcludedLibraryFiles(
                    VideoId TEXT PRIMARY KEY,
                    ExcludedUtc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS SelectedLibraryFiles(
                    FullPath TEXT PRIMARY KEY COLLATE NOCASE,
                    SelectedUtc TEXT NOT NULL
                );
                """;
            command.ExecuteNonQuery();
        }

        _watcher = CreateWatcher(_root, true);
        _watcher.Renamed += OnRenamed;
        _watchers.Add(_watcher);
        foreach (var folder in GetScanRoots())
            AddWatcher(folder);
    }

    private FileSystemWatcher CreateWatcher(string folder, bool includeSubdirectories)
    {
        var watcher = new FileSystemWatcher(folder)
        {
            IncludeSubdirectories = includeSubdirectories,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                           NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        watcher.Created += Invalidate;
        watcher.Changed += Invalidate;
        watcher.Deleted += Invalidate;
        watcher.Error += (_, _) => Invalidate();
        return watcher;
    }

    public IReadOnlyList<LibraryVideo> List()
    {
        EnsureIndex();
        lock (_sync)
            return _videos!.ToArray();
    }

    public IReadOnlyList<LibraryVideo> ListFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            return Array.Empty<LibraryVideo>();
        var root = Path.GetFullPath(folderPath);
        return List().Where(video =>
        {
            var resolved = Resolve(video.Id);
            if (resolved is null) return false;
            var relative = Path.GetRelativePath(root, Path.GetFullPath(resolved.FullPath));
            return relative == "." || (!Path.IsPathRooted(relative) &&
                relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        }).ToArray();
    }

    public IReadOnlyList<SubtitleTrack> ListSubtitles(string videoId)
    {
        var video = Resolve(videoId);
        if (video is null) return Array.Empty<SubtitleTrack>();
        var directory = Path.GetDirectoryName(video.FullPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return Array.Empty<SubtitleTrack>();

        var stem = Path.GetFileNameWithoutExtension(video.FullPath);
        var tracks = new List<SubtitleTrack>();
        IEnumerable<string> candidates;
        try { candidates = Directory.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly).ToArray(); }
        catch { return tracks; }

        foreach (var path in candidates)
        {
            var extension = Path.GetExtension(path);
            if (!extension.Equals(".srt", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".vtt", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".ass", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".ssa", StringComparison.OrdinalIgnoreCase))
                continue;
            var name = Path.GetFileNameWithoutExtension(path);
            if (!name.Equals(stem, StringComparison.OrdinalIgnoreCase) &&
                !name.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                if (new FileInfo(path).Length > 2 * 1024 * 1024) continue;
                var suffix = name.Length == stem.Length ? "" : name[(stem.Length + 1)..];
                var tokens = suffix.Split('.', StringSplitOptions.RemoveEmptyEntries)
                    .Select(token => token.Trim().ToLowerInvariant()).Where(token => token.Length > 0).ToArray();
                var language = tokens.FirstOrDefault(IsKnownLanguage) ?? "und";
                var isDefault = tokens.Contains("default", StringComparer.OrdinalIgnoreCase);
                var isForced = tokens.Contains("forced", StringComparer.OrdinalIgnoreCase) ||
                               tokens.Contains("foreign", StringComparer.OrdinalIgnoreCase);
                var isSdh = tokens.Contains("sdh", StringComparer.OrdinalIgnoreCase) ||
                            tokens.Contains("cc", StringComparer.OrdinalIgnoreCase) ||
                            (tokens.Contains("hi", StringComparer.OrdinalIgnoreCase) && language != "hi" && language != "hin");
                var titleTokens = tokens.Where(token => !IsKnownLanguage(token) &&
                    token is not ("default" or "forced" or "foreign" or "sdh" or "cc" or "hi"));
                var customTitle = string.Join(" ", titleTokens);
                var label = LanguageLabel(language, customTitle, isForced, isSdh);
                tracks.Add(new SubtitleTrack(Path.GetFileName(path), label, language,
                    "/api/subtitles/" + videoId + "/" + Uri.EscapeDataString(Path.GetFileName(path)),
                    isDefault, isForced, isSdh));
            }
            catch { }
        }
        return tracks.OrderBy(x => x.IsDefault ? 0 : 1)
            .ThenBy(x => x.Language == "ar" ? 0 : 1)
            .ThenBy(x => x.Label, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public string? ReadSubtitle(string videoId, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName != Path.GetFileName(fileName))
            return null;
        var video = Resolve(videoId);
        if (video is null) return null;
        var directory = Path.GetDirectoryName(video.FullPath);
        if (string.IsNullOrWhiteSpace(directory)) return null;
        if (!ListSubtitles(videoId).Any(x => string.Equals(x.FileName, fileName, StringComparison.OrdinalIgnoreCase)))
            return null;

        var fullPath = Path.GetFullPath(Path.Combine(directory, fileName));
        if (!string.Equals(Path.GetDirectoryName(fullPath), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase))
            return null;
        var extension = Path.GetExtension(fullPath);
        var info = new FileInfo(fullPath);
        if (!info.Exists || info.Length > 2 * 1024 * 1024) return null;
        var bytes = File.ReadAllBytes(fullPath);
        string content;
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            content = Encoding.Unicode.GetString(bytes);
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            content = Encoding.BigEndianUnicode.GetString(bytes);
        else
        {
            try { content = new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                content = Encoding.GetEncoding(1256).GetString(bytes);
            }
        }
        if (extension.Equals(".srt", StringComparison.OrdinalIgnoreCase))
            return ConvertSrtToVtt(content);
        if (extension.Equals(".ass", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".ssa", StringComparison.OrdinalIgnoreCase))
            return ConvertAssToVtt(content);
        return EnsureVttHeader(content);
    }

    private static bool IsKnownLanguage(string value) => value.ToLowerInvariant() is
        "ar" or "ara" or "en" or "eng" or "fr" or "fra" or "fre" or "tr" or "tur" or
        "es" or "spa" or "de" or "deu" or "ger" or "it" or "ita" or "pt" or "por" or
        "ru" or "rus" or "ja" or "jpn" or "ko" or "kor" or "zh" or "zho" or "chi" or
        "hi" or "hin" or "nl" or "nld" or "dut" or "pl" or "pol" or "sv" or "swe";

    private static string LanguageLabel(string language, string customTitle, bool forced, bool sdh)
    {
        var known = language.ToLowerInvariant() switch
        {
            "ar" or "ara" => "العربية",
            "en" or "eng" => "English",
            "fr" or "fra" or "fre" => "Français",
            "tr" or "tur" => "Türkçe",
            "es" or "spa" => "Español",
            "de" or "deu" or "ger" => "Deutsch",
            "it" or "ita" => "Italiano",
            "pt" or "por" => "Português",
            "ru" or "rus" => "Русский",
            "ja" or "jpn" => "日本語",
            "ko" or "kor" => "한국어",
            "zh" or "zho" or "chi" => "中文",
            "hi" or "hin" => "हिन्दी",
            "nl" or "nld" or "dut" => "Nederlands",
            "pl" or "pol" => "Polski",
            "sv" or "swe" => "Svenska",
            _ => ""
        };
        var label = !string.IsNullOrEmpty(known) ? known :
            !string.IsNullOrWhiteSpace(customTitle) ? customTitle :
            language == "und" ? "ترجمة" : language;
        if (forced) label += " — إجباري";
        if (sdh) label += " — للصم وضعاف السمع";
        return label;
    }

    private static string ConvertAssToVtt(string text)
    {
        var lines = text.TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var format = new[] { "layer", "start", "end", "style", "name", "marginl", "marginr", "marginv", "effect", "text" };
        var output = new StringBuilder("WEBVTT\n\n");
        var inEvents = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
            {
                inEvents = line.Equals("[Events]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inEvents) continue;

            if (line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
            {
                format = line["Format:".Length..].Split(',').Select(x => x.Trim().ToLowerInvariant()).ToArray();
                continue;
            }
            if (!line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase)) continue;

            var values = line["Dialogue:".Length..].TrimStart().Split(',', format.Length);
            if (values.Length < format.Length) continue;
            var startIndex = Array.IndexOf(format, "start");
            var endIndex = Array.IndexOf(format, "end");
            var textIndex = Array.IndexOf(format, "text");
            if (startIndex < 0 || endIndex < 0 || textIndex < 0) continue;
            if (!TryAssTimestamp(values[startIndex], out var start) ||
                !TryAssTimestamp(values[endIndex], out var end)) continue;

            var caption = values[textIndex]
                .Replace(@"\N", "\n", StringComparison.OrdinalIgnoreCase)
                .Replace(@"\n", "\n", StringComparison.OrdinalIgnoreCase)
                .Replace(@"\h", " ", StringComparison.OrdinalIgnoreCase);
            caption = Regex.Replace(caption, @"\{[^}]*\}", "");
            caption = caption.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
            if (string.IsNullOrWhiteSpace(caption)) continue;
            output.Append(start).Append(" --> ").Append(end).Append('\n');
            output.Append(caption).Append("\n\n");
        }
        return output.ToString();
    }

    private static bool TryAssTimestamp(string value, out string timestamp)
    {
        timestamp = "";
        var match = Regex.Match(value.Trim(), @"^(\d+):(\d{1,2}):(\d{1,2})[.](\d{1,2})$");
        if (!match.Success ||
            !int.TryParse(match.Groups[1].Value, out var hours) ||
            !int.TryParse(match.Groups[2].Value, out var minutes) ||
            !int.TryParse(match.Groups[3].Value, out var seconds) ||
            !int.TryParse(match.Groups[4].Value, out var centiseconds) ||
            minutes > 59 || seconds > 59) return false;
        timestamp = $"{hours:00}:{minutes:00}:{seconds:00}.{centiseconds * 10:000}";
        return true;
    }

    private static string EnsureVttHeader(string text)
    {
        var clean = text.TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n');
        return clean.StartsWith("WEBVTT", StringComparison.Ordinal) ? clean : "WEBVTT\n\n" + clean;
    }

    private static string ConvertSrtToVtt(string text)
    {
        var clean = text.TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = clean.Split('\n');
        var output = new StringBuilder("WEBVTT\n\n");
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Contains("-->"))
                output.AppendLine(trimmed.Replace(',', '.'));
            else
                output.AppendLine(line);
        }
        return output.ToString();
    }

    public ResolvedVideo? Resolve(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length != 24 || id.Any(c => !Uri.IsHexDigit(c)))
            return null;

        EnsureIndex();
        lock (_sync)
            return _resolved!.TryGetValue(id, out var video) ? video : null;
    }

    public IReadOnlyList<VideoFileCandidate> PreviewFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            throw new DirectoryNotFoundException("مجلد الفيديو غير موجود أو لا يمكن الوصول إليه.");
        var root = Path.GetFullPath(folderPath);
        var files = new List<VideoFileCandidate>();
        foreach (var path in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
        {
            if (!ContentTypes.ContainsKey(Path.GetExtension(path))) continue;
            try
            {
                var info = new FileInfo(path);
                if (info.Exists) files.Add(new VideoFileCandidate(Path.GetFullPath(path), info.Name, info.Length));
            }
            catch { }
        }
        return files.OrderBy(x => x.FileName, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public int RegisterSelectedFiles(string folderPath, IEnumerable<string> selectedPaths)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            throw new DirectoryNotFoundException("مجلد الفيديو غير موجود أو لا يمكن الوصول إليه.");
        var root = Path.GetFullPath(folderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var selected = selectedPaths?.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            ?? Array.Empty<string>();
        foreach (var path in selected)
        {
            var relative = Path.GetRelativePath(root, path);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                !File.Exists(path) || !ContentTypes.ContainsKey(Path.GetExtension(path)))
                throw new InvalidOperationException("توجد ملفات خارج المجلد المحدد أو ليست ملفات فيديو صالحة.");
        }

        var allFiles = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(path => ContentTypes.ContainsKey(Path.GetExtension(path)))
            .Select(Path.GetFullPath).ToArray();
        var selectedSet = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
        using (var connection = new SqliteConnection(_connectionString))
        {
            connection.Open();
            using var transaction = connection.BeginTransaction();
            using (var removeRoot = connection.CreateCommand())
            {
                removeRoot.Transaction = transaction;
                removeRoot.CommandText = "DELETE FROM LibraryScanRoots WHERE FolderPath=$path";
                removeRoot.Parameters.AddWithValue("$path", root);
                removeRoot.ExecuteNonQuery();
            }

            var previouslySelected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var selectedCommand = connection.CreateCommand())
            {
                selectedCommand.Transaction = transaction;
                selectedCommand.CommandText = "SELECT FullPath FROM SelectedLibraryFiles";
                using var reader = selectedCommand.ExecuteReader();
                while (reader.Read()) previouslySelected.Add(reader.GetString(0));
            }

            foreach (var path in allFiles)
            {
                var insideLibraryRoot = IsInsideRoot(path);
                if (!selectedSet.Contains(path))
                {
                    // Files already selected in an earlier import must remain visible.
                    // For files inside the automatically scanned library root, hide only
                    // files that have never been explicitly selected.
                    if (insideLibraryRoot && !previouslySelected.Contains(path))
                    {
                        var id = GetOrCreateId(connection, transaction,
                            NormalizeRelativePath(Path.GetRelativePath(_root, path)));
                        using var exclude = connection.CreateCommand();
                        exclude.Transaction = transaction;
                        exclude.CommandText = "INSERT INTO ExcludedLibraryFiles(VideoId,ExcludedUtc) VALUES($id,$utc) ON CONFLICT(VideoId) DO NOTHING";
                        exclude.Parameters.AddWithValue("$id", id);
                        exclude.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
                        exclude.ExecuteNonQuery();
                    }
                    // External, unselected files are deliberately not registered.
                    continue;
                }

                var videoId = insideLibraryRoot
                    ? GetOrCreateId(connection, transaction, NormalizeRelativePath(Path.GetRelativePath(_root, path)))
                    : GetOrCreateExternalId(connection, transaction, path);
                using (var include = connection.CreateCommand())
                {
                    include.Transaction = transaction;
                    include.CommandText = "DELETE FROM ExcludedLibraryFiles WHERE VideoId=$id";
                    include.Parameters.AddWithValue("$id", videoId);
                    include.ExecuteNonQuery();
                }
                using (var remember = connection.CreateCommand())
                {
                    remember.Transaction = transaction;
                    remember.CommandText = "INSERT INTO SelectedLibraryFiles(FullPath,SelectedUtc) VALUES($path,$utc) ON CONFLICT(FullPath) DO UPDATE SET SelectedUtc=excluded.SelectedUtc";
                    remember.Parameters.AddWithValue("$path", path);
                    remember.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
                    remember.ExecuteNonQuery();
                }
            }
            transaction.Commit();
        }

        if (!IsInsideRoot(root))
        {
            lock (_sync)
            {
                foreach (var watcher in _watchers.Where(w => string.Equals(
                    w.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), root,
                    StringComparison.OrdinalIgnoreCase)).ToArray())
                {
                    watcher.Dispose();
                    _watchers.Remove(watcher);
                }
            }
            AddWatcher(root);
        }
        Invalidate();
        return selected.Length;
    }

    public void EnsureFolderRegistered(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            return;
        var full = Path.GetFullPath(folderPath);
        if (IsInsideRoot(full)) return;

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM LibraryScanRoots WHERE FolderPath=$path";
        command.Parameters.AddWithValue("$path", full);
        if (command.ExecuteScalar() is null)
            RegisterFolder(full);
        else
            AddWatcher(full);
    }

    public int RegisterFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            throw new DirectoryNotFoundException("مجلد الفيديو غير موجود أو لا يمكن الوصول إليه.");

        var full = Path.GetFullPath(folderPath);
        if (IsInsideRoot(full))
        {
            Invalidate();
            return Directory.EnumerateFiles(full, "*.*", SearchOption.AllDirectories)
                .Count(path => ContentTypes.ContainsKey(Path.GetExtension(path)));
        }

        using (var connection = new SqliteConnection(_connectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT OR IGNORE INTO LibraryScanRoots(FolderPath,AddedUtc) VALUES($path,$utc)";
            command.Parameters.AddWithValue("$path", full);
            command.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }

        AddWatcher(full);
        Invalidate();
        return Directory.EnumerateFiles(full, "*.*", SearchOption.AllDirectories)
            .Count(path => ContentTypes.ContainsKey(Path.GetExtension(path)));
    }

    private bool IsInsideRoot(string path)
    {
        var relative = Path.GetRelativePath(_root, path);
        return relative == "." || (!Path.IsPathRooted(relative) &&
            relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    private string[] GetScanRoots()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT FolderPath FROM LibraryScanRoots";
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
        {
            var path = reader.GetString(0);
            if (Directory.Exists(path) && !IsInsideRoot(path))
                result.Add(path);
        }
        return result.ToArray();
    }

    private void AddWatcher(string folder)
    {
        lock (_sync)
        {
            if (_watchers.Any(w => string.Equals(w.Path.TrimEnd(Path.DirectorySeparatorChar),
                folder.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)))
                return;
            try
            {
                var watcher = CreateWatcher(folder, true);
                watcher.Renamed += OnExternalRenamed;
                _watchers.Add(watcher);
            }
            catch
            {
                // An inaccessible optional folder must not prevent the server from starting.
            }
        }
    }

    private void EnsureIndex()
    {
        lock (_sync)
        {
            if (_videos is not null && _resolved is not null)
                return;

            var videos = new List<LibraryVideo>();
            var resolved = new Dictionary<string, ResolvedVideo>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction();

            var excludedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var excludedCommand = connection.CreateCommand())
            {
                excludedCommand.Transaction = transaction;
                excludedCommand.CommandText = "SELECT VideoId FROM ExcludedLibraryFiles";
                using var excludedReader = excludedCommand.ExecuteReader();
                while (excludedReader.Read()) excludedIds.Add(excludedReader.GetString(0));
            }

            var roots = new[] { _root }.Concat(GetScanRoots()).Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var scanRoot in roots)
            {
                if (!Directory.Exists(scanRoot)) continue;
                IEnumerable<string> paths;
                try { paths = Directory.EnumerateFiles(scanRoot, "*.*", SearchOption.AllDirectories).ToArray(); }
                catch { continue; }

                foreach (var path in paths)
                {
                    if (!ContentTypes.TryGetValue(Path.GetExtension(path), out var contentType))
                        continue;
                    var fullPath = Path.GetFullPath(path);
                    if (!seen.Add(fullPath)) continue;

                    var id = IsInsideRoot(fullPath)
                        ? GetOrCreateId(connection, transaction, NormalizeRelativePath(Path.GetRelativePath(_root, fullPath)))
                        : GetOrCreateExternalId(connection, transaction, fullPath);
                    if (excludedIds.Contains(id)) continue;
                    var file = new FileInfo(fullPath);
                    videos.Add(new LibraryVideo(id, file.Name[..^file.Extension.Length],
                        file.Name, "/media/" + id, file.Length));
                    resolved[id] = new ResolvedVideo(fullPath, contentType);
                }
            }

            using (var selectedCommand = connection.CreateCommand())
            {
                selectedCommand.Transaction = transaction;
                selectedCommand.CommandText = "SELECT FullPath,VideoId FROM ExternalLibraryFiles";
                using var selectedReader = selectedCommand.ExecuteReader();
                var selectedFiles = new List<(string Path, string Id)>();
                while (selectedReader.Read()) selectedFiles.Add((selectedReader.GetString(0), selectedReader.GetString(1)));
                selectedReader.Close();
                foreach (var selectedFile in selectedFiles)
                {
                    if (!File.Exists(selectedFile.Path) || !ContentTypes.TryGetValue(Path.GetExtension(selectedFile.Path), out var contentType)) continue;
                    var fullPath = Path.GetFullPath(selectedFile.Path);
                    if (!seen.Add(fullPath) || excludedIds.Contains(selectedFile.Id)) continue;
                    var file = new FileInfo(fullPath);
                    videos.Add(new LibraryVideo(selectedFile.Id, file.Name[..^file.Extension.Length], file.Name, "/media/" + selectedFile.Id, file.Length));
                    resolved[selectedFile.Id] = new ResolvedVideo(fullPath, contentType);
                }
            }

            transaction.Commit();
            _videos = videos
                .OrderBy(video => video.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            _resolved = resolved;
        }
    }

    private static string GetOrCreateId(SqliteConnection connection, SqliteTransaction transaction, string relativePath)
    {
        using (var find = connection.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText = "SELECT VideoId FROM LibraryFileIds WHERE RelativePath=$path";
            find.Parameters.AddWithValue("$path", relativePath);
            var existing = find.ExecuteScalar() as string;
            if (!string.IsNullOrWhiteSpace(existing))
                return existing;
        }

        var id = CreateId(relativePath);
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO LibraryFileIds(RelativePath,VideoId,UpdatedUtc) VALUES($path,$id,$updated)";
        insert.Parameters.AddWithValue("$path", relativePath);
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
        insert.ExecuteNonQuery();
        return id;
    }

    private static string GetOrCreateExternalId(SqliteConnection connection, SqliteTransaction transaction, string fullPath)
    {
        using (var find = connection.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText = "SELECT VideoId FROM ExternalLibraryFiles WHERE FullPath=$path";
            find.Parameters.AddWithValue("$path", fullPath);
            var existing = find.ExecuteScalar() as string;
            if (!string.IsNullOrWhiteSpace(existing)) return existing;
        }

        var id = CreateId("external:" + fullPath);
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO ExternalLibraryFiles(FullPath,VideoId,UpdatedUtc) VALUES($path,$id,$updated)";
        insert.Parameters.AddWithValue("$path", fullPath);
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
        insert.ExecuteNonQuery();
        return id;
    }

    private void OnRenamed(object? sender, RenamedEventArgs e)
    {
        try
        {
            var oldRelative = NormalizeRelativePath(Path.GetRelativePath(_root, e.OldFullPath));
            var newRelative = NormalizeRelativePath(Path.GetRelativePath(_root, e.FullPath));
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction();

            using var find = connection.CreateCommand();
            find.Transaction = transaction;
            find.CommandText = """
                SELECT RelativePath, VideoId FROM LibraryFileIds
                WHERE RelativePath=$old OR substr(RelativePath,1,length($prefix))=$prefix
                """;
            find.Parameters.AddWithValue("$old", oldRelative);
            find.Parameters.AddWithValue("$prefix", oldRelative.TrimEnd('/') + "/");
            var mappings = new List<(string Path, string Id)>();
            using (var reader = find.ExecuteReader())
                while (reader.Read()) mappings.Add((reader.GetString(0), reader.GetString(1)));

            foreach (var mapping in mappings)
            {
                var suffix = mapping.Path.Length == oldRelative.Length ? "" : mapping.Path[oldRelative.Length..];
                var target = newRelative + suffix;
                using var exists = connection.CreateCommand();
                exists.Transaction = transaction;
                exists.CommandText = "SELECT 1 FROM LibraryFileIds WHERE RelativePath=$path";
                exists.Parameters.AddWithValue("$path", target);
                if (exists.ExecuteScalar() is not null) continue;

                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE LibraryFileIds SET RelativePath=$new,UpdatedUtc=$updated WHERE RelativePath=$old";
                update.Parameters.AddWithValue("$new", target);
                update.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
                update.Parameters.AddWithValue("$old", mapping.Path);
                update.ExecuteNonQuery();

                using var selectedPath = connection.CreateCommand();
                selectedPath.Transaction = transaction;
                selectedPath.CommandText = "UPDATE SelectedLibraryFiles SET FullPath=$new WHERE FullPath=$old";
                selectedPath.Parameters.AddWithValue("$new", Path.GetFullPath(Path.Combine(_root, target.Replace('/', Path.DirectorySeparatorChar))));
                selectedPath.Parameters.AddWithValue("$old", Path.GetFullPath(Path.Combine(_root, mapping.Path.Replace('/', Path.DirectorySeparatorChar))));
                selectedPath.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        catch { }
        finally { Invalidate(); }
    }

    private void OnExternalRenamed(object? sender, RenamedEventArgs e)
    {
        try
        {
            var oldPath = Path.GetFullPath(e.OldFullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var newPath = Path.GetFullPath(e.FullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var prefix = oldPath + Path.DirectorySeparatorChar;
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction();
            var mappings = new List<(string Path, string Id)>();
            using (var find = connection.CreateCommand())
            {
                find.Transaction = transaction;
                find.CommandText = """
                    SELECT FullPath,VideoId FROM ExternalLibraryFiles
                    WHERE FullPath=$old OR substr(FullPath,1,length($prefix))=$prefix
                    """;
                find.Parameters.AddWithValue("$old", oldPath);
                find.Parameters.AddWithValue("$prefix", prefix);
                using var reader = find.ExecuteReader();
                while (reader.Read()) mappings.Add((reader.GetString(0), reader.GetString(1)));
            }

            foreach (var mapping in mappings)
            {
                var suffix = mapping.Path.Length == oldPath.Length ? "" : mapping.Path[oldPath.Length..];
                var target = newPath + suffix;
                using var exists = connection.CreateCommand();
                exists.Transaction = transaction;
                exists.CommandText = "SELECT 1 FROM ExternalLibraryFiles WHERE FullPath=$path";
                exists.Parameters.AddWithValue("$path", target);
                if (exists.ExecuteScalar() is not null) continue;

                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE ExternalLibraryFiles SET FullPath=$new,UpdatedUtc=$utc WHERE FullPath=$old";
                update.Parameters.AddWithValue("$new", target);
                update.Parameters.AddWithValue("$old", mapping.Path);
                update.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
                update.ExecuteNonQuery();

                using var selectedPath = connection.CreateCommand();
                selectedPath.Transaction = transaction;
                selectedPath.CommandText = "UPDATE SelectedLibraryFiles SET FullPath=$new WHERE FullPath=$old";
                selectedPath.Parameters.AddWithValue("$new", target);
                selectedPath.Parameters.AddWithValue("$old", mapping.Path);
                selectedPath.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        catch { }
        finally { Invalidate(); }
    }

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/').TrimStart('/');

    private void Invalidate(object? sender, FileSystemEventArgs e) => Invalidate();
    private void Invalidate()
    {
        lock (_sync) { _videos = null; _resolved = null; }
    }

    public void DeleteFile(string id)
    {
        var video = Resolve(id) ?? throw new FileNotFoundException("ملف الفيديو غير موجود في المكتبة.");
        File.Delete(video.FullPath);
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM LibraryFileIds WHERE VideoId=$id; DELETE FROM ExternalLibraryFiles WHERE VideoId=$id; DELETE FROM ExcludedLibraryFiles WHERE VideoId=$id; DELETE FROM SelectedLibraryFiles WHERE FullPath=$path;";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$path", video.FullPath);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
        Invalidate();
    }

    public void RemoveFromLibrary(string id)
    {
        if (Resolve(id) is null)
            throw new FileNotFoundException("ملف الفيديو غير موجود في المكتبة.");
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO ExcludedLibraryFiles(VideoId,ExcludedUtc) VALUES($id,$utc) ON CONFLICT(VideoId) DO UPDATE SET ExcludedUtc=excluded.ExcludedUtc";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
        Invalidate();
    }

    private static string CreateId(string path)
    {
        var normalized = path.Replace('\\', '/').ToLowerInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..24].ToLowerInvariant();
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers) watcher.Dispose();
    }
}