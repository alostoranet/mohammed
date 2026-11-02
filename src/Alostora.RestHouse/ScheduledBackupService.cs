using System.IO;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Alostora.RestHouse;

/// <summary>
/// Creates a daily database-and-posters archive at 02:00 local time.
/// Video files are intentionally excluded; keep the media library separately backed up.
/// </summary>
public sealed class ScheduledBackupService : BackgroundService
{
    private const int RetentionCount = 14;
    private readonly string _root;
    private readonly string _postersPath;
    private readonly ILogger<ScheduledBackupService> _logger;

    public ScheduledBackupService(string root, string postersPath, ILogger<ScheduledBackupService> logger)
    {
        _root = root;
        _postersPath = postersPath;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.Now;
            var nextRun = now.Date.AddDays(1).AddHours(2);
            if (now < now.Date.AddHours(2))
                nextRun = now.Date.AddHours(2);

            var delay = nextRun - now;
            try
            {
                await Task.Delay(delay, stoppingToken);
                if (stoppingToken.IsCancellationRequested)
                    break;

                await CreateDailyBackupAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ALOSTORA scheduled backup cycle failed.");
            }
        }
    }

    private async Task CreateDailyBackupAsync(CancellationToken cancellationToken)
    {
        var databasePath = Path.Combine(_root, "resthouse.db");
        var postersPath = _postersPath;
        var backupDirectory = Path.Combine(_root, "Backups");
        Directory.CreateDirectory(backupDirectory);

        if (!File.Exists(databasePath))
        {
            _logger.LogWarning("Scheduled backup skipped because the database does not exist yet.");
            return;
        }

        var archivePath = Path.Combine(
            backupDirectory,
            "ALOSTORA-REST-HOUSE-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip");

        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            BackupService.CreateArchive(databasePath, postersPath, archivePath);
        }, cancellationToken);

        _logger.LogInformation("Scheduled backup created: {ArchivePath}", archivePath);
        PruneOldBackups(backupDirectory);
    }

    private void PruneOldBackups(string directory)
    {
        try
        {
            var archives = new DirectoryInfo(directory)
                .GetFiles("ALOSTORA-REST-HOUSE-*.zip", SearchOption.TopDirectoryOnly)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToArray();

            foreach (var oldArchive in archives.Skip(RetentionCount))
            {
                try
                {
                    oldArchive.Delete();
                    _logger.LogInformation("Removed expired backup: {ArchivePath}", oldArchive.FullName);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not remove expired backup {ArchivePath}", oldArchive.FullName);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not apply scheduled-backup retention policy.");
        }
    }
}
