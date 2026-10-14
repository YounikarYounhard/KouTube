// The download queue: links → candidates (with the exact format and size) → user confirms →
// yt-dlp into a temp folder on the PC → SponsorBlock cuts → ffprobe check → copied onto the
// storage under a temporary name → renamed → registered. A half-downloaded file never shows up
// in the library, and a power cut just resumes from yt-dlp's .part file next time.

using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KouTube;

public record QueueItem
{
    public string Id { get; init; } = "";
    public string Url { get; init; } = "";
    public string Title { get; init; } = "";
    public string? Channel { get; init; }
    public double DurationSec { get; init; }
    public string? Published { get; init; }
    public string Kind { get; init; } = "video";
    public string? Spec { get; init; }
    public string? Label { get; init; }
    public long Bytes { get; init; }
    public bool Approx { get; init; }
    public double SbCutSec { get; init; }
    public bool? SbMarked { get; init; }
    public string? StorageId { get; init; }
    public bool Include { get; init; } = true;
    public string Status { get; init; } = Q.Pending;
    public string? Source { get; init; }
    public string? Reason { get; init; }
    public string? ThumbUrl { get; init; }
    public List<Choice>? Variants { get; init; }
    public List<string>? Tags { get; init; }

    /// <summary>Expected size on the stick: SponsorBlock cuts shrink it roughly in proportion.</summary>
    public long Expected => DurationSec > 0 ? (long)(Bytes * Math.Max(0.05, 1 - SbCutSec / DurationSec)) : Bytes;
}

public static class Q
{
    public const string Pending = "ожидает", Skipped = "уже есть или просмотрено", Done = "готово", Failed = "ошибка",
        Working = "загрузка", Checking = "проверка", Copying = "копирование на носитель";
}

/// <summary>A queue row for the UI: the record plus live progress as numbers.</summary>
public sealed class QRow(QueueItem item) : INotifyPropertyChanged
{
    QueueItem _item = item;
    string _progress = "";
    double _pct; double _speed; double _eta; string _stage = "";
    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new(n));

    public QueueItem Item { get => _item; set { _item = value; Changed(""); } }
    /// <summary>Ticking an "already have / watched" row is the explicit "download again" the rules allow.</summary>
    public bool Include
    {
        get => _item.Include;
        set
        {
            _item = _item with { Include = value, Status = value && _item.Status == Q.Skipped ? Q.Pending : _item.Status };
            Changed(""); Downloader.Changed();
        }
    }
    /// <summary>Free text under the status: an error in plain words, or a note.</summary>
    public string Progress { get => _progress; set { _progress = value; Changed(); Changed(nameof(Detail)); } }

    public void SetProgress(double fraction, double speed, double eta, string stage)
    {
        _pct = Math.Clamp(fraction, 0, 1) * 100; _speed = speed; _eta = eta; _stage = stage;
        Changed(nameof(Percent)); Changed(nameof(Detail)); Changed(nameof(BytesDone));
    }

    public void SetStage(string stage, double? percent = null)
    {
        _stage = stage; _speed = 0; _eta = 0;
        if (percent is { } p) _pct = p;
        Changed(nameof(Percent)); Changed(nameof(Detail));
    }

    public double Percent => _item.Status == Q.Done ? 100 : _item.Status is Q.Pending or Q.Skipped ? 0 : _pct;
    public double Speed => _item.Status == Q.Working ? _speed : 0;
    public long BytesDone => _item.Status == Q.Done ? _item.Expected : (long)(_item.Expected * Percent / 100);

    public string Status => _item.Status switch
    {
        Q.Working or Q.Checking or Q.Copying => _stage.Length > 0 ? _stage : _item.Status,
        _ => _item.Status,
    };
    /// <summary>waiting grey · working YouTube blue · done green · error red</summary>
    public string StatusColor => _item.Status switch
    {
        Q.Done => "#2BA640", Q.Failed => "#FF4E45", Q.Working or Q.Checking or Q.Copying => "#3EA6FF", _ => "#909090",
    };
    public string Detail => _item.Status switch
    {
        Q.Working when _speed > 0 => $"{Percent:0}% · {Ui.Gb((long)_speed)}/с" + (_eta > 0 ? $" · осталось {Downloader.Eta(_eta)}" : ""),
        Q.Working or Q.Checking or Q.Copying => Percent > 0 ? $"{Percent:0}%" : "",
        _ => _progress,
    };
    public string Title => _item.Title;
    public string Channel => _item.Channel ?? "";
    public string Kind => _item.Kind == "short" ? "Shorts" : "Видео";
    public string Duration => _item.DurationSec > 0 ? TimeSpan.FromSeconds(_item.DurationSec).ToString(_item.DurationSec >= 3600 ? @"h\:mm\:ss" : @"m\:ss") : "";
    /// <summary>"212 МБ → ≈ 207 МБ без рекламы" rather than two numbers that disagree for no visible reason.</summary>
    public string Size => _item.Bytes <= 0 ? "размер узнаю при загрузке"
        : _item.SbCutSec > 1 && _item.Expected < _item.Bytes ? $"{(_item.Approx ? "≈ " : "")}{Ui.Gb(_item.Bytes)} → ≈ {Ui.Gb(_item.Expected)} без рекламы"
        : (_item.Approx ? "≈ " : "") + Ui.Gb(_item.Expected);
    public string Sb => _item.SbMarked switch { true => "реклама: вырежу " + TimeSpan.FromSeconds(_item.SbCutSec).ToString(@"m\:ss"), false => "рекламу никто не разметил", _ => "" };
    public string Why => string.Join(" · ", new[] { _item.Source, _item.Reason }.Where(s => !string.IsNullOrEmpty(s)));
    public string? Thumb => _item.ThumbUrl;
    System.Windows.Media.Imaging.BitmapImage? _pic;
    /// <summary>The cover decoded at the row's size (160 px), made when the row first shows.</summary>
    public System.Windows.Media.ImageSource? Pic => _pic ??= Thumb is { } u && Uri.TryCreate(u, UriKind.Absolute, out var uri) ? Small(uri) : null;
    static System.Windows.Media.Imaging.BitmapImage Small(Uri u)
    {
        var b = new System.Windows.Media.Imaging.BitmapImage();
        b.BeginInit(); b.UriSource = u; b.DecodePixelWidth = 160; b.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; b.EndInit();
        return b;
    }
    public List<Choice> Variants => _item.Variants ?? [];
    public Choice? Selected
    {
        get => Variants.FirstOrDefault(v => v.Spec == _item.Spec);
        set { if (value != null) { _item = _item with { Spec = value.Spec, Label = value.Label, Bytes = value.Bytes, Approx = value.Approx }; Changed(""); Downloader.Changed(); } }
    }
}

public static class Downloader
{
    static readonly string QueuePath = Path.Combine(Lib.LocalDir, "queue.json");
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    public static readonly System.Collections.ObjectModel.ObservableCollection<QRow> Rows = [];
    public static event Action? OnChanged;
    public static void Changed() { Save(); OnChanged?.Invoke(); }

    static Process? _proc;
    static CancellationTokenSource? _cts;
    public static bool Running => _cts != null;

    public static void Load()
    {
        if (!File.Exists(QueuePath)) return;
        foreach (var i in Format.FromJson<List<QueueItem>>(File.ReadAllText(QueuePath)) ?? [])
            Rows.Add(new QRow(i.Status is Q.Working or Q.Checking or Q.Copying ? i with { Status = Q.Pending } : i));
    }

    public static string Eta(double sec) => sec >= 3600 ? $"{(int)(sec / 3600)} ч {(int)(sec % 3600 / 60)} мин" : sec >= 60 ? $"{(int)(sec / 60)} мин" : $"{(int)sec} с";

    /// <summary>The line on top of the downloads page: how many, how much, how fast, how long.</summary>
    public static (int Done, int Total, int Failed, long BytesDone, long BytesTotal, double Speed, double EtaSec) Summary()
    {
        var rows = Rows.Where(r => r.Item.Include && r.Item.Status != Q.Skipped).ToList();
        var total = rows.Sum(r => r.Item.Expected);
        var done = rows.Sum(r => r.BytesDone);
        var speed = rows.Sum(r => r.Speed);
        return (rows.Count(r => r.Item.Status == Q.Done), rows.Count, rows.Count(r => r.Item.Status == Q.Failed), done, total, speed,
            speed > 0 ? (total - done) / speed : 0);
    }

    public static void Save() => Format.WriteAtomic(QueuePath, Format.ToJson(Rows.Select(r => r.Item).ToList()));

    // ---------- links → candidates ----------

    static readonly Regex Single = new(@"(?:youtube\.com/(?:watch\?.*v=|shorts/|live/)|youtu\.be/)([A-Za-z0-9_-]{11})");

    /// <summary>Video, Shorts, playlist or channel link → the videos behind it (just ids and titles, fast).</summary>
    public static List<(string Id, string Url, string? Title, string Kind)> Expand(string url, int limit)
    {
        url = url.Trim();
        if (Single.Match(url) is { Success: true } m)
            return [(m.Groups[1].Value, url, null, url.Contains("/shorts/") ? "short" : "video")];
        var j = Tools.Json(Tools.YtDlp, ["--flat-playlist", "-J", "--playlist-end", limit.ToString(), "--no-warnings", url], 180_000);
        var outList = new List<(string, string, string?, string)>();
        void Walk(JsonElement e)
        {
            if (!e.TryGetProperty("entries", out var es) || es.ValueKind != JsonValueKind.Array) return;
            foreach (var x in es.EnumerateArray())
            {
                if (x.TryGetProperty("entries", out _)) { Walk(x); continue; }   // channel → its tabs
                var id = x.TryGetProperty("id", out var i) ? i.GetString() : null;
                if (id is not { Length: 11 }) continue;
                var u = x.TryGetProperty("url", out var uu) ? uu.GetString() ?? "" : "";
                outList.Add((id, u.StartsWith("http") ? u : "https://www.youtube.com/watch?v=" + id,
                    x.TryGetProperty("title", out var t) ? t.GetString() : null, u.Contains("/shorts/") ? "short" : "video"));
            }
        }
        Walk(j);
        return outList.DistinctBy(x => x.Item1).Take(limit).ToList();
    }

    /// <summary>Full info for one video: formats with sizes, our pick, the alternatives and the SponsorBlock cut.</summary>
    public static async Task<QueueItem> Probe(string url, string kind, string? source, string? reason, string storageId, bool account = false)
    {
        var j = await Task.Run(() => Tools.Json(Tools.YtDlp, [.. account ? Account.CookieArgs() : [], "-J", "--no-playlist", "--no-warnings", url], 180_000));
        string? S(string n) => j.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var id = S("id")!;
        var sec = j.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0;
        var fs = j.GetProperty("formats").EnumerateArray().Select(YtFormat.From).ToList();
        var w = j.TryGetProperty("width", out var ww) && ww.ValueKind == JsonValueKind.Number ? ww.GetInt32() : (int?)null;
        var h = j.TryGetProperty("height", out var hh) && hh.ValueKind == JsonValueKind.Number ? hh.GetInt32() : (int?)null;
        var isShort = kind == "short" || S("media_type") == "short" || url.Contains("/shorts/") || (h > w && sec <= 180);
        var pick = FormatChoice.Pick(fs, ProfileForDevices(), sec);
        var (cut, marked) = await SponsorBlock(id);
        var date = S("upload_date");
        return new QueueItem
        {
            Id = id, Url = S("webpage_url") ?? url, Title = S("title") ?? id, Channel = S("channel") ?? S("uploader"), DurationSec = sec,
            Published = date is { Length: 8 } ? $"{date[..4]}-{date[4..6]}-{date[6..]}" : null,
            Kind = isShort ? "short" : "video", Spec = pick?.Spec, Label = pick?.Label, Bytes = pick?.Bytes ?? 0, Approx = pick?.Approx ?? true,
            SbCutSec = cut, SbMarked = marked, StorageId = storageId, Source = source, Reason = reason,
            ThumbUrl = $"https://i.ytimg.com/vi/{id}/mqdefault.jpg", Variants = FormatChoice.Variants(fs, sec),
            Status = pick == null ? Q.Failed : Q.Pending, Include = pick != null,
            Tags = j.TryGetProperty("tags", out var tg) && tg.ValueKind == JsonValueKind.Array
                ? tg.EnumerateArray().Select(t => t.GetString()).OfType<string>().Take(30).ToList() : null,
        };
    }

    /// <summary>
    /// The profile, trimmed to what every phone/tablet that wrote to the storages can play: a codec
    /// none of them decodes (even in software) is dropped, so the AV1 default can't produce dead files.
    /// </summary>
    public static Profile ProfileForDevices()
    {
        var p = Lib.Config.Profile;
        var devs = Lib.Mounts.SelectMany(m => m.Others).Where(d => d.Kind == "android" && d.Caps?.Video.Count > 0).ToList();
        if (devs.Count == 0) return p;
        string Mime(string fam) => fam switch { "av01" => "video/av01", "vp09" => "video/x-vnd.on2.vp9", "avc1" => "video/avc", "hevc" => "video/hevc", _ => "" };
        var ok = p.Codecs.Where(c => devs.All(d => d.Caps!.Video.Any(v => v.Mime == Mime(c) && v.MaxW >= 1280))).ToList();
        var slow = ok.Where(c => devs.Any(d => !d.Caps!.Video.Any(v => v.Mime == Mime(c) && v.Hw))).ToList();
        return ok.Count > 0 ? p with { Codecs = ok, SlowCodecs = slow } : p;
    }

    /// <summary>Seconds SponsorBlock will cut with the profile's categories; marked=false when nobody tagged the video.</summary>
    static async Task<(double Cut, bool? Marked)> SponsorBlock(string id)
    {
        try
        {
            var cats = JsonSerializer.Serialize(Lib.Config.SponsorRemove);
            var r = await Http.GetAsync($"https://sponsor.ajay.app/api/skipSegments?videoID={id}&categories={Uri.EscapeDataString(cats)}");
            if (r.StatusCode == System.Net.HttpStatusCode.NotFound) return (0, false);
            if (!r.IsSuccessStatusCode) return (0, null);
            var a = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
            var segs = a.EnumerateArray().Where(s => s.GetProperty("actionType").GetString() == "skip")
                .Select(s => (s.GetProperty("segment")[0].GetDouble(), s.GetProperty("segment")[1].GetDouble())).OrderBy(s => s.Item1).ToList();
            double total = 0, end = 0;   // merge overlaps
            foreach (var (s, e) in segs) { if (e <= end) continue; total += e - Math.Max(s, end); end = e; }
            return (total, segs.Count > 0);
        }
        catch (Exception) { return (0, null); }
    }

    // ---------- space (the reserve is never broken) ----------

    public record Plan(long Free, long Total, long QueueBytes, long After, long Reserve, bool Ok, string Text);

    public static Plan Check(Mount m)
    {
        var mine = Rows.Where(r => r.Item.Include && r.Item.Status == Q.Pending && r.Item.StorageId == m.Ref.StorageId).Select(r => r.Item).ToList();
        var q = (long)(mine.Sum(i => i.Expected) * 1.05);   // 5% for estimates being estimates
        var sp = m.Space();
        if (sp == null) return new(0, 0, q, 0, 0, false, "Носитель не подключён");
        var (free, total) = sp.Value;
        var b = m.Storage.Budget;
        var after = free - q;
        var msg = $"Очередь {mine.Count} шт., ≈ {Ui.Gb(q)} · свободно {Ui.Gb(free)} из {Ui.Gb(total)} · останется {Ui.Gb(after)} · резерв {Ui.Gb(b.ReserveBytes)}";
        if (after < b.ReserveBytes) return new(free, total, q, after, b.ReserveBytes, false, msg + " — не хватает места: уберите часть очереди или выберите профиль меньше");
        var allowance = b.TotalBytes ?? (m.UsedBy(false) + m.UsedBy(true) + free - b.ReserveBytes);
        foreach (var shorts in new[] { false, true })
        {
            var limit = (long)(allowance * (shorts ? b.ShortsShare : 1 - b.ShortsShare));
            var used = m.UsedBy(shorts) + (long)(mine.Where(i => (i.Kind == "short") == shorts).Sum(i => i.Expected) * 1.05);
            if (used > limit) return new(free, total, q, after, b.ReserveBytes, false,
                msg + $" — превышен лимит для {(shorts ? "Shorts" : "видео")}: {Ui.Gb(used)} из {Ui.Gb(limit)}");
        }
        return new(free, total, q, after, b.ReserveBytes, true, msg);
    }

    // ---------- the run ----------

    public static void Stop() { _cts?.Cancel(); try { _proc?.Kill(true); } catch (Exception) { } }

    public static async Task Run(Action<string> status)
    {
        if (_cts != null || !await Tools.Wait(status)) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        // null = done, or not worth another try; otherwise yt-dlp's raw error
        async Task<string?> Go(QRow row, bool account)
        {
            var m = Lib.Mounts.FirstOrDefault(x => x.Ref.StorageId == row.Item.StorageId);
            if (m == null || !m.Online) { Set(row, Q.Failed, "носитель не подключён"); return null; }
            try
            {
                if (row.Item.Spec == null)
                {
                    Set(row, Q.Working, "выбираю формат…");
                    var it = row.Item;
                    var q = await Probe(it.Url, it.Kind, it.Source, it.Reason, it.StorageId!, account);
                    Ui.OnUi(() => row.Item = q with { Include = true, Status = q.Status == Q.Failed ? Q.Failed : Q.Pending });
                    if (q.Status == Q.Failed) return null;
                }
                if (!Check(m).Ok) { Set(row, Q.Failed, "нет места — резерв нарушился бы"); return null; }
                Set(row, Q.Done, await One(row, m, token, account) ?? "");
                return null;
            }
            catch (OperationCanceledException) { Set(row, Q.Pending, "остановлено — продолжится с того же места"); return null; }
            catch (Exception e) { Set(row, Q.Failed, Human(e.Message)); return e.Message; }
        }
        try
        {
            var again = new List<(QRow Row, string Error)>();
            void Done() => status($"Готово {Rows.Count(r => r.Item.Status == Q.Done)} из {Rows.Count(r => r.Item.Include)}");
            // a Short is a few MB, yet each one waits on YouTube for seconds: six of them at a time; videos go one by one
            using var lanes = new SemaphoreSlim(6);
            var shorts = new List<Task>();
            async Task Lane(QRow row)
            {
                try { if (await Go(row, false) is { } e && Passing(e)) lock (again) again.Add((row, e)); Done(); }
                finally { lanes.Release(); }
            }
            foreach (var row in Rows.ToList())
            {
                if (token.IsCancellationRequested) break;
                if (!row.Item.Include || row.Item.Status != Q.Pending) continue;
                if (row.Item.Kind == "short")
                {
                    try { await lanes.WaitAsync(token); } catch (OperationCanceledException) { break; }
                    shorts.Add(Lane(row));
                    continue;
                }
                if (await Go(row, false) is { } err && Passing(err)) lock (again) again.Add((row, err));
                Done();
            }
            await Task.WhenAll(shorts);
            // YouTube's 403 and "are you a bot" usually pass minutes later: one more go at the end — after a 403 with a
            // fresh yt-dlp, after "not a bot" with your YouTube sign-in if there is one
            if (again.Count > 0 && !token.IsCancellationRequested)
            {
                if (again.Any(x => x.Error.Contains("403"))) { status("Обновляю yt-dlp перед второй попыткой…"); await Task.Run(Tools.UpdateYtDlp); }
                foreach (var (row, err) in again)
                {
                    if (token.IsCancellationRequested) break;
                    Set(row, Q.Pending, "вторая попытка…");
                    if (await Go(row, Bot(err) && Account.SignedIn) != null)
                        Ui.OnUi(() => row.Progress += " Вторая попытка тоже не удалась — нажмите «Повторить ошибки» позже.");
                }
            }
        }
        finally { _cts = null; _proc = null; Changed(); }
    }

    static void Set(QRow r, string status, string progress) => Ui.OnUi(() => { r.Item = r.Item with { Status = status }; r.Progress = progress; Changed(); });

    static async Task<string?> One(QRow row, Mount m, CancellationToken ct, bool account = false)
    {
        var it = row.Item;
        var tmp = Directory.CreateDirectory(Path.Combine(Lib.TempDir, it.Id)).FullName;
        var c = Lib.Config;
        var args = new List<string>
        {
            "-f", it.Spec ?? "bv*+ba/b", "--no-playlist", "--newline", "--no-mtime", "--no-warnings",
            "-o", Path.Combine(tmp, "%(uploader).30B - %(title).170B.%(ext)s"),
            "--embed-metadata", "--embed-chapters",
            // YouTube rate-limits subtitles (HTTP 429); a missing subtitle must not cost the whole video
            "--ignore-errors",
            "--progress", "--progress-template", "download:KT %(progress.downloaded_bytes)s|%(progress.total_bytes,progress.total_bytes_estimate)s|%(progress.speed)s|%(progress.eta)s",
            "--print", "after_move:KTFILE %(filepath)s",
        };
        // a Short is a minute of video: subtitles (2 s of waiting each), comments and SponsorBlock cost it more time than the video itself
        if (it.Kind != "short")
        {
            args.AddRange(["--write-subs", "--sub-langs", string.Join(",", c.SubLangs) + ",-live_chat", "--convert-subs", "vtt", "--sleep-subtitles", "2",
                "--sponsorblock-mark", "all"]);
            if (c.AutoSubs) args.Add("--write-auto-subs");
            args.AddRange(Lib.CommentArgs());
            if (c.SponsorRemove.Count > 0) args.AddRange(["--sponsorblock-remove", string.Join(",", c.SponsorRemove)]);
        }
        if (c.PreciseCuts) args.Add("--force-keyframes-at-cuts");
        if (account) args.AddRange(Account.CookieArgs());
        args.Add(it.Url);

        Set(row, Q.Working, "запуск…");
        string? file = null, note = null;
        var log = new List<string>();
        long streamsDone = 0, lastTotal = 0, lastDone = 0;
        var err = new List<string>();
        var psi = Tools.Start(Tools.YtDlp, args);
        using (var p = Process.Start(psi)!)
        {
            _proc = p;
            using var reg = ct.Register(() => { try { p.Kill(true); } catch (Exception) { } });
            var errTask = Task.Run(async () => { string? l; while ((l = await p.StandardError.ReadLineAsync()) != null) lock (err) err.Add(l); });
            string? line;
            while ((line = await p.StandardOutput.ReadLineAsync()) != null)
            {
                lock (log) log.Add(line);
                if (line.StartsWith("KTFILE ")) file = line["KTFILE ".Length..].Trim();
                else if (line.StartsWith("KT "))
                {
                    var f = line["KT ".Length..].Split('|');
                    static double N(string x) => double.TryParse(x, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
                    if (f.Length < 4) continue;
                    long done = (long)N(f[0]), total = (long)N(f[1]);
                    if (done < lastDone) streamsDone += lastTotal;          // the sound starts after the picture
                    lastDone = done; lastTotal = total;
                    var all = Math.Max(it.Bytes, streamsDone + total);
                    double frac = (streamsDone + done) / (double)Math.Max(1, all), speed = N(f[2]), eta = N(f[3]);
                    Ui.OnUi(() => row.SetProgress(frac * 0.9, speed, eta, "скачивание"));
                }
                else if (line.Contains("[SponsorBlock]") || line.Contains("[ModifyChapters]")) Ui.OnUi(() => row.SetStage("вырезаю рекламу…", 92));
                else if (line.Contains("[Merger]")) Ui.OnUi(() => row.SetStage("склеиваю видео и звук…", 90));
            }
            await p.WaitForExitAsync(CancellationToken.None);
            await errTask;
            ct.ThrowIfCancellationRequested();
            KeepLog(it.Id, log, err);
            var errors = err.Where(l => l.Contains("ERROR")).ToList();
            var serious = errors.Where(l => !l.Contains("subtitles", StringComparison.OrdinalIgnoreCase)).ToList();
            // not printed (an old yt-dlp, an odd name)? the finished file is still in the temp folder
            if (file == null || !File.Exists(file))
                file = Directory.EnumerateFiles(tmp).Where(f => Format.IsVideoName(Path.GetFileName(f)))
                    .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (serious.Count > 0) throw new IOException(Human(Tools.LastLine(string.Join("\n", serious))!));
            if (file == null) throw new IOException("yt-dlp ничего не скачал. Подробности — в журнале: " + LogPath(it.Id));
            if (errors.Count > 0) note = "часть субтитров не скачалась — YouTube ограничил запросы";
        }

        // check before it goes anywhere near the library
        Set(row, Q.Checking, "");
        Ui.OnUi(() => row.SetStage("проверяю файл…", 95));
        var pr = Tools.Json(Tools.FfProbe, ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", file]);
        var streams = pr.GetProperty("streams").EnumerateArray().Select(s => s.GetProperty("codec_type").GetString()).ToList();
        var dur = double.Parse(pr.GetProperty("format").GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        if (!streams.Contains("video") || !streams.Contains("audio")) throw new IOException("в файле нет видео или звука");
        if (it.DurationSec > 0 && dur < (it.DurationSec - it.SbCutSec) * 0.9)
            throw new IOException($"длительность {dur:0} с вместо ≈{it.DurationSec - it.SbCutSec:0} с — файл недокачан или повреждён");
        if (it.SbCutSec > 10 && Lib.Config.SponsorRemove.Count > 0 && dur > it.DurationSec - it.SbCutSec / 2)
            throw new IOException("SponsorBlock не вырезал сегменты — файл не принят");

        // onto the storage: temporary name first, the real one only when the bytes are all there
        Set(row, Q.Copying, "");
        var where = m.Phone != null ? "копирую на телефон…" : "копирую на носитель…";
        Ui.OnUi(() => row.SetStage(where, 96));
        var rel = m.Storage.DownloadTo.GetValueOrDefault(it.Kind) ?? (it.Kind == "short" ? "Shorts" : ".");
        if (!m.Storage.Folders.Any(f => f.Path == rel || f.Path == "."))
        {   // a new Shorts folder must be scanned from now on
            m.Storage = m.Storage with { Folders = [.. m.Storage.Folders, new Folder(rel, it.Kind == "short" ? "short" : "video")], UpdatedAt = Format.Now() };
            Format.WriteAtomic(Path.Combine(m.Ml, "storage.json"), Format.Seal(Format.ToJson(m.Storage)));
        }
        var name = Path.GetFileName(file);
        var b = Path.GetFileNameWithoutExtension(file);
        var subs = new List<Sub>();
        string destRel;
        if (m.Phone is { } ph)
        {
            // over the cable: same temporary-name rule, the phone's own folders, progress from the bytes sent
            string RelOf(string n) => rel == "." ? n : rel + "/" + n;
            destRel = RelOf(name);
            for (var n = 2; await Task.Run(() => Mtp.Exists(ph, destRel), ct); n++) destRel = RelOf($"{b} ({n}){Path.GetExtension(name)}");
            var total = Math.Max(1, new FileInfo(file).Length);
            await Task.Run(() => Mtp.Upload(ph, file, destRel, done => Ui.OnUi(() => row.SetStage(where, 96 + done * 4.0 / total)), ct), ct);
            foreach (var s in Directory.EnumerateFiles(tmp, b + ".*.vtt"))
            {
                var lang = Path.GetFileName(s)[(b.Length + 1)..].Split('.')[0];
                var sr = RelOf(Path.GetFileNameWithoutExtension(destRel.Split('/')[^1]) + "." + lang + ".vtt");
                await Task.Run(() => Mtp.Upload(ph, s, sr), ct);
                subs.Add(new Sub(lang, sr));
            }
        }
        else
        {
            var dir = Directory.CreateDirectory(rel == "." ? m.Ref.Root : Path.Combine(m.Ref.Root, rel.Replace('/', '\\'))).FullName;
            var dest = Path.Combine(dir, name);
            for (var n = 2; File.Exists(dest); n++) dest = Path.Combine(dir, $"{b} ({n}){Path.GetExtension(name)}");
            await CopyAtomic(file, dest, row, ct);
            foreach (var s in Directory.EnumerateFiles(tmp, b + ".*.vtt"))
            {
                var lang = Path.GetFileName(s)[(b.Length + 1)..].Split('.')[0];
                var sd = Path.Combine(dir, Path.GetFileNameWithoutExtension(dest) + "." + lang + ".vtt");
                File.Copy(s, sd, true);
                subs.Add(new Sub(lang, Rel(m, sd)));
            }
            destRel = Rel(m, dest);
            if (Format.FileKey(dest) != Format.FileKey(file)) throw new IOException("копия на носителе не совпала с файлом");
        }
        string? thumb = null;
        var td = Directory.CreateDirectory(Path.Combine(m.Ml, "thumbs")).FullName;
        try
        {
            if (await Lib.YouTubeCover(it.Id, it.Kind == "short", ct) is { } cover)
            {
                await File.WriteAllBytesAsync(Path.Combine(td, it.Id + ".jpg"), cover, ct);
                thumb = "thumbs/" + it.Id + ".jpg";
            }
        }
        catch (Exception) { }
        if (thumb == null)
        {   // no picture on YouTube: a frame from the file while it is still here (a phone on the cable can't be read back cheaply)
            await Task.Run(() => Lib.Frame(file, (long)(dur * 1000), Path.Combine(td, it.Id + ".frame.jpg")), ct);
            if (File.Exists(Path.Combine(td, it.Id + ".frame.jpg"))) thumb = "thumbs/" + it.Id + ".frame.jpg";
        }

        var comments = Lib.SaveComments(m, it.Id, tmp);
        var key = Format.FileKey(file);       // the same bytes now lie on the storage
        var vs = pr.GetProperty("streams").EnumerateArray().First(s => s.GetProperty("codec_type").GetString() == "video");
        var aus = pr.GetProperty("streams").EnumerateArray().First(s => s.GetProperty("codec_type").GetString() == "audio");
        Lib.PutMeta(m, key, new Meta
        {
            YtId = it.Id, Title = it.Title, Channel = it.Channel, Url = it.Url, Published = it.Published, Kind = it.Kind,
            DurationMs = (long)(dur * 1000), Width = vs.GetProperty("width").GetInt32(), Height = vs.GetProperty("height").GetInt32(),
            Vcodec = vs.GetProperty("codec_name").GetString(), Acodec = aus.GetProperty("codec_name").GetString(),
            Size = new FileInfo(file).Length, Path = destRel, Subs = subs.Count > 0 ? subs : null, Thumb = thumb,
            AddedAt = Format.Now(), Source = "manager", SponsorCutMs = (long)(Math.Max(0, it.DurationSec - dur) * 1000),
            Comments = comments?.Path, CommentCount = comments?.Count, Tags = it.Tags is { Count: > 0 } ? it.Tags : null,
        });
        foreach (var old in Lib.RedownloadRequests().Where(v => v.Meta.YtId == it.Id).ToList())
            Lib.SetFlags(old.Mount, old.Key, f => f with { Redownload = false });
        Lib.Flush(m);
        try { Directory.Delete(tmp, true); } catch (IOException) { }
        return note;
    }

    static string LogPath(string id) => Path.Combine(Directory.CreateDirectory(Path.Combine(Lib.LocalDir, "logs")).FullName, id + ".log");

    static void KeepLog(string id, List<string> o, List<string> e)
    {
        try { lock (o) lock (e) File.WriteAllLines(LogPath(id), o.Concat(["--- stderr ---"]).Concat(e)); } catch (IOException) { }
    }

    static bool Bot(string err) => err.Contains("not a bot", StringComparison.OrdinalIgnoreCase) || err.Contains("sign in to confirm", StringComparison.OrdinalIgnoreCase);

    /// <summary>Refusals that tend to pass: worth one more go at the end of the queue.</summary>
    static bool Passing(string err) => Bot(err) || new[] { "403", "429", "timed out", "connection", "unable to download webpage" }.Any(x => err.Contains(x, StringComparison.OrdinalIgnoreCase));

    /// <summary>yt-dlp's English errors in words a person can act on.</summary>
    public static string Human(string raw)
    {
        var s = raw.ToLowerInvariant();
        if (Bot(s)) return "YouTube попросил подтвердить, что вы не робот (так бывает с адресами VPN) — повторю в конце очереди.";
        if (s.Contains("video unavailable") || s.Contains("private video") || s.Contains("been removed") || s.Contains("terminated"))
            return "Видео недоступно: удалено, скрыто или закрыто автором.";
        if (s.Contains("age") && (s.Contains("restrict") || s.Contains("confirm your age"))) return "Видео 18+: YouTube не отдаёт его без входа в аккаунт.";
        if (s.Contains("http error 403")) return "YouTube не отдал файл (403) — повторю в конце очереди со свежим yt-dlp.";
        if (s.Contains("http error 429")) return "YouTube временно ограничил загрузки (429). Попробуйте позже.";
        if (s.Contains("requested format is not available")) return "Такого формата у видео нет — выберите другой в списке.";
        if (s.Contains("unable to download webpage") || s.Contains("timed out") || s.Contains("getaddrinfo") || s.Contains("connection") || s.Contains("network"))
            return "Нет связи с YouTube. Проверьте интернет и VPN.";
        if (s.Contains("no space left") || s.Contains("not enough space")) return "На диске не хватило места.";
        if (s.Contains("live event") || s.Contains("premiere")) return "Это трансляция или премьера — её ещё нельзя скачать.";
        return raw.Replace("ERROR: ", "").Trim();
    }

    static string Rel(Mount m, string full) => Path.GetRelativePath(m.Ref.Root, full).Replace('\\', '/');

    static async Task CopyAtomic(string src, string dest, QRow row, CancellationToken ct)
    {
        var part = dest + ".ktpart";
        await using (var i = File.OpenRead(src))
        await using (var o = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            var buf = new byte[1 << 20];
            long done = 0; int n;
            while ((n = await i.ReadAsync(buf, ct)) > 0)
            {
                await o.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                var pct = 96 + done * 4.0 / Math.Max(1, i.Length);
                Ui.OnUi(() => row.SetStage("копирую на носитель…", pct));
            }
            o.Flush(true);
        }
        if (new FileInfo(part).Length != new FileInfo(src).Length) { File.Delete(part); throw new IOException("копия не совпала по размеру"); }
        File.Move(part, dest);
    }

    // ---------- adding to the queue ----------

    public static async Task Add(IEnumerable<string> links, string storageId, Action<string> status, string? source = null, string? reason = null, int limit = 30)
    {
        var skip = Lib.DoNotDownload();
        var have = Rows.Select(r => r.Item.Id).ToHashSet();
        foreach (var link in links.Where(l => l.Trim().Length > 0))
        {
            List<(string Id, string Url, string? Title, string Kind)> found;
            try { status("Смотрю ссылку: " + link); found = await Task.Run(() => Expand(link, limit)); }
            catch (Exception e) { status($"Не удалось открыть {link}: {e.Message}"); continue; }
            var n = 0;
            foreach (var f in found.Where(f => !have.Contains(f.Id)))
            {
                status($"Проверяю {++n} из {found.Count}: {f.Title ?? f.Id}");
                try
                {
                    var q = await Probe(f.Url, f.Kind, source ?? (found.Count > 1 ? "по ссылке на канал/плейлист" : "по ссылке"), reason, storageId);
                    if (skip.Contains(q.Id)) q = q with { Status = Q.Skipped, Include = false };
                    Rows.Add(new QRow(q));
                    have.Add(q.Id);
                    Changed();
                }
                catch (Exception e) { status($"Пропущено {f.Id}: {e.Message}"); }
            }
        }
        status("Очередь готова — проверьте и нажмите «Подтвердить загрузку»");
    }
}
