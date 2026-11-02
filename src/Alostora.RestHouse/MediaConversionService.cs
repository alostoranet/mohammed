using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Alostora.RestHouse;

public sealed record AudioTrackInfo(int Index, string Language, string Title, string Codec, bool IsDefault);
public sealed record EmbeddedSubtitleTrackInfo(int Index, string Language, string Title, string Codec, bool IsDefault);
public sealed record MediaProbeResult(bool Available, bool Success, string Message, bool NeedsConversion,
    IReadOnlyList<AudioTrackInfo> AudioTracks, IReadOnlyList<EmbeddedSubtitleTrackInfo> EmbeddedSubtitles, string? VideoCodec, string? VideoPixelFormat);
public sealed record MediaToolStatus(bool Available, string FfmpegPath, string FfprobePath, string Message);
public sealed record ConversionJobStatus(string JobId, string State, string Message, string? Url);

/// <summary>
/// Optional FFmpeg bridge for browser-incompatible containers/codecs and audio-track selection.
/// Original media files are never modified.
/// </summary>
public sealed class MediaConversionService
{
    private sealed class Job
    {
        public string State = "queued";
        public string Message = "بانتظار التحويل...";
        public string? Error;
    }

    private readonly LibraryService _library;
    private readonly string? _configuredFfmpeg;
    private readonly string _cacheDirectory;
    private readonly ConcurrentDictionary<string, Job> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _conversionSlots = new(2, 2);

    public MediaConversionService(LibraryService library, string? configuredFfmpeg, string cacheDirectory)
    {
        _library = library;
        _configuredFfmpeg = string.IsNullOrWhiteSpace(configuredFfmpeg) ? null : Path.GetFullPath(configuredFfmpeg);
        _cacheDirectory = Path.GetFullPath(cacheDirectory);
        Directory.CreateDirectory(_cacheDirectory);
        PruneCache();
    }

    public MediaToolStatus GetToolStatus()
    {
        var ffmpeg = ResolveTool(_configuredFfmpeg, "ffmpeg.exe", "ffmpeg");
        var ffprobeCandidate = ffmpeg is null ? null : Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe");
        var ffprobe = ffprobeCandidate is not null && File.Exists(ffprobeCandidate)
            ? ffprobeCandidate
            : ResolveTool(null, "ffprobe.exe", "ffprobe");
        var available = ffmpeg is not null && ffprobe is not null;
        return new MediaToolStatus(available, ffmpeg ?? "", ffprobe ?? "",
            available ? "FFmpeg وFFprobe جاهزان." :
            "لم يتم العثور على FFmpeg وFFprobe. اختر ملف ffmpeg.exe من لوحة الإدارة، وضع ffprobe.exe بجانبه.");
    }

    public async Task<MediaProbeResult> ProbeAsync(string videoId)
    {
        var video = _library.Resolve(videoId);
        if (video is null)
            return new(false, false, "ملف الفيديو غير موجود في المكتبة.", false, Array.Empty<AudioTrackInfo>(), Array.Empty<EmbeddedSubtitleTrackInfo>(), null, null);

        var tools = GetToolStatus();
        if (!tools.Available)
            return new(false, false, tools.Message, NeedsConversion(video.FullPath), Array.Empty<AudioTrackInfo>(), Array.Empty<EmbeddedSubtitleTrackInfo>(), null, null);

        try
        {
            var start = new ProcessStartInfo
            {
                FileName = tools.FfprobePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var arg in new[] { "-v", "error", "-show_entries",
                "stream=index,codec_type,codec_name,pix_fmt:stream_tags=language,title:stream_disposition=default",
                "-of", "json", video.FullPath })
                start.ArgumentList.Add(arg);

            using var process = Process.Start(start);
            if (process is null)
                return new(true, false, "تعذر تشغيل FFprobe.", NeedsConversion(video.FullPath), Array.Empty<AudioTrackInfo>(), Array.Empty<EmbeddedSubtitleTrackInfo>(), null, null);

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { }
                return new(true, false, "انتهت مهلة فحص ملف الفيديو.", NeedsConversion(video.FullPath), Array.Empty<AudioTrackInfo>(), Array.Empty<EmbeddedSubtitleTrackInfo>(), null, null);
            }

            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0)
                return new(true, false, string.IsNullOrWhiteSpace(error) ? "تعذر قراءة مسارات الفيديو." : error.Trim(),
                    NeedsConversion(video.FullPath), Array.Empty<AudioTrackInfo>(), Array.Empty<EmbeddedSubtitleTrackInfo>(), null, null);

            using var document = JsonDocument.Parse(output);
            var tracks = new List<AudioTrackInfo>();
            var embeddedSubtitles = new List<EmbeddedSubtitleTrackInfo>();
            string? videoCodec = null;
            string? videoPixelFormat = null;
            if (document.RootElement.TryGetProperty("streams", out var streams))
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    var type = GetString(stream, "codec_type");
                    var index = stream.TryGetProperty("index", out var indexValue) && indexValue.TryGetInt32(out var parsedIndex) ? parsedIndex : -1;
                    var codec = GetString(stream, "codec_name") ?? "unknown";
                    if (type == "video" && videoCodec is null)
                    {
                        videoCodec = codec;
                        videoPixelFormat = GetString(stream, "pix_fmt");
                    }
                    var language = "und";
                    var title = "";
                    if (stream.TryGetProperty("tags", out var tags))
                    {
                        language = GetString(tags, "language") ?? language;
                        title = GetString(tags, "title") ?? "";
                    }
                    var isDefault = stream.TryGetProperty("disposition", out var disposition) &&
                        disposition.TryGetProperty("default", out var defaultValue) &&
                        defaultValue.ValueKind == JsonValueKind.Number && defaultValue.GetInt32() == 1;
                    if (type == "audio" && index >= 0)
                        tracks.Add(new AudioTrackInfo(index, language, title, codec, isDefault));
                    else if (type == "subtitle" && index >= 0 &&
                        codec is "subrip" or "ass" or "ssa" or "mov_text" or "webvtt" or "text")
                        embeddedSubtitles.Add(new EmbeddedSubtitleTrackInfo(index, language, title, codec, isDefault));
                }
            }

            return new(true, true, "تم فحص مسارات الملف.", NeedsConversion(video.FullPath), tracks, embeddedSubtitles, videoCodec, videoPixelFormat);
        }
        catch (Exception ex)
        {
            return new(true, false, "فشل فحص الفيديو: " + ex.Message, NeedsConversion(video.FullPath),
                Array.Empty<AudioTrackInfo>(), Array.Empty<EmbeddedSubtitleTrackInfo>(), null, null);
        }
    }

    public async Task<string?> ReadEmbeddedSubtitleAsync(string videoId, int streamIndex)
    {
        if (streamIndex < 0) return null;
        var video = _library.Resolve(videoId);
        if (video is null) return null;
        var probe = await ProbeAsync(videoId);
        var track = probe.EmbeddedSubtitles.FirstOrDefault(x => x.Index == streamIndex);
        if (track is null) return null;
        var tools = GetToolStatus();
        if (!tools.Available) return null;

        try
        {
            var start = new ProcessStartInfo
            {
                FileName = tools.FfmpegPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                CreateNoWindow = true
            };
            foreach (var arg in new[] { "-nostdin", "-hide_banner", "-loglevel", "error",
                "-i", video.FullPath, "-map", "0:" + streamIndex, "-c:s", "webvtt",
                "-f", "webvtt", "pipe:1" })
                start.ArgumentList.Add(arg);

            using var process = Process.Start(start);
            if (process is null) return null;
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { }
                return null;
            }
            var output = await outputTask;
            _ = await errorTask;
            if (process.ExitCode != 0 || output.Length == 0 || output.Length > 10 * 1024 * 1024)
                return null;
            var clean = output.TrimStart('\uFEFF');
            return clean.StartsWith("WEBVTT", StringComparison.Ordinal) ? clean : "WEBVTT\n\n" + clean;
        }
        catch { return null; }
    }

    public async Task<ConversionJobStatus> StartConversionAsync(string videoId, int? audioStreamIndex)
    {
        var video = _library.Resolve(videoId);
        if (video is null) return new("", "failed", "ملف الفيديو غير موجود.", null);

        var probe = await ProbeAsync(videoId);
        if (!probe.Available)
            return new("", "failed", probe.Message, null);
        if (!probe.Success)
            return new("", "failed", probe.Message, null);

        AudioTrackInfo? selectedAudio = null;
        if (audioStreamIndex is not null)
        {
            selectedAudio = probe.AudioTracks.FirstOrDefault(x => x.Index == audioStreamIndex.Value);
            if (selectedAudio is null)
                return new("", "failed", "مسار الصوت المحدد غير موجود في هذا الملف.", null);
        }
        else
        {
            selectedAudio = probe.AudioTracks.FirstOrDefault(x => x.IsDefault) ?? probe.AudioTracks.FirstOrDefault();
        }

        var info = new FileInfo(video.FullPath);
        var keySource = string.Join("|", videoId, info.Length, info.LastWriteTimeUtc.Ticks,
            selectedAudio?.Index.ToString() ?? "no-audio", probe.VideoCodec ?? "unknown");
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keySource))).ToLowerInvariant()[..24];
        var output = Path.Combine(_cacheDirectory, key + ".mp4");
        if (File.Exists(output) && new FileInfo(output).Length > 0)
            return new ConversionJobStatus(key, "ready", "الملف المحوّل جاهز.", "/stream-cache/" + key + ".mp4");

        var job = _jobs.GetOrAdd(key, _ => new Job());
        var shouldStart = false;
        lock (job)
        {
            if (job.State is "queued" or "failed")
            {
                job.State = "running";
                job.Message = "جارٍ تجهيز الفيديو للتشغيل...";
                job.Error = null;
                shouldStart = true;
            }
        }
        if (shouldStart)
            _ = Task.Run(() => RunConversionAsync(key, video.FullPath, output, probe.VideoCodec, probe.VideoPixelFormat, selectedAudio));

        return new ConversionJobStatus(key, job.State, job.Message, null);
    }

    public ConversionJobStatus GetJob(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId) || jobId.Length != 24 || !jobId.All(Uri.IsHexDigit))
            return new ConversionJobStatus("", "failed", "معرّف التحويل غير صالح.", null);

        var output = Path.Combine(_cacheDirectory, jobId.ToLowerInvariant() + ".mp4");
        if (File.Exists(output) && new FileInfo(output).Length > 0)
            return new ConversionJobStatus(jobId, "ready", "الفيديو جاهز للمشاهدة.", "/stream-cache/" + jobId + ".mp4");

        if (!_jobs.TryGetValue(jobId, out var job))
            return new ConversionJobStatus(jobId, "missing", "لم يتم العثور على مهمة التحويل. أعد طلب التشغيل.", null);

        return new ConversionJobStatus(jobId, job.State, job.Error ?? job.Message, null);
    }

    public string? ResolveCachedFile(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId) || jobId.Length != 24 || !jobId.All(Uri.IsHexDigit))
            return null;
        var path = Path.Combine(_cacheDirectory, jobId.ToLowerInvariant() + ".mp4");
        return File.Exists(path) ? path : null;
    }

    private async Task RunConversionAsync(string jobId, string input, string output, string? videoCodec, string? videoPixelFormat, AudioTrackInfo? audio)
    {
        var job = _jobs[jobId];
        var tools = GetToolStatus();
        if (!tools.Available)
        {
            job.State = "failed";
            job.Error = tools.Message;
            return;
        }

        var temporary = output + ".partial";
        var acquired = false;
        try
        {
            await _conversionSlots.WaitAsync();
            acquired = true;
            if (File.Exists(temporary)) File.Delete(temporary);
            var start = new ProcessStartInfo
            {
                FileName = tools.FfmpegPath,
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            var args = new List<string> { "-y", "-hide_banner", "-loglevel", "error", "-i", input,
                "-map", "0:v:0?" };
            if (audio is null)
                args.AddRange(new[] { "-an" });
            else
                args.AddRange(new[] { "-map", "0:" + audio.Index });
            args.AddRange(new[] { "-map_metadata", "0" });

            if (string.Equals(videoCodec, "h264", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(videoPixelFormat, "yuv420p", StringComparison.OrdinalIgnoreCase))
                args.AddRange(new[] { "-c:v", "copy" });
            else
                args.AddRange(new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-pix_fmt", "yuv420p" });

            if (audio is not null)
                args.AddRange(new[] { "-c:a", "aac", "-b:a", "192k", "-ac", "2" });
            args.AddRange(new[] { "-movflags", "+faststart", "-max_muxing_queue_size", "2048", "-f", "mp4", temporary });
            foreach (var arg in args) start.ArgumentList.Add(arg);

            using var process = Process.Start(start);
            if (process is null) throw new InvalidOperationException("تعذر تشغيل FFmpeg.");
            var errorTask = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromHours(12));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { }
                throw new TimeoutException("تجاوز التحويل الحد الزمني المسموح.");
            }
            var error = await errorTask;
            if (process.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "فشل FFmpeg في تحويل الفيديو." : error.Trim());
            if (!File.Exists(temporary) || new FileInfo(temporary).Length < 1024)
                throw new InvalidOperationException("لم ينتج FFmpeg ملف فيديو صالحاً.");
            File.Move(temporary, output, overwrite: true);
            job.State = "ready";
            job.Message = "الفيديو جاهز للمشاهدة.";
        }
        catch (Exception ex)
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            job.State = "failed";
            job.Error = ex.Message;
        }
        finally
        {
            if (acquired) _conversionSlots.Release();
        }
    }

    private static bool NeedsConversion(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".mkv" or ".avi" or ".mov" or ".ts" or ".m2ts" or ".mts" or ".flv" or ".wmv" or ".mpg" or ".mpeg" or ".3gp";

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? ResolveTool(string? configuredPath, string executableName, string pathName)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
            return configuredPath;
        var local = Path.Combine(AppContext.BaseDirectory, executableName);
        if (File.Exists(local)) return local;
        var tools = Path.Combine(AppContext.BaseDirectory, "tools", executableName);
        if (File.Exists(tools)) return tools;

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var folder in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(folder.Trim(), executableName);
                if (File.Exists(candidate)) return candidate;
                if (!executableName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    candidate = Path.Combine(folder.Trim(), pathName);
                    if (File.Exists(candidate)) return candidate;
                }
            }
            catch { }
        }
        return null;
    }

    private void PruneCache()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_cacheDirectory, "*.mp4"))
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-5))
                    File.Delete(file);
            foreach (var file in Directory.EnumerateFiles(_cacheDirectory, "*.partial"))
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddHours(-12))
                    File.Delete(file);

            const long maximumCacheBytes = 20L * 1024 * 1024 * 1024;
            var cached = new DirectoryInfo(_cacheDirectory).GetFiles("*.mp4")
                .OrderBy(file => file.LastWriteTimeUtc).ToArray();
            var total = cached.Sum(file => file.Length);
            foreach (var file in cached)
            {
                if (total <= maximumCacheBytes) break;
                var length = file.Length;
                file.Delete();
                total -= length;
            }
        }
        catch { }
    }
}
