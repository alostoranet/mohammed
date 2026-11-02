using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Diagnostics;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;

namespace Alostora.RestHouse;

public partial class MainWindow : Window
{
    private readonly string _root;
    private string _library;
    private string _posters;
    private string? _ffmpegPath;
    private readonly HttpClient _http = new() { BaseAddress = new Uri("http://127.0.0.1:8096") };
    private readonly ObservableCollection<AdminVideo> _videos = new();
    private readonly ObservableCollection<EpisodeCandidate> _episodeCandidates = new();

    public MainWindow(string root, string library, string posters, string? ffmpegPath)
    {
        InitializeComponent();
        _root = root;
        _library = library;
        _posters = posters;
        _ffmpegPath = ffmpegPath;
        FfmpegPathBox.Text = ffmpegPath ?? "";
        LibraryBox.Text = library;
        MediaFolderBox.Text = library;
        PostersFolderBox.Text = posters;
        AddressBox.Text = $"رابط المشاهدة: http://{GetLanAddress()}:8096";
        VideosGrid.ItemsSource = _videos;
        EpisodeCandidatesGrid.ItemsSource = _episodeCandidates;
        Loaded += async (_, _) => { await LoadVideosAsync(); await LoadSeriesAsync(); await LoadMissingEpisodesAsync(); await CheckMediaToolsAsync(); };
    }

    private async void RefreshLibrary_Click(object sender, RoutedEventArgs e)
    {
        await LoadVideosAsync();
        await LoadMissingEpisodesAsync();
    }

    private async void AddExternalMediaFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "اختر مجلداً إضافياً يحتوي أفلاماً أو حلقات فيديو",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var folderPath = Path.GetFullPath(dialog.FolderName);
            StatusText.Text = "جارٍ فحص المجلد وعرض ملفات الفيديو للاختيار...";
            var previewUrl = "/api/admin/preview-folder?folderPath=" + Uri.EscapeDataString(folderPath);
            var previewResponse = await _http.GetAsync(previewUrl);
            if (!previewResponse.IsSuccessStatusCode)
            {
                StatusText.Text = "تعذر فحص المجلد. تحقق من وجوده وصلاحية الوصول إليه.";
                return;
            }

            var candidates = await previewResponse.Content.ReadFromJsonAsync<VideoFileCandidateAdmin[]>() ?? Array.Empty<VideoFileCandidateAdmin>();
            if (candidates.Length == 0)
            {
                StatusText.Text = "لم يتم العثور على ملفات فيديو مدعومة داخل المجلد المحدد.";
                return;
            }

            var selection = new FolderVideoSelectionWindow(folderPath, candidates);
            if (selection.ShowDialog() != true)
            {
                StatusText.Text = "تم إلغاء إضافة المجلد. لم تتم إضافة أي ملفات جديدة.";
                return;
            }

            var selectedPaths = selection.SelectedPaths;
            if (selectedPaths.Length == 0)
            {
                StatusText.Text = "لم تحدد أي فيديو للإضافة.";
                return;
            }

            StatusText.Text = "جارٍ إضافة ملفات الفيديو التي حددتها فقط...";
            var response = await _http.PostAsJsonAsync("/api/admin/register-selected-files",
                new { folderPath, filePaths = selectedPaths });
            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync();
                StatusText.Text = "تعذرت إضافة الملفات المحددة: " + detail;
                return;
            }

            await LoadVideosAsync();
            await LoadMissingEpisodesAsync();
            StatusText.Text = $"تمت إضافة {selectedPaths.Length} ملف فيديو من أصل {candidates.Length} ملفاً موجوداً في المجلد.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "تعذر إضافة ملفات الفيديو: " + ex.Message;
        }
    }

    private void ChooseMediaFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "اختر المجلد الرئيسي الذي يحتوي ملفات الأفلام والمسلسلات",
            InitialDirectory = Directory.Exists(_library) ? _library : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
        };
        if (dialog.ShowDialog(this) != true) return;
        _library = Path.GetFullPath(dialog.FolderName);
        SaveFolderSettings();
    }

    private void ChoosePostersFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "اختر المجلد الذي يحتوي صور أغلفة الأفلام والمسلسلات",
            InitialDirectory = Directory.Exists(_posters) ? _posters : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
        };
        if (dialog.ShowDialog(this) != true) return;
        _posters = Path.GetFullPath(dialog.FolderName);
        SaveFolderSettings();
    }

    private void ChooseSeriesFolder_Click(object sender, RoutedEventArgs e)
    {
        var initial = Directory.Exists(SeriesFolderPathBox.Text) ? SeriesFolderPathBox.Text : _library;
        var dialog = new OpenFolderDialog { Title = "اختر المجلد الذي يحتوي ملفات هذا المسلسل", InitialDirectory = Directory.Exists(initial) ? initial : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos) };
        if (dialog.ShowDialog(this) == true) SeriesFolderPathBox.Text = Path.GetFullPath(dialog.FolderName);
    }

    private void ChooseSeasonFolder_Click(object sender, RoutedEventArgs e)
    {
        var initial = Directory.Exists(SeasonFolderPathBox.Text) ? SeasonFolderPathBox.Text : (Directory.Exists(SeriesFolderPathBox.Text) ? SeriesFolderPathBox.Text : _library);
        var dialog = new OpenFolderDialog { Title = "اختر المجلد الذي يحتوي حلقات هذا الموسم", InitialDirectory = Directory.Exists(initial) ? initial : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos) };
        if (dialog.ShowDialog(this) == true) SeasonFolderPathBox.Text = Path.GetFullPath(dialog.FolderName);
    }

    private void ChooseFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "اختر ملف ffmpeg.exe",
            Filter = "FFmpeg executable|ffmpeg.exe|All executable files|*.exe",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
            FfmpegPathBox.Text = Path.GetFullPath(dialog.FileName);
    }

    private void SaveFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        var selected = FfmpegPathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(selected))
        {
            MessageBox.Show("اختر ملف ffmpeg.exe أولاً.", "إعداد محرك الفيديو", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        selected = Path.GetFullPath(selected);
        if (!File.Exists(selected) || !string.Equals(Path.GetFileName(selected), "ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("المسار يجب أن يشير إلى ملف ffmpeg.exe موجود.", "إعداد محرك الفيديو", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var ffprobe = Path.Combine(Path.GetDirectoryName(selected)!, "ffprobe.exe");
        try
        {
            AppSettings.SaveFfmpegPath(_root, selected);
            _ffmpegPath = selected;
            FfmpegStatusText.Text = File.Exists(ffprobe)
                ? "تم حفظ المسار. وجدنا ffprobe.exe أيضاً. أعد تشغيل البرنامج لتفعيل تحويل الفيديو واختيار الصوت."
                : "تم حفظ المسار، لكن لم نجد ffprobe.exe بجانب FFmpeg. ضع الملفين في المجلد نفسه ثم أعد تشغيل البرنامج.";
            MessageBox.Show(FfmpegStatusText.Text, "إعداد محرك الفيديو", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("تعذر حفظ إعداد FFmpeg: " + ex.Message, "إعداد محرك الفيديو", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveFolderSettings()
    {
        try
        {
            if (!Directory.Exists(_library) || !Directory.Exists(_posters))
                throw new DirectoryNotFoundException("تأكد أن مجلدي الفيديو والأغلفة موجودان ويمكن الوصول إليهما.");
            AppSettings.Save(_root, _library, _posters);
            MediaFolderBox.Text = _library;
            PostersFolderBox.Text = _posters;
            LibraryBox.Text = _library;
            MessageBox.Show(
                "تم حفظ مسارات المجلدات. لكي يبدأ البرنامج باستخدامها، أغلق ALOSTORA REST HOUSE وافتحه مرة أخرى.\n\nملاحظة: لا يتم نقل ملفات الفيديو أو الصور؛ البرنامج يقرأها من المجلدات التي اخترتها.",
                "تحديث مسارات المكتبة",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("تعذر حفظ مسارات المجلدات: " + ex.Message,
                "مسارات المكتبة", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void EnableFirewall_Click(object sender, RoutedEventArgs e)
    {
        const string ruleName = "ALOSTORA REST HOUSE TCP 8096";
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"$ErrorActionPreference='Stop'; try { Get-NetFirewallRule -DisplayName '" + ruleName + "' -ErrorAction SilentlyContinue | Remove-NetFirewallRule; New-NetFirewallRule -DisplayName '" + ruleName + "' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 8096 -Profile Private | Out-Null; exit 0 } catch { Write-Error $_; exit 1 }\"",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                MessageBox.Show("تعذر بدء إعداد جدار حماية Windows.", "إعداد الشبكة", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            await process.WaitForExitAsync();
            if (process.ExitCode == 0)
                MessageBox.Show("تم السماح باتصالات TCP على المنفذ 8096 ضمن ملف تعريف الشبكة الخاصة. تأكد أن شبكة Windows مضبوطة على «خاصة».", "إعداد الشبكة", MessageBoxButton.OK, MessageBoxImage.Information);
            else
                MessageBox.Show("لم يتمكن Windows من إضافة قاعدة جدار الحماية. شغّل البرنامج بصلاحية مسؤول وحاول مجدداً.", "إعداد الشبكة", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            MessageBox.Show("تم إلغاء طلب صلاحية المسؤول. لم تتغير إعدادات جدار الحماية.", "إعداد الشبكة", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("تعذر إعداد جدار الحماية: " + ex.Message, "إعداد الشبكة", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void CreateBackup_Click(object sender, RoutedEventArgs e)
    {
        var databasePath = Path.Combine(_root, "resthouse.db");
        var postersPath = _posters;
        var dialog = new SaveFileDialog
        {
            Title = "حفظ نسخة احتياطية من استراحة الأسطورة",
            Filter = "ملف نسخة احتياطية مضغوط|*.zip",
            DefaultExt = ".zip",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = "ALOSTORA-REST-HOUSE-Backup-" + DateTime.Now.ToString("yyyyMMdd-HHmm")
        };

        if (dialog.ShowDialog(this) != true) return;
        try
        {
            System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
            await System.Threading.Tasks.Task.Run(() =>
                BackupService.CreateArchive(databasePath, postersPath, dialog.FileName));
            MessageBox.Show(
                "تم إنشاء النسخة الاحتياطية بنجاح.\n\nتتضمن قاعدة البيانات والأغلفة، ولا تتضمن ملفات الفيديو.",
                "النسخ الاحتياطي",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "تعذر إنشاء النسخة الاحتياطية:\n" + ex.Message,
                "النسخ الاحتياطي",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            System.Windows.Input.Mouse.OverrideCursor = null;
        }
    }


    private async void RestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "اختر نسخة احتياطية لاستعادة المكتبة",
            Filter = "نسخ استراحة الأسطورة|*.zip",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;

        var confirm = MessageBox.Show(
            "سيتم استبدال قاعدة بيانات المكتبة والأغلفة بمحتويات النسخة المحددة.\n" +
            "سيُنشئ البرنامج نسخة أمان من البيانات الحالية قبل الاستعادة.\n\n" +
            "بعد نجاح الاستعادة أغلق البرنامج وافتحه مرة أخرى. هل تريد المتابعة؟",
            "تأكيد استعادة النسخة الاحتياطية",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
            await System.Threading.Tasks.Task.Run(() => BackupService.RestoreArchive(
                Path.Combine(_root, "resthouse.db"), _posters, dialog.FileName, _root));
            MessageBox.Show(
                "تمت استعادة النسخة الاحتياطية وفحص قاعدة البيانات بنجاح.\n" +
                "أغلق ALOSTORA REST HOUSE وافتحه مرة أخرى لتحديث بيانات المكتبة.",
                "استعادة النسخة الاحتياطية",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "لم تكتمل الاستعادة. لم يتم تجاهل الخطأ، وحاول البرنامج إعادة البيانات السابقة عند حدوث مشكلة:\n\n" + ex.Message,
                "تعذر استعادة النسخة",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            System.Windows.Input.Mouse.OverrideCursor = null;
        }
    }

    private async void RemoveVideoFromLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (VideosGrid.SelectedItem is not AdminVideo video)
        {
            StatusText.Text = "حدد ملف فيديو من القائمة أولاً.";
            return;
        }
        var confirm = MessageBox.Show(
            $"سيتم إخفاء الفيديو من مكتبة المشاهدة فقط، ولن يُحذف الملف الأصلي من القرص. يمكنك إضافته مجدداً لاحقاً.\n\nالملف: {video.Title}\n\nهل تريد المتابعة؟",
            "إزالة من المكتبة فقط", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;
        try
        {
            var response = await _http.PostAsync("/api/admin/library/" + Uri.EscapeDataString(video.Id) + "/remove", null);
            if (!response.IsSuccessStatusCode)
            {
                StatusText.Text = "تعذرت إزالة الفيديو من المكتبة: " + await response.Content.ReadAsStringAsync();
                return;
            }
            VideosGrid.SelectedItem = null;
            await LoadVideosAsync();
            StatusText.Text = "تمت إزالة الفيديو من المكتبة، والملف الأصلي ما زال محفوظاً في مكانه.";
        }
        catch (Exception ex) { StatusText.Text = "تعذرت إزالة الفيديو: " + ex.Message; }
    }

    private async void DeleteVideo_Click(object sender, RoutedEventArgs e)
    {
        if (VideosGrid.SelectedItem is not AdminVideo video)
        {
            StatusText.Text = "حدد ملف فيديو من القائمة أولاً.";
            return;
        }

        var confirm = MessageBox.Show(
            $"تحذير: سيتم حذف ملف الفيديو الأصلي نهائياً من القرص ولا يمكن التراجع عن ذلك.\n\nالملف: {video.Title}\n\nسيتم حذف بياناته وسجل المشاهدة أيضاً. إذا كان مربوطاً بحلقة مسلسل فستبقى الحلقة مسجلة كملف مفقود ويمكن إعادة ربطها لاحقاً.\n\nهل أنت متأكد؟",
            "تأكيد حذف ملف الفيديو نهائياً",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            StatusText.Text = "جارٍ حذف ملف الفيديو من الجهاز...";
            var response = await _http.DeleteAsync("/api/admin/library/" + Uri.EscapeDataString(video.Id));
            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync();
                StatusText.Text = "تعذر حذف الفيديو: " + detail;
                return;
            }

            VideosGrid.SelectedItem = null;
            IdBox.Clear(); TitleBox.Clear(); DescriptionBox.Clear(); YearBox.Clear(); GenreBox.Clear();
            DirectorBox.Clear(); CastBox.Clear(); RatingBox.Clear(); RuntimeBox.Clear(); ReleaseDateBox.Clear(); TagsBox.Clear();
            await LoadVideosAsync();
            await LoadMissingEpisodesAsync();
            StatusText.Text = "تم حذف ملف الفيديو نهائياً من القرص وتحديث المكتبة.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "تعذر حذف الفيديو: " + ex.Message;
        }
    }

    private async System.Threading.Tasks.Task LoadVideosAsync()
    {
        try
        {
            StatusText.Text = "جارٍ تحديث القائمة...";
            var items = await _http.GetFromJsonAsync<AdminVideo[]>("/api/library") ?? Array.Empty<AdminVideo>();
            _videos.Clear();
            foreach (var item in items) _videos.Add(item);
            EpisodeVideoPicker.ItemsSource = _videos;
            CountText.Text = $"عدد ملفات الفيديو: {_videos.Count}";
            StatusText.Text = "تم تحديث القائمة. اختر فيديو لتعديل بياناته.";
        }
        catch (Exception ex) { StatusText.Text = "تعذر تحميل المكتبة: " + ex.Message; }
    }

    private async System.Threading.Tasks.Task CheckMediaToolsAsync()
    {
        try
        {
            var status = await _http.GetFromJsonAsync<MediaToolStatus>("/api/media-tools");
            if (status is null) return;
            FfmpegStatusText.Text = status.Available
                ? "محرك التحويل جاهز. يمكن تشغيل MKV وAVI واختيار المسار الصوتي. " + status.FfmpegPath
                : status.Message;
        }
        catch (Exception ex)
        {
            FfmpegStatusText.Text = "تعذر فحص محرك التحويل: " + ex.Message;
        }
    }

    private void VideosGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VideosGrid.SelectedItem is not AdminVideo v) return;
        IdBox.Text = v.Id;
        TitleBox.Text = v.Title ?? "";
        DescriptionBox.Text = v.Description ?? "";
        YearBox.Text = v.Year?.ToString() ?? "";
        GenreBox.Text = v.Genre ?? "";
        DirectorBox.Text = v.Director ?? "";
        CastBox.Text = v.Cast ?? "";
        RatingBox.Text = v.Rating?.ToString() ?? "";
        RuntimeBox.Text = v.RuntimeMinutes?.ToString() ?? "";
        ReleaseDateBox.Text = v.ReleaseDate ?? "";
        TagsBox.Text = v.Tags ?? "";
        KindBox.SelectedIndex = string.Equals(v.Kind, "Series", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        PosterStatusText.Text = string.IsNullOrWhiteSpace(v.PosterPath) ? "لا يوجد غلاف لهذا العمل حالياً." : "يوجد غلاف محفوظ لهذا العمل.";
    }

    private async void UploadPoster_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(IdBox.Text)) { StatusText.Text = "اختر فيديو أولاً."; return; }
        var dialog = new OpenFileDialog { Title = "اختر غلاف الفيلم أو المسلسل", Filter = "صور مدعومة|*.jpg;*.jpeg;*.png;*.webp", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        var ext = Path.GetExtension(dialog.FileName).ToLowerInvariant();
        var contentType = ext switch { ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", ".webp" => "image/webp", _ => "" };
        var info = new FileInfo(dialog.FileName);
        if (contentType.Length == 0 || info.Length == 0 || info.Length > 5_242_880)
        { StatusText.Text = "اختر صورة JPG أو PNG أو WEBP لا يتجاوز حجمها 5 ميجابايت."; return; }
        try
        {
            StatusText.Text = "جارٍ رفع الغلاف...";
            using var bytes = new ByteArrayContent(await File.ReadAllBytesAsync(dialog.FileName));
            bytes.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            var response = await _http.PostAsync("/api/admin/poster/" + Uri.EscapeDataString(IdBox.Text), bytes);
            if (!response.IsSuccessStatusCode)
            { StatusText.Text = "تعذر رفع الغلاف. تأكد من صيغة الصورة وحجمها."; return; }
            var result = await response.Content.ReadFromJsonAsync<PosterUploadResult>();
            PosterStatusText.Text = "تم رفع الغلاف وحفظه: " + (result?.PosterPath ?? "");
            StatusText.Text = "تم رفع الغلاف بنجاح.";
            await LoadVideosAsync();
            VideosGrid.SelectedItem = _videos.FirstOrDefault(v => v.Id == IdBox.Text);
        }
        catch (Exception ex) { StatusText.Text = "تعذر رفع الغلاف: " + ex.Message; }
    }

    private async void SaveMetadata_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(IdBox.Text)) { StatusText.Text = "اختر فيديو أولاً."; return; }
        int? year = null;
        if (!string.IsNullOrWhiteSpace(YearBox.Text))
        {
            if (!int.TryParse(YearBox.Text, out var parsed)) { StatusText.Text = "السنة يجب أن تكون رقماً صحيحاً."; return; }
            year = parsed;
        }
        double? rating = null;
        if (!string.IsNullOrWhiteSpace(RatingBox.Text))
        {
            if (!double.TryParse(RatingBox.Text, out var parsedRating) || parsedRating < 0 || parsedRating > 10)
            { StatusText.Text = "التقييم يجب أن يكون رقماً من 0 إلى 10."; return; }
            rating = parsedRating;
        }
        int? runtime = null;
        if (!string.IsNullOrWhiteSpace(RuntimeBox.Text))
        {
            if (!int.TryParse(RuntimeBox.Text, out var parsedRuntime) || parsedRuntime < 1 || parsedRuntime > 1000)
            { StatusText.Text = "المدة يجب أن تكون بالدقائق من 1 إلى 1000."; return; }
            runtime = parsedRuntime;
        }
        var kindItem = KindBox.SelectedItem as ComboBoxItem;
        var request = new
        {
            videoId = IdBox.Text,
            title = TitleBox.Text,
            description = DescriptionBox.Text,
            year,
            genre = GenreBox.Text,
            kind = kindItem?.Tag?.ToString() ?? "Movie",
            posterPath = (string?)null,
            director = DirectorBox.Text,
            cast = CastBox.Text,
            rating,
            runtimeMinutes = runtime,
            releaseDate = string.IsNullOrWhiteSpace(ReleaseDateBox.Text) ? null : ReleaseDateBox.Text.Trim(),
            tags = TagsBox.Text
        };
        try
        {
            var response = await _http.PostAsJsonAsync("/api/admin/metadata", request);
            if (!response.IsSuccessStatusCode) { StatusText.Text = "لم يتم الحفظ. تحقق من العنوان والسنة."; return; }
            StatusText.Text = "تم حفظ بيانات الفيديو بنجاح.";
            await LoadVideosAsync();
            VideosGrid.SelectedItem = _videos.FirstOrDefault(v => v.Id == IdBox.Text);
        }
        catch (Exception ex) { StatusText.Text = "تعذر الحفظ: " + ex.Message; }
    }


    private async System.Threading.Tasks.Task LoadSeriesAsync()
    {
        try
        {
            var selectedId = (SeriesPicker.SelectedItem as SeriesAdmin)?.SeriesId;
            var rows = await _http.GetFromJsonAsync<SeriesAdmin[]>("/api/admin/series") ?? Array.Empty<SeriesAdmin>();
            SeriesPicker.ItemsSource = rows;
            SeriesPicker.SelectedItem = rows.FirstOrDefault(x => x.SeriesId == selectedId);
            if (SeriesPicker.SelectedItem is null) SeasonsGrid.ItemsSource = null;
        }
        catch (Exception ex) { SeriesStatusText.Text = "تعذر تحميل المسلسلات: " + ex.Message; }
    }

    private async void SeriesPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SeriesPicker.SelectedItem is not SeriesAdmin s) return;
        SeriesIdBox.Text = s.SeriesId;
        SeriesTitleBox.Text = s.Title ?? "";
        SeriesDescriptionBox.Text = s.Description ?? "";
        SeriesYearBox.Text = s.Year?.ToString() ?? "";
        SeriesGenreBox.Text = s.Genre ?? "";
        SeriesPosterPathBox.Text = s.PosterPath ?? "";
        SeriesFolderPathBox.Text = s.FolderPath ?? "";
        await LoadSeasonsAsync(s.SeriesId);
    }

    private async System.Threading.Tasks.Task LoadSeasonsAsync(string seriesId)
    {
        try
        {
            var url = "/api/admin/seasons?seriesId=" + Uri.EscapeDataString(seriesId);
            var rows = await _http.GetFromJsonAsync<SeasonAdmin[]>(url) ?? Array.Empty<SeasonAdmin>();
            SeasonsGrid.ItemsSource = rows;
            SeriesStatusText.Text = $"تم تحميل {rows.Length} موسم/مواسم.";
        }
        catch (Exception ex) { SeriesStatusText.Text = "تعذر تحميل المواسم: " + ex.Message; }
    }

    private async void UploadSeriesPoster_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SeriesIdBox.Text))
        {
            SeriesStatusText.Text = "احفظ المسلسل أولاً ثم اختر غلافه.";
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "اختر غلاف المسلسل",
            Filter = "صور مدعومة|*.jpg;*.jpeg;*.png;*.webp",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;

        var ext = Path.GetExtension(dialog.FileName).ToLowerInvariant();
        var contentType = ext switch { ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", ".webp" => "image/webp", _ => "" };
        var info = new FileInfo(dialog.FileName);
        if (contentType.Length == 0 || info.Length == 0 || info.Length > 5_242_880)
        {
            SeriesStatusText.Text = "اختر صورة JPG أو PNG أو WEBP لا يتجاوز حجمها 5 ميجابايت.";
            return;
        }

        try
        {
            SeriesStatusText.Text = "جارٍ رفع غلاف المسلسل...";
            using var bytes = new ByteArrayContent(await File.ReadAllBytesAsync(dialog.FileName));
            bytes.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            var response = await _http.PostAsync("/api/admin/series-poster/" + Uri.EscapeDataString(SeriesIdBox.Text.Trim()), bytes);
            if (!response.IsSuccessStatusCode)
            {
                SeriesStatusText.Text = "تعذر رفع الغلاف. تحقق من حفظ المسلسل وصيغة الصورة وحجمها.";
                return;
            }

            var result = await response.Content.ReadFromJsonAsync<PosterUploadResult>();
            SeriesPosterPathBox.Text = result?.PosterPath ?? "";
            SeriesStatusText.Text = "تم رفع غلاف المسلسل وحفظه.";
            await LoadSeriesAsync();
            SeriesPicker.SelectedItem = (SeriesPicker.ItemsSource as SeriesAdmin[])?.FirstOrDefault(x => x.SeriesId == SeriesIdBox.Text.Trim());
        }
        catch (Exception ex)
        {
            SeriesStatusText.Text = "تعذر رفع غلاف المسلسل: " + ex.Message;
        }
    }

    private async void SaveSeries_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SeriesTitleBox.Text)) { SeriesStatusText.Text = "اكتب اسم المسلسل أولاً."; return; }
        int? year = null;
        if (!string.IsNullOrWhiteSpace(SeriesYearBox.Text))
        {
            if (!int.TryParse(SeriesYearBox.Text, out var parsed) || parsed < 1888 || parsed > DateTime.UtcNow.Year + 5)
            { SeriesStatusText.Text = "سنة الإنتاج غير صحيحة."; return; }
            year = parsed;
        }
        var id = string.IsNullOrWhiteSpace(SeriesIdBox.Text) ? "series-" + Guid.NewGuid().ToString("N") : SeriesIdBox.Text.Trim();
        var folderPath = string.IsNullOrWhiteSpace(SeriesFolderPathBox.Text) ? null : Path.GetFullPath(SeriesFolderPathBox.Text.Trim());
        if (folderPath is not null && !Directory.Exists(folderPath)) { SeriesStatusText.Text = "مجلد المسلسل المحدد غير موجود."; return; }
        var request = new { seriesId = id, title = SeriesTitleBox.Text.Trim(), description = SeriesDescriptionBox.Text, year, genre = SeriesGenreBox.Text, posterPath = string.IsNullOrWhiteSpace(SeriesPosterPathBox.Text) ? null : SeriesPosterPathBox.Text.Trim(), folderPath };
        try
        {
            var response = await _http.PostAsJsonAsync("/api/admin/series", request);
            if (!response.IsSuccessStatusCode) { SeriesStatusText.Text = "لم يتم حفظ المسلسل. تحقق من البيانات."; return; }
            SeriesIdBox.Text = id;
            SeriesStatusText.Text = "تم حفظ بيانات المسلسل.";
            await LoadSeriesAsync();
            SeriesPicker.SelectedItem = (SeriesPicker.ItemsSource as SeriesAdmin[])?.FirstOrDefault(x => x.SeriesId == id);
            if (SeriesPicker.SelectedItem is null) await LoadSeasonsAsync(id);
            await LoadVideosAsync();
        }
        catch (Exception ex) { SeriesStatusText.Text = "تعذر حفظ المسلسل: " + ex.Message; }
    }

    private async void DeleteSeries_Click(object sender, RoutedEventArgs e)
    {
        if (SeriesPicker.SelectedItem is not SeriesAdmin series)
        {
            SeriesStatusText.Text = "اختر المسلسل الذي تريد حذفه أولاً.";
            return;
        }
        var confirm = MessageBox.Show(
            $"سيتم حذف بيانات «{series.Title}» وجميع مواسمه وروابط حلقاته فقط. لن تُحذف ملفات الفيديو الأصلية من القرص.\n\nهل تريد المتابعة؟",
            "حذف بيانات المسلسل", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;
        try
        {
            var response = await _http.DeleteAsync("/api/admin/series/" + Uri.EscapeDataString(series.SeriesId));
            if (!response.IsSuccessStatusCode)
            {
                SeriesStatusText.Text = "تعذر حذف المسلسل: " + await response.Content.ReadAsStringAsync();
                return;
            }
            SeriesPicker.SelectedItem = null;
            SeriesIdBox.Clear(); SeriesTitleBox.Clear(); SeriesDescriptionBox.Clear(); SeriesYearBox.Clear();
            SeriesGenreBox.Clear(); SeriesPosterPathBox.Clear(); SeriesFolderPathBox.Clear();
            SeasonsGrid.ItemsSource = null;
            await LoadSeriesAsync();
            await LoadVideosAsync();
            await LoadMissingEpisodesAsync();
            SeriesStatusText.Text = "تم حذف بيانات المسلسل ومواسمه، وملفات الفيديو الأصلية ما زالت محفوظة.";
        }
        catch (Exception ex) { SeriesStatusText.Text = "تعذر حذف المسلسل: " + ex.Message; }
    }

    private async void DeleteSeason_Click(object sender, RoutedEventArgs e)
    {
        if (SeasonsGrid.SelectedItem is not SeasonAdmin season)
        {
            SeriesStatusText.Text = "اختر الموسم الذي تريد حذفه أولاً.";
            return;
        }
        var confirm = MessageBox.Show(
            $"سيتم حذف الموسم {season.SeasonNumber} وروابط حلقاته من بيانات المسلسل فقط، ولن تُحذف ملفات الفيديو الأصلية.\n\nهل تريد المتابعة؟",
            "حذف الموسم", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;
        try
        {
            var response = await _http.DeleteAsync("/api/admin/seasons/" + season.SeasonId);
            if (!response.IsSuccessStatusCode)
            {
                SeriesStatusText.Text = "تعذر حذف الموسم: " + await response.Content.ReadAsStringAsync();
                return;
            }
            await LoadSeasonsAsync(season.SeriesId);
            await LoadMissingEpisodesAsync();
            SeriesStatusText.Text = "تم حذف الموسم وروابط حلقاته، وملفات الفيديو الأصلية لم تُمس.";
        }
        catch (Exception ex) { SeriesStatusText.Text = "تعذر حذف الموسم: " + ex.Message; }
    }

    private async void AddSeason_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SeriesIdBox.Text)) { SeriesStatusText.Text = "احفظ المسلسل أولاً."; return; }
        if (!int.TryParse(SeasonNumberBox.Text, out var number) || number < 1 || number > 500)
        { SeriesStatusText.Text = "رقم الموسم يجب أن يكون بين 1 و500."; return; }
        try
        {
            var seasonFolder = string.IsNullOrWhiteSpace(SeasonFolderPathBox.Text) ? null : Path.GetFullPath(SeasonFolderPathBox.Text.Trim());
            if (seasonFolder is not null && !Directory.Exists(seasonFolder)) { SeriesStatusText.Text = "مجلد الموسم المحدد غير موجود."; return; }
            var response = await _http.PostAsJsonAsync("/api/admin/seasons", new { seriesId = SeriesIdBox.Text.Trim(), seasonNumber = number, title = SeasonTitleBox.Text, folderPath = seasonFolder });
            if (!response.IsSuccessStatusCode) { SeriesStatusText.Text = "لم تتم إضافة الموسم. ربما رقم الموسم مكرر."; return; }
            SeasonTitleBox.Clear(); SeasonFolderPathBox.Clear(); SeasonNumberBox.Text = (number + 1).ToString();
            await LoadSeasonsAsync(SeriesIdBox.Text.Trim());
            await LoadVideosAsync();
            SeriesStatusText.Text = "تمت إضافة الموسم بنجاح.";
        }
        catch (Exception ex) { SeriesStatusText.Text = "تعذر إضافة الموسم: " + ex.Message; }
    }

    private void SeasonsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SeasonsGrid.SelectedItem is SeasonAdmin season)
        {
            SeasonFolderPathBox.Text = season.FolderPath ?? "";
            SeriesStatusText.Text = $"الموسم المحدد: {season.SeasonNumber} — اختر ملف فيديو لإضافته كحلقة.";
        }
    }

    private async void SaveSeasonFolder_Click(object sender, RoutedEventArgs e)
    {
        if (SeasonsGrid.SelectedItem is not SeasonAdmin season)
        {
            SeriesStatusText.Text = "اختر الموسم الذي تريد تحديث مساره أولاً.";
            return;
        }

        var folder = string.IsNullOrWhiteSpace(SeasonFolderPathBox.Text)
            ? null
            : Path.GetFullPath(SeasonFolderPathBox.Text.Trim());
        if (folder is not null && !Directory.Exists(folder))
        {
            SeriesStatusText.Text = "مجلد الموسم المحدد غير موجود.";
            return;
        }

        try
        {
            SeriesStatusText.Text = "جارٍ حفظ المسار وفحص ملفات الفيديو...";
            var response = await _http.PostAsJsonAsync(
                "/api/admin/seasons/" + season.SeasonId + "/folder",
                new { folderPath = folder });
            if (!response.IsSuccessStatusCode)
            {
                SeriesStatusText.Text = "تعذر حفظ مسار الموسم. تحقق من المجلد المحدد.";
                return;
            }

            int? videoCount = null;
            if (folder is not null)
            {
                var scan = await _http.PostAsJsonAsync("/api/admin/scan-folder", new { folderPath = folder });
                if (!scan.IsSuccessStatusCode)
                {
                    SeriesStatusText.Text = "تم حفظ المسار، لكن تعذر فحص المجلد.";
                    await LoadSeasonsAsync(season.SeriesId);
                    return;
                }
                var result = await scan.Content.ReadFromJsonAsync<FolderScanResult>();
                videoCount = result?.VideoCount ?? 0;
            }

            await LoadSeasonsAsync(season.SeriesId);
            await LoadVideosAsync();
            SeriesStatusText.Text = folder is null
                ? "تم مسح مسار الموسم. لم يتم حذف أي ملفات من المكتبة."
                : $"تم حفظ مسار الموسم وفحصه. عدد ملفات الفيديو التي عُثر عليها: {videoCount ?? 0}.";
        }
        catch (Exception ex)
        {
            SeriesStatusText.Text = "تعذر حفظ أو فحص مجلد الموسم: " + ex.Message;
        }
    }

    private async void PreviewSeasonFolder_Click(object sender, RoutedEventArgs e)
    {
        if (SeasonsGrid.SelectedItem is not SeasonAdmin season)
        {
            SeriesStatusText.Text = "اختر الموسم أولاً.";
            return;
        }
        try
        {
            SeriesStatusText.Text = "جارٍ فحص أسماء ملفات الموسم...";
            var rows = await _http.GetFromJsonAsync<EpisodeCandidate[]>(
                "/api/admin/seasons/" + season.SeasonId + "/preview") ?? Array.Empty<EpisodeCandidate>();
            _episodeCandidates.Clear();
            foreach (var row in rows)
            {
                row.Status = row.IsAlreadyLinked ? "مربوط مسبقاً"
                    : row.EpisodeNumber is null or <= 0 ? "لم يتم التعرف على الرقم"
                    : row.IsNumberUsed ? "رقم مكرر"
                    : "جاهز للمراجعة";
                row.Selected = !row.IsAlreadyLinked && !row.IsNumberUsed && row.EpisodeNumber is > 0;
                _episodeCandidates.Add(row);
            }
            SeriesStatusText.Text = $"تم العثور على {rows.Length} ملفاً. راجع أرقام الحلقات قبل اعتماد الربط.";
        }
        catch (Exception ex) { SeriesStatusText.Text = "تعذر فحص الموسم: " + ex.Message; }
    }

    private async void LinkDetectedEpisodes_Click(object sender, RoutedEventArgs e)
    {
        if (SeasonsGrid.SelectedItem is not SeasonAdmin season)
        {
            SeriesStatusText.Text = "اختر الموسم أولاً.";
            return;
        }
        EpisodeCandidatesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        EpisodeCandidatesGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var selected = _episodeCandidates.Where(x => x.Selected).ToArray();
        if (selected.Length == 0)
        {
            SeriesStatusText.Text = "حدد ملفاً واحداً على الأقل للربط.";
            return;
        }
        if (selected.Any(x => x.EpisodeNumber is null or < 1 or > 10000))
        {
            SeriesStatusText.Text = "صحح أرقام الحلقات المحددة أولاً.";
            return;
        }
        if (MessageBox.Show($"سيتم ربط {selected.Length} ملفاً بالموسم رقم {season.SeasonNumber}. هل راجعت الأرقام؟",
            "تأكيد ربط الحلقات", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        try
        {
            var payload = new
            {
                episodes = selected.Select(x => new
                {
                    videoId = x.VideoId,
                    episodeNumber = x.EpisodeNumber,
                    title = x.Title,
                    selected = true
                }).ToArray()
            };
            var response = await _http.PostAsJsonAsync(
                "/api/admin/seasons/" + season.SeasonId + "/link-detected", payload);
            if (!response.IsSuccessStatusCode)
            {
                SeriesStatusText.Text = "تعذر اعتماد ربط الحلقات المحددة.";
                return;
            }
            var result = await response.Content.ReadFromJsonAsync<LinkDetectedResult>();
            await LoadSeasonsAsync(season.SeriesId);
            var rows = await _http.GetFromJsonAsync<EpisodeCandidate[]>(
                "/api/admin/seasons/" + season.SeasonId + "/preview") ?? Array.Empty<EpisodeCandidate>();
            _episodeCandidates.Clear();
            foreach (var row in rows)
            {
                row.Status = row.IsAlreadyLinked ? "مربوط مسبقاً"
                    : row.EpisodeNumber is null or <= 0 ? "لم يتم التعرف على الرقم"
                    : row.IsNumberUsed ? "رقم مكرر"
                    : "جاهز للمراجعة";
                row.Selected = !row.IsAlreadyLinked && !row.IsNumberUsed && row.EpisodeNumber is > 0;
                _episodeCandidates.Add(row);
            }
            SeriesStatusText.Text = $"تم ربط {result?.LinkedCount ?? 0} حلقة، وتخطي {result?.Skipped ?? 0} ملف.";
        }
        catch (Exception ex) { SeriesStatusText.Text = "تعذر ربط الحلقات: " + ex.Message; }
    }

    private async void AddEpisode_Click(object sender, RoutedEventArgs e)
    {
        if (SeasonsGrid.SelectedItem is not SeasonAdmin season) { SeriesStatusText.Text = "اختر الموسم أولاً."; return; }
        if (EpisodeVideoPicker.SelectedItem is not AdminVideo video) { SeriesStatusText.Text = "اختر ملف الفيديو من المكتبة."; return; }
        if (!int.TryParse(EpisodeNumberBox.Text, out var number) || number < 1 || number > 10000)
        { SeriesStatusText.Text = "رقم الحلقة غير صحيح."; return; }
        try
        {
            var response = await _http.PostAsJsonAsync("/api/admin/episodes", new { seasonId = season.SeasonId, videoId = video.Id, episodeNumber = number, title = EpisodeTitleBox.Text });
            if (!response.IsSuccessStatusCode) { SeriesStatusText.Text = "لم تتم إضافة الحلقة. قد يكون الملف مرتبطاً بحلقة أخرى أو الرقم مكرراً."; return; }
            EpisodeTitleBox.Clear(); EpisodeNumberBox.Text = (number + 1).ToString();
            var rows = await _http.GetFromJsonAsync<EpisodeCandidate[]>(
                "/api/admin/seasons/" + season.SeasonId + "/preview") ?? Array.Empty<EpisodeCandidate>();
            _episodeCandidates.Clear();
            foreach (var row in rows)
            {
                row.Status = row.IsAlreadyLinked ? "مربوط مسبقاً"
                    : row.EpisodeNumber is null or <= 0 ? "لم يتم التعرف على الرقم"
                    : row.IsNumberUsed ? "رقم مكرر"
                    : "جاهز للمراجعة";
                row.Selected = !row.IsAlreadyLinked && !row.IsNumberUsed && row.EpisodeNumber is > 0;
                _episodeCandidates.Add(row);
            }
            await LoadMissingEpisodesAsync();
            SeriesStatusText.Text = $"تم ربط «{video.Title}» كالحلقة رقم {number} وتحديث قائمة ملفات الموسم.";
        }
        catch (Exception ex) { SeriesStatusText.Text = "تعذر إضافة الحلقة: " + ex.Message; }
    }

    private async void RefreshMissingEpisodes_Click(object sender, RoutedEventArgs e)
    {
        await LoadMissingEpisodesAsync();
    }

    private async System.Threading.Tasks.Task LoadMissingEpisodesAsync()
    {
        try
        {
            var rows = await _http.GetFromJsonAsync<MissingEpisodeAdmin[]>("/api/admin/missing-episodes")
                ?? Array.Empty<MissingEpisodeAdmin>();
            MissingEpisodesGrid.ItemsSource = rows;
            SeriesStatusText.Text = rows.Length == 0
                ? "فحص الملفات مكتمل: جميع ملفات الحلقات المرتبطة موجودة."
                : $"تم العثور على {rows.Length} حلقة لا يوجد ملفها في المسارات المسجلة. أضف المجلد الجديد ثم أعد الربط.";
        }
        catch (Exception ex)
        {
            SeriesStatusText.Text = "تعذر فحص الملفات المفقودة: " + ex.Message;
        }
    }

    private async void RelinkMissingEpisode_Click(object sender, RoutedEventArgs e)
    {
        if (MissingEpisodesGrid.SelectedItem is not MissingEpisodeAdmin episode)
        {
            SeriesStatusText.Text = "حدد الحلقة المفقودة أولاً.";
            return;
        }
        if (EpisodeVideoPicker.SelectedItem is not AdminVideo video)
        {
            SeriesStatusText.Text = "أضف مجلد الملف الجديد إلى المكتبة ثم اختر الفيديو البديل من قائمة الملفات.";
            return;
        }
        if (MessageBox.Show(
            $"هل تريد ربط الحلقة «{episode.Title}» بالملف «{video.Title}»؟",
            "تأكيد إعادة الربط",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        try
        {
            var response = await _http.PostAsJsonAsync(
                "/api/admin/episodes/" + episode.EpisodeId + "/relink",
                new { videoId = video.Id });
            if (!response.IsSuccessStatusCode)
            {
                SeriesStatusText.Text = "تعذرت إعادة الربط. قد يكون الملف مستخدماً في حلقة أخرى.";
                return;
            }
            await LoadMissingEpisodesAsync();
            SeriesStatusText.Text = $"تم ربط الحلقة «{episode.Title}» بالملف الجديد.";
        }
        catch (Exception ex)
        {
            SeriesStatusText.Text = "تعذر إعادة ربط الحلقة: " + ex.Message;
        }
    }

    private static string GetLanAddress()
    {
        static bool IsPrivateIPv4(IPAddress address)
        {
            if (address.AddressFamily != AddressFamily.InterNetwork) return false;
            var b = address.GetAddressBytes();
            return b[0] == 10 ||
                   (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                   (b[0] == 192 && b[1] == 168);
        }

        var interfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .OrderBy(n => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 ? 0 : 1)
            .ThenByDescending(n => n.GetIPProperties().GatewayAddresses
                .Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork &&
                          !g.Address.Equals(IPAddress.Any)));

        foreach (var adapter in interfaces)
        {
            var address = adapter.GetIPProperties().UnicastAddresses
                .Select(x => x.Address)
                .FirstOrDefault(IsPrivateIPv4);
            if (address is not null) return address.ToString();
        }

        return "عنوان الشبكة غير معروف";
    }

    private sealed class MediaToolStatus
    {
        public bool Available { get; set; }
        public string FfmpegPath { get; set; } = "";
        public string FfprobePath { get; set; } = "";
        public string Message { get; set; } = "";
    }
    private sealed class SeriesAdmin
    {
        public string SeriesId { get; set; } = "";
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public int? Year { get; set; }
        public string Genre { get; set; } = "";
        public string? PosterPath { get; set; }
        public string? FolderPath { get; set; }
    }
    private sealed class SeasonAdmin
    {
        public long SeasonId { get; set; }
        public string SeriesId { get; set; } = "";
        public int SeasonNumber { get; set; }
        public string Title { get; set; } = "";
        public string? FolderPath { get; set; }
    }
    private sealed class PosterUploadResult { public string? PosterPath { get; set; } }
    private sealed class LinkDetectedResult { public int LinkedCount { get; set; } public int Skipped { get; set; } }
    private sealed class MissingEpisodeAdmin
    {
        public long EpisodeId { get; set; }
        public string SeriesId { get; set; } = "";
        public string SeriesTitle { get; set; } = "";
        public int SeasonNumber { get; set; }
        public int EpisodeNumber { get; set; }
        public string Title { get; set; } = "";
        public string VideoId { get; set; } = "";
    }
    private sealed class EpisodeCandidate
    {
        public string VideoId { get; set; } = "";
        public string FileName { get; set; } = "";
        public string Title { get; set; } = "";
        public int? EpisodeNumber { get; set; }
        public bool IsAlreadyLinked { get; set; }
        public bool IsNumberUsed { get; set; }
        public bool Selected { get; set; }
        public string Status { get; set; } = "";
    }
    private sealed class FolderScanResult { public bool Registered { get; set; } public int VideoCount { get; set; } }
    private sealed class AdminVideo
    {
        public string Id { get; set; } = "";
        public string? Title { get; set; }
        public string? Description { get; set; }
        public int? Year { get; set; }
        public string? Genre { get; set; }
        public string? Kind { get; set; }
        public string? PosterPath { get; set; }
        public string? Director { get; set; }
        public string? Cast { get; set; }
        public double? Rating { get; set; }
        public int? RuntimeMinutes { get; set; }
        public string? ReleaseDate { get; set; }
        public string? Tags { get; set; }
    }
}

public sealed class VideoFileCandidateAdmin
{
    public string FullPath { get; set; } = "";
    public string FileName { get; set; } = "";
    public long SizeBytes { get; set; }
}

public sealed class FolderVideoSelectionWindow : Window
{
    private readonly ObservableCollection<SelectableVideoFile> _items;
    public string[] SelectedPaths => _items.Where(x => x.Selected).Select(x => x.FullPath).ToArray();

    public FolderVideoSelectionWindow(string folderPath, IEnumerable<VideoFileCandidateAdmin> candidates)
    {
        Title = "حدد ملفات الفيديو التي تريد إضافتها";
        Width = 760;
        Height = 620;
        MinWidth = 600;
        MinHeight = 450;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FlowDirection = FlowDirection.RightToLeft;
        Background = System.Windows.Media.Brushes.White;
        Owner = Application.Current.MainWindow;

        _items = new ObservableCollection<SelectableVideoFile>(candidates.Select(x => new SelectableVideoFile
        {
            FullPath = x.FullPath,
            FileName = x.FileName,
            SizeText = FormatSize(x.SizeBytes),
            Selected = false
        }));

        var root = new DockPanel { Margin = new Thickness(16) };
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(new TextBlock { Text = "اختر الفيديوهات التي تريد إدخالها إلى المكتبة", FontSize = 19, FontWeight = FontWeights.Bold, Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(20,45,74)) });
        header.Children.Add(new TextBlock { Text = folderPath, TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.DimGray, Margin = new Thickness(0,6,0,8) });
        var selectionButtons = new StackPanel { Orientation = Orientation.Horizontal };
        var allButton = new Button { Content = "تحديد الكل", Padding = new Thickness(12,6,12,6), Margin = new Thickness(0,0,6,0) };
        var noneButton = new Button { Content = "إلغاء التحديد", Padding = new Thickness(12,6,12,6) };
        allButton.Click += (_, _) => { foreach (var item in _items) item.Selected = true; };
        noneButton.Click += (_, _) => { foreach (var item in _items) item.Selected = false; };
        selectionButtons.Children.Add(allButton);
        selectionButtons.Children.Add(noneButton);
        header.Children.Add(selectionButtons);
        var selectionCount = new TextBlock
        {
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(36, 91, 135)),
            FontWeight = FontWeights.SemiBold
        };
        header.Children.Add(selectionCount);
        void UpdateSelectionCount() =>
            selectionCount.Text = $"تم تحديد {_items.Count(x => x.Selected)} من أصل {_items.Count} فيديو";
        foreach (var item in _items)
            item.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(SelectableVideoFile.Selected))
                    UpdateSelectionCount();
            };
        UpdateSelectionCount();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0,12,0,0) };
        var addButton = new Button { Content = "إضافة المحدد", IsDefault = true, Padding = new Thickness(18,8,18,8), Margin = new Thickness(0,0,8,0), Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(36,91,135)), Foreground = System.Windows.Media.Brushes.White };
        var cancelButton = new Button { Content = "إلغاء", IsCancel = true, Padding = new Thickness(18,8,18,8) };
        addButton.Click += (_, _) => { DialogResult = true; };
        footer.Children.Add(addButton);
        footer.Children.Add(cancelButton);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var grid = new DataGrid { ItemsSource = _items, AutoGenerateColumns = false, CanUserAddRows = false, IsReadOnly = false, SelectionMode = DataGridSelectionMode.Extended, HeadersVisibility = DataGridHeadersVisibility.Column, FlowDirection = FlowDirection.RightToLeft };
        grid.Columns.Add(new DataGridCheckBoxColumn { Header = "اختيار", Binding = new Binding(nameof(SelectableVideoFile.Selected)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 65 });
        grid.Columns.Add(new DataGridTextColumn { Header = "اسم ملف الفيديو", Binding = new Binding(nameof(SelectableVideoFile.FileName)), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "الحجم", Binding = new Binding(nameof(SelectableVideoFile.SizeText)), IsReadOnly = true, Width = 100 });
        root.Children.Add(grid);
        Content = root;
    }

    private static string FormatSize(long bytes) => bytes >= 1024L * 1024 * 1024
        ? (bytes / (1024d * 1024 * 1024)).ToString("0.00") + " GB"
        : bytes >= 1024L * 1024 ? (bytes / (1024d * 1024)).ToString("0.0") + " MB"
        : (bytes / 1024d).ToString("0") + " KB";

    private sealed class SelectableVideoFile : INotifyPropertyChanged
    {
        private bool _selected;
        public string FullPath { get; set; } = "";
        public string FileName { get; set; } = "";
        public string SizeText { get; set; } = "";
        public bool Selected { get => _selected; set { if (_selected == value) return; _selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
