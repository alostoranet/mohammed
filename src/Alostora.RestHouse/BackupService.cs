using Microsoft.Data.Sqlite;
using System.IO;
using System.IO.Compression;

namespace Alostora.RestHouse;

public static class BackupService
{
    public static void RestoreArchive(string databasePath, string postersDirectory, string archivePath, string applicationRoot)
    {
        if (!File.Exists(archivePath))
            throw new FileNotFoundException("ملف النسخة الاحتياطية غير موجود.", archivePath);
        var archiveInfo = new FileInfo(archivePath);
        if (archiveInfo.Length <= 0 || archiveInfo.Length > 2L * 1024 * 1024 * 1024)
            throw new InvalidDataException("حجم ملف النسخة الاحتياطية غير صالح أو يتجاوز 2 جيجابايت.");

        var tempDirectory = Path.Combine(Path.GetTempPath(), "AlostoraRestHouseRestore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var databaseDirectory = Path.GetDirectoryName(Path.GetFullPath(databasePath))!;
        var postersParent = Path.GetDirectoryName(Path.GetFullPath(postersDirectory))!;
        var stagedDatabase = Path.Combine(databaseDirectory, "resthouse.restore.tmp");
        var rollbackDatabase = Path.Combine(databaseDirectory, "resthouse.pre-restore-" + Guid.NewGuid().ToString("N") + ".tmp");
        var stagedPosters = Path.Combine(postersParent, ".alostora-posters-restore-" + Guid.NewGuid().ToString("N"));
        var oldPosters = Path.Combine(postersParent, ".alostora-posters-old-" + Guid.NewGuid().ToString("N"));
        var databaseReplaced = false;
        var postersMoved = false;
        var newPostersInstalled = false;

        try
        {
            using (var archive = ZipFile.OpenRead(archivePath))
            {
                if (archive.Entries.Count > 100_000 ||
                    archive.Entries.Sum(entry => Math.Max(0, entry.Length)) > 2L * 1024 * 1024 * 1024)
                    throw new InvalidDataException("محتويات النسخة الاحتياطية تتجاوز الحدود المسموحة.");
                foreach (var entry in archive.Entries)
                {
                    var normalized = entry.FullName.Replace('\\', '/');
                    if (normalized.StartsWith('/') || normalized.Split('/').Any(part => part == ".."))
                        throw new InvalidDataException("يحتوي ملف النسخة على مسار غير آمن.");
                }
            }

            ZipFile.ExtractToDirectory(archivePath, tempDirectory);
            var extractedDatabase = Path.Combine(tempDirectory, "resthouse.db");
            if (!File.Exists(extractedDatabase))
                throw new InvalidDataException("النسخة الاحتياطية لا تحتوي قاعدة بيانات الاستراحة.");

            using (var check = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = extractedDatabase,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString()))
            {
                check.Open();
                using var command = check.CreateCommand();
                command.CommandText = "PRAGMA integrity_check;";
                var result = command.ExecuteScalar() as string;
                if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("فشل فحص سلامة قاعدة البيانات داخل النسخة الاحتياطية.");
            }

            Directory.CreateDirectory(databaseDirectory);
            Directory.CreateDirectory(postersParent);
            File.Copy(extractedDatabase, stagedDatabase, overwrite: true);

            var extractedPosters = Path.Combine(tempDirectory, "posters");
            if (Directory.Exists(extractedPosters))
            {
                Directory.CreateDirectory(stagedPosters);
                foreach (var file in Directory.EnumerateFiles(extractedPosters, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(extractedPosters, file);
                    var destination = Path.Combine(stagedPosters, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(file, destination, overwrite: true);
                }
            }

            var backupDirectory = Path.Combine(applicationRoot, "Backups");
            Directory.CreateDirectory(backupDirectory);
            var safetyArchive = Path.Combine(backupDirectory,
                "ALOSTORA-PRE-RESTORE-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".zip");
            if (File.Exists(databasePath))
                CreateArchive(databasePath, postersDirectory, safetyArchive);

            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath))
                File.Move(databasePath, rollbackDatabase, overwrite: true);
            File.Move(stagedDatabase, databasePath, overwrite: true);
            databaseReplaced = true;

            if (Directory.Exists(stagedPosters))
            {
                if (Directory.Exists(postersDirectory))
                {
                    Directory.Move(postersDirectory, oldPosters);
                    postersMoved = true;
                }
                Directory.Move(stagedPosters, postersDirectory);
                newPostersInstalled = true;
            }

            // Remove the rollback copy only after the restored database and posters are installed.
            try { if (File.Exists(rollbackDatabase)) File.Delete(rollbackDatabase); } catch { }
            try { if (Directory.Exists(oldPosters)) Directory.Delete(oldPosters, recursive: true); } catch { }
        }
        catch
        {
            SqliteConnection.ClearAllPools();
            if (databaseReplaced)
            {
                try { if (File.Exists(databasePath)) File.Delete(databasePath); } catch { }
                try { if (File.Exists(rollbackDatabase)) File.Move(rollbackDatabase, databasePath, overwrite: true); } catch { }
            }
            if (newPostersInstalled)
            {
                try { if (Directory.Exists(postersDirectory)) Directory.Delete(postersDirectory, recursive: true); } catch { }
            }
            if (postersMoved)
            {
                try { if (Directory.Exists(oldPosters)) Directory.Move(oldPosters, postersDirectory); } catch { }
            }
            throw;
        }
        finally
        {
            try { if (File.Exists(stagedDatabase)) File.Delete(stagedDatabase); } catch { }
            try { if (Directory.Exists(stagedPosters)) Directory.Delete(stagedPosters, recursive: true); } catch { }
            try { if (Directory.Exists(tempDirectory)) Directory.Delete(tempDirectory, recursive: true); } catch { }
        }
    }

    private static void ValidateArchive(string archivePath)
    {
        var verificationDirectory = Path.Combine(
            Path.GetTempPath(), "AlostoraRestHouseVerify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(verificationDirectory);

        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var databaseEntry = archive.GetEntry("resthouse.db")
                ?? throw new InvalidDataException("النسخة المنشأة لا تحتوي قاعدة البيانات.");
            if (archive.GetEntry("backup-info.txt") is null)
                throw new InvalidDataException("النسخة المنشأة لا تحتوي معلومات النسخة الاحتياطية.");

            // Read every entry to detect damaged compressed poster/image data as well as the database.
            foreach (var entry in archive.Entries)
            {
                using var entryStream = entry.Open();
                entryStream.CopyTo(Stream.Null);
            }

            var verifiedDatabase = Path.Combine(verificationDirectory, "resthouse.db");
            using (var input = databaseEntry.Open())
            using (var output = new FileStream(verifiedDatabase, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                input.CopyTo(output);

            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = verifiedDatabase,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            var result = command.ExecuteScalar() as string;
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("فشل فحص سلامة قاعدة البيانات في النسخة المنشأة.");
        }
        finally
        {
            try { Directory.Delete(verificationDirectory, recursive: true); }
            catch { /* Validation has completed; temporary cleanup must not mask its result. */ }
        }
    }

    public static void CreateArchive(string databasePath, string postersDirectory, string archivePath)
    {
        if (!File.Exists(databasePath))
            throw new FileNotFoundException("قاعدة البيانات غير موجودة.", databasePath);

        var tempDirectory = Path.Combine(Path.GetTempPath(), "AlostoraRestHouseBackup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var backupDatabase = Path.Combine(tempDirectory, "resthouse.db");
            var sourceBuilder = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Shared
            };
            var destinationBuilder = new SqliteConnectionStringBuilder
            {
                DataSource = backupDatabase,
                Mode = SqliteOpenMode.ReadWriteCreate
            };

            using (var source = new SqliteConnection(sourceBuilder.ToString()))
            using (var destination = new SqliteConnection(destinationBuilder.ToString()))
            {
                source.Open();
                destination.Open();
                source.BackupDatabase(destination);
            }

            if (Directory.Exists(postersDirectory))
            {
                var targetPosters = Path.Combine(tempDirectory, "posters");
                Directory.CreateDirectory(targetPosters);
                foreach (var file in Directory.EnumerateFiles(postersDirectory, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(postersDirectory, file);
                    var target = Path.Combine(targetPosters, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target, overwrite: true);
                }
            }

            File.WriteAllText(
                Path.Combine(tempDirectory, "backup-info.txt"),
                "ALOSTORA REST HOUSE backup\nCreated UTC: " + DateTime.UtcNow.ToString("O") +
                "\nContains: SQLite database and poster images. Video files are not included.");

            var archiveDirectory = Path.GetDirectoryName(Path.GetFullPath(archivePath));
            if (string.IsNullOrEmpty(archiveDirectory))
                throw new InvalidOperationException("تعذر تحديد مجلد حفظ النسخة الاحتياطية.");
            Directory.CreateDirectory(archiveDirectory);

            // Build beside the destination so publishing the completed archive is an atomic rename.
            var temporaryArchive = Path.Combine(
                archiveDirectory,
                "." + Path.GetFileName(archivePath) + "." + Guid.NewGuid().ToString("N") + ".tmp");

            try
            {
                ZipFile.CreateFromDirectory(tempDirectory, temporaryArchive, CompressionLevel.Optimal, includeBaseDirectory: false);
                ValidateArchive(temporaryArchive);
                File.Move(temporaryArchive, archivePath, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temporaryArchive)) File.Delete(temporaryArchive); }
                catch { /* Preserve the previous valid archive if cleanup is unavailable. */ }
            }
        }
        finally
        {
            try { Directory.Delete(tempDirectory, recursive: true); }
            catch { /* لا نُفشل النسخة الاحتياطية بسبب تعذر حذف الملفات المؤقتة. */ }
        }
    }
}
