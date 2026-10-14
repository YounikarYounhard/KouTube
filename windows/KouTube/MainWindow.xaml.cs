using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KouTube;

public partial class MainWindow : Window
{
    /// <summary>A card; pictures load only when the card is actually on screen.</summary>
    public sealed class LibRow(Video v)
    {
        ImageSource? _cover, _avatar; bool _c, _a;
        static readonly Color[] Palette = [Color.FromRgb(0x5C, 0x6B, 0xC0), Color.FromRgb(0x26, 0xA6, 0x9A), Color.FromRgb(0xEF, 0x6C, 0x00), Color.FromRgb(0x8E, 0x24, 0xAA),
            Color.FromRgb(0x43, 0xA0, 0x47), Color.FromRgb(0xD8, 0x1B, 0x60), Color.FromRgb(0x1E, 0x88, 0xE5), Color.FromRgb(0x6D, 0x4C, 0x41)];

        public Video V { get; } = v;
        public string Title => V.Title;
        public string Channel => V.Meta.Channel ?? "";
        public string Sub => string.Join(" · ", new[] { V.Status, Ago(V.Meta.AddedAt) }.Where(s => s.Length > 0));
        public string Duration => V.Meta.DurationMs is { } d ? TimeSpan.FromMilliseconds(d).ToString(d >= 3_600_000 ? @"h\:mm\:ss" : @"m\:ss") : "";
        public string Badge => !V.Present ? V.FileState.State switch { FileState.AfterWatch => "Удалено после просмотра", FileState.ByUser => "Удалено", _ => "Файл пропал" } : "";
        public Visibility BadgeVisible => Badge.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        double Progress => V.Watch.Status == Watch.Finished ? 1 : V.Meta.DurationMs > 0 ? Math.Clamp(V.Watch.PosMs / (double)V.Meta.DurationMs!.Value, 0, 1) : 0;
        public Visibility ProgressVisible => Progress > 0 ? Visibility.Visible : Visibility.Collapsed;
        public double ProgressWidth => 300 * Progress;
        public double ShortProgressWidth => 180 * Progress;
        public string Letter => (Channel.Trim().FirstOrDefault() is var ch && ch != default ? ch : '?').ToString().ToUpperInvariant();
        public Brush AvatarColor => new SolidColorBrush(Palette[(Channel.GetHashCode() & 0x7fffffff) % Palette.Length]);
        public Visibility AvatarVisible => Avatar != null ? Visibility.Visible : Visibility.Collapsed;

        public ImageSource? Cover
        {
            get
            {
                if (_c) return _cover; _c = true;
                return _cover = Load(V.Found?.Thumb ?? (V.Meta.Thumb != null ? Path.Combine(V.Mount.Ml, V.Meta.Thumb.Replace('/', '\\')) : null), 320);
            }
        }

        public ImageSource? Avatar
        {
            get
            {
                if (_a) return _avatar; _a = true;
                return _avatar = V.Meta.Avatar != null ? Load(Path.Combine(V.Mount.Ml, V.Meta.Avatar.Replace('/', '\\')), 72) : null;
            }
        }

        static ImageSource? Load(string? p, int width)
        {
            if (p == null || !File.Exists(p)) return null;
            try
            {
                var b = new BitmapImage();
                b.BeginInit(); b.UriSource = new Uri(p); b.DecodePixelWidth = width; b.CacheOption = BitmapCacheOption.OnLoad; b.EndInit(); b.Freeze();
                return b;
            }
            catch (Exception) { return null; }
        }
    }

    public static string Ago(long? t)
    {
        if (t == null) return "";
        var d = (long)(DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(t.Value)).TotalDays;
        static string P(long n, string a, string b, string c) => n % 10 == 1 && n % 100 != 11 ? a : n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14 ? b : c;
        return d <= 0 ? "сегодня" : d == 1 ? "вчера" : d < 30 ? $"{d} {P(d, "день", "дня", "дней")} назад"
             : d < 365 ? $"{d / 30} {P(d / 30, "месяц", "месяца", "месяцев")} назад" : $"{d / 365} {P(d / 365, "год", "года", "лет")} назад";
    }

    static readonly (string Key, string Name)[] SbCats =
    [
        ("sponsor", "Спонсорская реклама"), ("selfpromo", "Самореклама"), ("interaction", "Напоминания о подписке"),
        ("intro", "Введение (заставка)"), ("outro", "Конечные заставки и титры"), ("preview", "Предпросмотр"),
        ("filler", "Наполнители"), ("music_offtopic", "Сегменты без музыки и не по теме"), ("hook", "Вступление / приветствие"),
    ];

    static readonly Dictionary<int, (string Key, string Name)[]> ChipSets = new()
    {
        [0] = [("all", "Все"), ("new", "Не просмотрено"), ("cont", "Продолжить"), ("done", "Досмотрено")],
        [1] = [("all", "Все"), ("new", "Не просмотрено"), ("done", "Досмотрено")],
        [2] = [("watched", "Просмотренные"), ("deleted", "Удалённые"), ("missing", "Пропавшие"), ("dups", "Повторы")],
    };

    List<Video> _all = [];
    int _page;
    readonly Dictionary<int, string> _chip = new() { [0] = "all", [1] = "all", [2] = "watched" };

    public MainWindow()
    {
        InitializeComponent();
        QueueList.ItemsSource = Downloader.Rows;
        Downloader.OnChanged += () => Ui.OnUi(UpdatePlan);
        // the summary panel follows the running download twice a second
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        t.Tick += (_, _) => { if (Downloader.Running || _page == 3) UpdateSummary(); };
        t.Start();
        foreach (var (k, n) in SbCats) SbPanel.Children.Add(new CheckBox { Content = n, Tag = k, Margin = new Thickness(0, 4, 18, 4), MinWidth = 260 });
        LoadSettingsUi();
        BuildChips();
    }

    void Status(string s) => Ui.OnUi(() => StatusText.Text = s);

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshStorageUi();
        ShowLibrary();
        UpdatePlan();
        if (Tools.Missing().Length > 0)
            try { await Tools.Fetch(Status); Status("yt-dlp, ffmpeg и deno скачаны — всё готово"); }
            catch (Exception ex) { Status($"Не удалось скачать {Tools.Missing()}: {ex.Message} — проверьте интернет и перезапустите KouTube"); }
        _ = Task.Run(() => { var r = Tools.UpdateYtDlp(); Ui.OnUi(() => ToolsText.Text = "yt-dlp: " + r); });
        await RefreshAll();
        await OfferCleanup();
        var req = Lib.RedownloadRequests().ToList();
        if (req.Count > 0 && Target.SelectedItem is Mount t)
        {
            await Downloader.Add(req.Select(v => v.Meta.Url ?? "https://www.youtube.com/watch?v=" + v.Meta.YtId), t.Ref.StorageId, Status, source: "«Скачать снова» с телефона");
            Status($"С телефона попросили скачать снова: {req.Count} — они во вкладке «Загрузки»");
        }
    }

    async Task RefreshAll()
    {
        foreach (var m in Lib.Mounts.ToList())
        {
            Status($"Читаю {m.Name}…");
            await Task.Run(() => Lib.Refresh(m, s => Status($"{m.Name}: {s}")));
        }
        Recommend.Calibrate(Lib.Videos());
        RefreshStorageUi();
        ShowLibrary();
        UpdatePlan();
        // the list first, pictures after — they may take a while (or YouTube may be out of reach)
        foreach (var m in Lib.Mounts.Where(m => m.Online).ToList())
        {
            await Lib.FetchThumbs(m, s => Status($"{m.Name}: {s}"));
            await Lib.FetchAvatars(m, s => Status($"{m.Name}: {s}"));
            await Lib.FetchComments(m, s => Status($"{m.Name}: {s}"));
            await Task.Run(() => Lib.Flush(m));
        }
        ShowLibrary();
        Status("Готово");
    }

    // ---------- pages ----------

    void OnNav(object sender, SelectionChangedEventArgs e)
    {
        if (PageCards == null) return;
        _page = Math.Max(0, Nav.SelectedIndex);
        PageCards.Visibility = _page <= 2 ? Visibility.Visible : Visibility.Collapsed;
        PageDownloads.Visibility = _page == 3 ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = _page == 4 ? Visibility.Visible : Visibility.Collapsed;
        if (_page <= 2) { BuildChips(); ShowLibrary(); }
        if (_page == 3) UpdatePlan();
    }

    void BuildChips()
    {
        if (Chips == null) return;
        Chips.Children.Clear();
        if (!ChipSets.TryGetValue(_page, out var set)) return;
        foreach (var (k, n) in set)
        {
            var b = new ToggleButton { Content = n, Tag = k, Style = (Style)FindResource("Chip"), IsChecked = _chip[_page] == k };
            b.Click += (_, _) => { _chip[_page] = k; foreach (ToggleButton o in Chips.Children) o.IsChecked = (string)o.Tag == k; ShowLibrary(); };
            Chips.Children.Add(b);
        }
    }

    void ShowLibrary()
    {
        if (Cards == null) return;
        _all = Lib.Videos();
        var q = Search.Text.Trim().ToLowerInvariant();
        IEnumerable<Video> l = _all;
        var chip = _chip.GetValueOrDefault(_page, "all");
        l = _page switch
        {
            1 => l.Where(v => v.IsShort && v.Present),
            2 => l,
            _ => l.Where(v => !v.IsShort && v.Present),
        };
        var dupIds = _all.Where(v => v.Present && v.Meta.YtId != null).GroupBy(v => v.Meta.YtId).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        l = chip switch
        {
            "new" => l.Where(v => v.Watch.Status == Watch.New),
            "cont" => l.Where(v => v.Watch.Status == Watch.Started),
            "done" => l.Where(v => v.Watch.Status == Watch.Finished),
            "watched" => l.Where(v => v.Watch.Status != Watch.New || v.Watch.LastWatchedAt != null),
            "deleted" => l.Where(v => !v.Present && !v.Missing),
            "missing" => l.Where(v => v.Missing),
            "dups" => l.Where(v => v.Present && v.Meta.YtId != null && dupIds.Contains(v.Meta.YtId)),
            _ => l,
        };
        if (q.Length > 0) l = l.Where(v => new[] { v.Title, v.Meta.Channel, v.Meta.Category }.Any(s => s != null && Format.UnFullwidth(s).ToLowerInvariant().Contains(q)));
        var rows = (_page == 2 ? l.OrderByDescending(v => v.Watch.LastWatchedAt ?? v.FileState.At ?? 0) : l.OrderByDescending(v => v.Meta.AddedAt ?? 0))
            .Select(v => new LibRow(v)).ToList();
        Cards.ItemTemplate = (DataTemplate)FindResource(_page == 1 ? "ShortCard" : "VideoCard");
        Cards.ItemsSource = rows;
        EmptyCards.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyBtn.Visibility = Lib.Mounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = Lib.Mounts.Count == 0 ? "Подключите флешку или папку с видео — например ту, куда качает YTDLnis."
            : q.Length > 0 ? "Ничего не нашлось" : _page == 1 ? "Shorts пока нет. Нажмите «Подборка», чтобы подобрать." : "Здесь пока пусто. Нажмите «Подборка», чтобы подобрать видео.";
        StorageText.Text = string.Join("   ·   ", Lib.Mounts.Select(m => m.Online && m.Space() is { } s
            ? $"{m.Name}: {m.Docs.Count} видео, свободно {Ui.Gb(s.Free)} из {Ui.Gb(s.Total)}" : $"{m.Name}: {m.Error ?? "не подключено"}"));
    }

    void OnFilter(object sender, RoutedEventArgs e)
    {
        SearchHint.Visibility = Search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (IsLoaded && _page <= 2) ShowLibrary();
    }

    LibRow? Selected => Cards.SelectedItem as LibRow;

    void OnOpenVideo(object sender, RoutedEventArgs e)
    {
        if (Selected?.V is { Mount.Phone: not null }) { Status("Это видео на телефоне — смотрите его в KouTube на телефоне"); return; }
        if (Selected?.V.Found is { } f) Process.Start(new ProcessStartInfo(f.FullPath) { UseShellExecute = true });
    }

    void OnShowInExplorer(object sender, RoutedEventArgs e)
    {
        if (Selected?.V is { Mount.Phone: not null }) { Status("Это видео на телефоне — в Проводнике откройте телефон и папку библиотеки"); return; }
        if (Selected?.V.Found is { } f) Lib.OpenInExplorer(f.FullPath);
    }

    async void OnRedownload(object sender, RoutedEventArgs e)
    {
        if (Selected?.V is not { Present: false, Meta.YtId: { } id } v || Target.SelectedItem is not Mount t) return;
        Nav.SelectedIndex = 3;
        await Downloader.Add([v.Meta.Url ?? "https://www.youtube.com/watch?v=" + id], t.Ref.StorageId, Status, source: "«Скачать снова»");
        if (Downloader.Rows.FirstOrDefault(r => r.Item.Id == id) is { } row) row.Include = true;   // explicit command beats the "watched" mark
    }

    void OnNeverAgain(object sender, RoutedEventArgs e)
    {
        if (Selected?.V is not { } v) return;
        Lib.SetFlags(v.Mount, v.Key, f => f with { NoAutoRedownload = true, Redownload = false });
        Lib.Flush(v.Mount);
        ShowLibrary();
    }

    // ---------- watched → removed → new ones ----------

    readonly HashSet<string> _offered = [];

    /// <summary>Once per session and storage: show what was watched and offer to clear it and pick new videos.</summary>
    async Task OfferCleanup()
    {
        foreach (var m in Lib.Mounts.Where(m => m.Online).ToList())
        {
            if (!_offered.Add(m.Ref.StorageId)) continue;
            var watched = Lib.Watched(m);
            if (watched.Count == 0) continue;
            var w = new CleanupWindow(m, watched) { Owner = this };
            w.ShowDialog();
            if (w.Choice == 0) continue;
            var (n, bytes) = Lib.DeleteWatched(m, w.ToDelete);
            Status($"Удалено {n} просмотренных видео, освобождено {Ui.Gb(bytes)}");
            await RefreshAll();
            if (w.Choice == 2)
            {
                Target.SelectedItem = m;
                var pick = new PickWindow(m) { Owner = this };
                if (pick.ShowDialog() == true) { Nav.SelectedIndex = 3; await RunQueue(); }
            }
        }
    }

    // ---------- the wizard ----------

    async void OnPick(object sender, RoutedEventArgs e)
    {
        var m = Target.SelectedItem as Mount ?? Lib.Mounts.FirstOrDefault(x => x.Online);
        if (m is not { Online: true }) { Status("Сначала подключите флешку или папку (Настройки)"); Nav.SelectedIndex = 4; return; }
        var w = new PickWindow(m) { Owner = this };
        if (w.ShowDialog() != true) return;
        Nav.SelectedIndex = 3;
        await RunQueue();
    }

    // ---------- downloads ----------

    void OnTargetChanged(object sender, SelectionChangedEventArgs e) => UpdatePlan();

    void UpdatePlan()
    {
        var plans = Lib.Mounts.Where(m => Downloader.Rows.Any(r => r.Item.StorageId == m.Ref.StorageId && r.Item.Include && r.Item.Status == Q.Pending))
            .Select(m => (m, Downloader.Check(m))).ToList();
        PlanText.Text = plans.Count == 0 ? (Downloader.Rows.Count == 0 ? "Очередь пуста. Нажмите «Подборка» или вставьте ссылку." : "В очереди нечего качать.")
            : string.Join("\n", plans.Select(p => $"{p.m.Name}: {p.Item2.Text}"));
        // the theme's own text colour when all is well (the system "control text" is black even on the dark theme)
        if (plans.Any(p => !p.Item2.Ok)) PlanText.Foreground = Brushes.IndianRed; else PlanText.ClearValue(TextBlock.ForegroundProperty);
        ConfirmBtn.IsEnabled = !Downloader.Running && plans.Count > 0 && plans.All(p => p.Item2.Ok);
        StopBtn.IsEnabled = Downloader.Running;
        FindBtn.IsEnabled = !Downloader.Running;
        var left = Downloader.Rows.Count(r => r.Item.Include && r.Item.Status is Q.Pending or Q.Working or Q.Checking or Q.Copying);
        NavDownloads.Text = left > 0 ? $"Загрузки ({left})" : "Загрузки";
        UpdateSummary();
    }

    DateTime? _runStart;
    long _runBytes;

    void UpdateSummary()
    {
        var (done, total, failed, bd, bt, speed, _) = Downloader.Summary();
        // time left from the average pace since the start: downloading, cutting and copying all count, so it neither
        // vanishes while a file is being copied nor jumps with every new video
        if (!Downloader.Running) _runStart = null;
        else if (_runStart == null) (_runStart, _runBytes) = (DateTime.Now, bd);
        var secs = (DateTime.Now - (_runStart ?? DateTime.Now)).TotalSeconds;
        var pace = secs > 30 && bd > _runBytes ? (bd - _runBytes) / secs : speed;
        var eta = pace > 0 && bt > bd ? (bt - bd) / pace : 0;
        RetryBtn.Visibility = failed > 0 && !Downloader.Running ? Visibility.Visible : Visibility.Collapsed;
        SumTitle.Text = total == 0 ? "Очередь пуста" : Downloader.Running ? $"Скачано {done} из {total}" : done == total ? $"Готово: {done} из {total}" : $"В очереди {total - done}";
        SumDetail.Text = total == 0 ? "Нажмите «Подборка» или вставьте ссылку ниже." : string.Join(" · ", new[] {
            $"{Ui.Gb(bd)} из {Ui.Gb(bt)}",
            speed > 0 ? $"{Ui.Gb((long)speed)}/с" : null,
            eta > 0 ? $"осталось ~{Downloader.Eta(eta)}" : null,
            failed > 0 ? $"ошибок: {failed}" : null }.OfType<string>());
        SumBar.Value = bt > 0 ? bd * 100.0 / bt : 0;
    }

    void OnLinksChanged(object sender, TextChangedEventArgs e) => LinksHint.Visibility = Links.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    void OnRemoveRow(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not QRow r) return;
        if (r.Item.Status is Q.Working or Q.Checking or Q.Copying) { Status("Это видео сейчас качается — сначала «Пауза»"); return; }
        Downloader.Rows.Remove(r);
        Downloader.Changed();
    }

    async void OnFind(object sender, RoutedEventArgs e)
    {
        if (Target.SelectedItem is not Mount t) { Status("Сначала добавьте флешку или папку в «Настройках»"); return; }
        if (!t.Online) { Status($"{t.Name} не подключено"); return; }
        FindBtn.IsEnabled = false;
        try { await Downloader.Add(Links.Text.Split('\n', ' '), t.Ref.StorageId, Status); Links.Clear(); }
        finally { FindBtn.IsEnabled = true; UpdatePlan(); }
    }

    async void OnConfirm(object sender, RoutedEventArgs e) => await RunQueue();

    async Task RunQueue()
    {
        ConfirmBtn.IsEnabled = false; StopBtn.IsEnabled = true;
        var run = Downloader.Run(Status);
        UpdatePlan();
        await run;
        await RefreshAll();
    }

    async void OnRetry(object sender, RoutedEventArgs e)
    {
        foreach (var r in Downloader.Rows.Where(r => r.Item.Include && r.Item.Status == Q.Failed)) { r.Item = r.Item with { Status = Q.Pending }; r.Progress = ""; }
        Downloader.Changed();
        await RunQueue();
    }

    void OnStop(object sender, RoutedEventArgs e) { Downloader.Stop(); Status("Пауза. Недокачанное продолжится с того же места — нажмите «Скачать»"); }

    void OnClean(object sender, RoutedEventArgs e)
    {
        foreach (var r in Downloader.Rows.Where(r => r.Item.Status is Q.Done or Q.Skipped || (r.Item.Status == Q.Failed && !r.Item.Include)).ToList()) Downloader.Rows.Remove(r);
        Downloader.Changed();
    }

    // ---------- settings ----------

    void RefreshStorageUi()
    {
        var sel = Target.SelectedItem as Mount;
        Target.ItemsSource = Lib.Mounts.ToList();
        Target.SelectedItem = sel != null && Lib.Mounts.Contains(sel) ? sel : Lib.Mounts.FirstOrDefault(m => m.Online) ?? Lib.Mounts.FirstOrDefault();
        StorageList.ItemsSource = Lib.Mounts.Select(m => $"{m.Name} — {(m.Phone is { } ph ? "по кабелю: " + ph.Root.TrimStart('\\') : m.Ref.Root)} — папки: {string.Join(", ", m.Storage.Folders.Select(f => (f.Path == "." ? "вся папка" : f.Path) + (f.Role == "short" ? " (Shorts)" : "")))}").ToList();
        var caps = Lib.Mounts.SelectMany(m => m.Others).Where(d => d.Kind == "android" && d.Caps != null).DistinctBy(d => d.DeviceId).ToList();
        var p = Downloader.ProfileForDevices();
        CapsText.Text = caps.Count == 0 ? "Телефоны ещё не подключались — после первого запуска KouTube на телефоне здесь будет видно, какие форматы он тянет."
            : "Устройства: " + string.Join("; ", caps.Select(d => $"{d.DeviceName}: " + string.Join(", ", d.Caps!.Video
                .Where(v => v.Mime is "video/av01" or "video/x-vnd.on2.vp9" or "video/avc").Select(v => $"{v.Mime[6..].Replace("x-vnd.on2.", "")}{(v.Hw ? "" : " (программно)")}").Distinct())))
              + $". Будет выбрано: {string.Join(" → ", p.Codecs.Select(FormatChoice.Pretty))}"
              + (p.SlowCodecs.Count > 0 ? $"; {string.Join(", ", p.SlowCodecs.Select(FormatChoice.Pretty))} — только до 30 кадров/с (не на всех устройствах есть аппаратное декодирование)." : ".");
    }

    void LoadSettingsUi()
    {
        var c = Lib.Config;
        MaxRes.SelectedIndex = c.Profile.MaxShortSide switch { <= 480 => 0, <= 720 => 1, _ => 2 };
        CodecAv1.IsChecked = c.Profile.Codecs.Contains("av01");
        CodecVp9.IsChecked = c.Profile.Codecs.Contains("vp09");
        CodecH264.IsChecked = c.Profile.Codecs.Contains("avc1");
        Fps60.IsChecked = c.Profile.MaxFps >= 60;
        SubLangs.Text = string.Join(",", c.SubLangs);
        AutoSubs.IsChecked = c.AutoSubs;
        foreach (CheckBox cb in SbPanel.Children) cb.IsChecked = c.SponsorRemove.Contains((string)cb.Tag);
        PreciseCuts.IsChecked = c.PreciseCuts;
        TempDir.Text = Lib.TempDir;
        CommentsN.Text = c.Comments.ToString();
    }

    Mount? SelectedStorage => StorageList.SelectedIndex >= 0 && StorageList.SelectedIndex < Lib.Mounts.Count ? Lib.Mounts[StorageList.SelectedIndex] : null;

    void OnStorageSelected(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedStorage is { } m) ReserveGb.Text = (m.Storage.Budget.ReserveBytes / (double)(1L << 30)).ToString("0.#");
    }

    void OnSaveSettings(object sender, RoutedEventArgs e)
    {
        var codecs = new List<string>();
        if (CodecAv1.IsChecked == true) codecs.Add("av01");
        if (CodecVp9.IsChecked == true) codecs.Add("vp09");
        if (CodecH264.IsChecked == true) codecs.Add("avc1");
        if (codecs.Count == 0) { Status("Нужен хотя бы один формат"); return; }
        Lib.Update(c => c with
        {
            Profile = c.Profile with { MaxShortSide = MaxRes.SelectedIndex switch { 0 => 480, 2 => 1080, _ => 720 }, Codecs = codecs, MaxFps = Fps60.IsChecked == true ? 60 : 30 },
            SubLangs = SubLangs.Text.Split(',', ' ').Select(s => s.Trim()).Where(s => s.Length > 0).ToList(),
            AutoSubs = AutoSubs.IsChecked == true,
            SponsorRemove = SbPanel.Children.OfType<CheckBox>().Where(cb => cb.IsChecked == true).Select(cb => (string)cb.Tag).ToList(),
            PreciseCuts = PreciseCuts.IsChecked == true,
            TempDir = string.IsNullOrWhiteSpace(TempDir.Text) ? null : TempDir.Text.Trim(),
            Comments = int.TryParse(CommentsN.Text, out var cn) ? Math.Clamp(cn, 0, 1000) : 100,
        });
        if (SelectedStorage is { Online: true } m && double.TryParse(ReserveGb.Text.Replace('.', ','), out var gb) && gb >= 0)
        {
            m.Storage = m.Storage with { UpdatedAt = Format.Now(), Budget = m.Storage.Budget with { ReserveBytes = (long)(gb * (1L << 30)) } };
            Format.WriteAtomic(Path.Combine(m.Ml, "storage.json"), Format.Seal(Format.ToJson(m.Storage)));
        }
        RefreshStorageUi(); UpdatePlan();
        Status("Настройки сохранены");
    }

    async void OnAddStorage(object sender, RoutedEventArgs e)
    {
        var d = new Microsoft.Win32.OpenFolderDialog { Title = "Флешка или папка с видео" };
        if (d.ShowDialog(this) != true) return;
        var root = d.FolderName;
        var (existing, cands) = Lib.Inspect(root);
        var folders = existing?.Folders ?? AskFolders(cands);
        if (folders == null) return;
        var m = Lib.Add(root, folders);
        RefreshStorageUi();
        Nav.SelectedIndex = 0;
        await RefreshAll();
        Status($"{m.Name}: подключено, {m.Docs.Count} видео");
        await OfferCleanup();
    }

    /// <summary>
    /// A phone on the USB cable. Its library is the folder KouTube on the phone was given, so we look for that
    /// (.koutube up to two levels deep) instead of asking for a folder Windows can't pick on a phone.
    /// </summary>
    async void OnAddPhone(object sender, RoutedEventArgs e)
    {
        Status("Ищу телефоны на кабеле…");
        List<Mtp.PhoneLibrary> found;
        try { found = await Task.Run(Mtp.Find); }
        catch (Exception ex) { Status("Не удалось опросить телефоны: " + ex.Message); return; }
        if (found.Count == 0)
        {
            Status("");
            MessageBox.Show(this, "Библиотеку KouTube на телефоне не нашёл.\n\n" +
                "1. Подключите телефон кабелем и на телефоне в уведомлении «USB» выберите «Передача файлов».\n" +
                "2. В KouTube на телефоне выберите папку для видео («Настройки» → «Флешка или папка»), например Movies/KouTube.\n" +
                "3. Нажмите «Телефон по кабелю» ещё раз.", "KouTube", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var pick = found.Count == 1 ? found[0] : AskPhoneLibrary(found);
        if (pick == null) { Status(""); return; }
        var m = Lib.AddPhone(pick);
        RefreshStorageUi();
        Nav.SelectedIndex = 0;
        await RefreshAll();
        Status(m.Online ? $"{m.Name}: подключено по кабелю, {m.Docs.Count} видео" : $"{m.Name}: {m.Error}");
        await OfferCleanup();
    }

    Mtp.PhoneLibrary? AskPhoneLibrary(List<Mtp.PhoneLibrary> found)
    {
        var w = new Window { Title = "Какую библиотеку подключить?", Owner = this, SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        var list = new ListBox { MinWidth = 420, Margin = new Thickness(18, 18, 18, 0),
            ItemsSource = found.Select(f => $"{f.Device}: {f.Path.TrimStart('\\')}").ToList(), SelectedIndex = 0 };
        var ok = new Button { Content = "Подключить", IsDefault = true, Margin = new Thickness(18, 12, 18, 18), HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 120 };
        ok.Click += (_, _) => w.DialogResult = true;
        w.Content = new StackPanel { Children = { list, ok } };
        return w.ShowDialog() == true && list.SelectedIndex >= 0 ? found[list.SelectedIndex] : null;
    }

    List<Folder>? AskFolders(List<Lib.Candidate> c)
    {
        var w = new Window { Title = "Какие папки смотреть?", Owner = this, SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        var sp = new StackPanel { Margin = new Thickness(18) };
        sp.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 460, Margin = new Thickness(0, 0, 0, 10),
            Text = c.Count == 0 ? "Видео здесь пока нет — будет подключена вся папка, новые видео лягут в неё."
                                : "Отметьте папки с видео. «Shorts» — для папки с короткими вертикальными роликами. Остальное на носителе программа не трогает." });
        var rows = c.Select(x => (x, On: new CheckBox { Content = $"{(x.Path == "." ? "Эта папка" : x.Path)} — {x.Count} видео", IsChecked = true, MinWidth = 300 },
            Short: new CheckBox { Content = "Shorts", IsChecked = x.Path.Contains("short", StringComparison.OrdinalIgnoreCase) })).ToList();
        foreach (var r in rows)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
            row.Children.Add(r.On); row.Children.Add(r.Short); sp.Children.Add(row);
        }
        var ok = new Button { Content = "Подключить", IsDefault = true, Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 120 };
        ok.Click += (_, _) => w.DialogResult = true;
        sp.Children.Add(ok);
        w.Content = sp;
        if (w.ShowDialog() != true) return null;
        var f = rows.Where(r => r.On.IsChecked == true).Select(r => new Folder(r.x.Path, r.Short.IsChecked == true ? "short" : "auto")).ToList();
        return f.Count > 0 ? f : [new Folder(".", "auto")];
    }

    void OnRemoveStorage(object sender, RoutedEventArgs e)
    {
        if (SelectedStorage is not { } m) return;
        if (MessageBox.Show(this, $"Отключить «{m.Name}»? Файлы на носителе не трогаются.", "KouTube", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        Lib.Remove(m);
        RefreshStorageUi(); ShowLibrary();
    }

    async void OnUpdateYtDlp(object sender, RoutedEventArgs e)
    {
        ToolsText.Text = "обновляю…";
        ToolsText.Text = "yt-dlp: " + await Task.Run(Tools.UpdateYtDlp);
    }

    void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        Downloader.Stop();
        Downloader.Save();
        foreach (var m in Lib.Mounts) Lib.Flush(m);
    }
}
