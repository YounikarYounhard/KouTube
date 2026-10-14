// Self-check: the shared fixtures in spec/fixtures (the Android tests read the same files), then
// an end-to-end run of the library on copies of real videos, and with --online a real download.
// Exit code 0 = all good.   dotnet run --project windows/KouTube.Tests [-- --online]

using System.Text.Json;
using KouTube;

// the phone download test keeps one home (one device id), so re-runs don't leave a new device file on the phone each time
// KOUTUBE_TEST_TMP moves the copies of real videos off a full system drive
var home = Path.Combine(Environment.GetEnvironmentVariable("KOUTUBE_TEST_TMP") ?? Path.GetTempPath(),
    args.Contains("--mtp-download") ? "koutube-mtp-test" : "koutube-test-" + Guid.NewGuid().ToString("N")[..6]);
Environment.SetEnvironmentVariable("KOUTUBE_HOME", Path.Combine(home, "home"));   // before Lib is touched

// maintenance: fill covers, channel pictures and comments for a library folder, then exit
if (args is ["--enrich", var root, ..])
{
    var (ex, cands) = Lib.Inspect(root);
    var mount = Lib.Add(root, ex?.Folders ?? cands.Select(c => new Folder(c.Path)).ToList());
    Lib.Refresh(mount, Console.WriteLine);
    await Lib.FetchThumbs(mount, Console.WriteLine);
    await Lib.FetchAvatars(mount, Console.WriteLine);
    await Lib.FetchComments(mount, Console.WriteLine, 50);
    Lib.Flush(mount);
    foreach (var v in Lib.Videos()) Console.WriteLine($"{v.Title} | thumb {v.Meta.Thumb} | avatar {v.Meta.Avatar} | comments {v.Meta.Comments} ({v.Meta.CommentCount})");
    return 0;
}

// pictures of the PC windows, drawn off-screen (nothing appears on the user's desktop) — the phone's test library as content
if (args is ["--shots", var shotDir, ..])
{
    Directory.CreateDirectory(shotDir);
    var lib = Mtp.Find().FirstOrDefault(l => l.Path.EndsWith("KouTubeStick"));
    if (lib != null) { var pm = Lib.AddPhone(lib); Lib.Refresh(pm); }
    Recommend.Calibrate(Lib.Videos());
    Exception? fail = null;
    var ui = new Thread(() =>
    {
        try
        {
            // a plain application with KouTube's colours: App itself would run its startup and open a window on the desktop
            var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown, ThemeMode = System.Windows.ThemeMode.System };
            var up = new DirectoryInfo(AppContext.BaseDirectory);
            while (up != null && !File.Exists(Path.Combine(up.FullName, "windows", "KouTube", "App.xaml"))) up = up.Parent;
            var appXaml = File.ReadAllText(Path.Combine(up!.FullName, "windows", "KouTube", "App.xaml"));
            var res = appXaml[(appXaml.IndexOf("<Application.Resources>") + 23)..appXaml.IndexOf("</Application.Resources>")];
            app.Resources.MergedDictionaries.Add((System.Windows.ResourceDictionary)System.Windows.Markup.XamlReader.Parse(
                "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">" + res + "</ResourceDictionary>"));
            void Pump(double sec)
            {
                var frame = new System.Windows.Threading.DispatcherFrame();
                var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(sec) };
                t.Tick += (_, _) => { t.Stop(); frame.Continue = false; };
                t.Start();
                System.Windows.Threading.Dispatcher.PushFrame(frame);
            }
            void Save(System.Windows.Window w, string name)
            {
                var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)w.ActualWidth, (int)w.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bmp.Render(w);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                using var f = File.Create(Path.Combine(shotDir, name));
                enc.Save(f);
                Console.WriteLine("saved " + name);
            }
            void Hide(System.Windows.Window w) { w.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual; w.Left = -30000; w.Top = 0; w.ShowInTaskbar = false; w.ShowActivated = false; }
            // the app offers to clear watched videos in a modal window on start: picture it, then close it (nobody is there to click)
            var closer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            closer.Tick += (_, _) =>
            {
                foreach (var d in app.Windows.OfType<System.Windows.Window>().Where(w => w is CleanupWindow && w.IsLoaded).ToList()) { Save(d, "cleanup.png"); d.Close(); }
            };
            closer.Start();
            var mw = new MainWindow { Width = 1400, Height = 900 };
            Hide(mw);
            mw.Show();
            Pump(8);                                   // its refresh, covers and channel pictures
            var nav = (System.Windows.Controls.ListBox)mw.FindName("Nav");
            foreach (var (i, n) in new[] { (0, "main"), (1, "shorts"), (2, "history"), (3, "downloads"), (4, "settings") }) { nav.SelectedIndex = i; Pump(1); Save(mw, n + ".png"); }
            var pw = new PickWindow(Lib.Mounts.First());
            Hide(pw);
            pw.Show();
            Pump(2);
            Save(pw, "pick1.png");
            pw.Close(); mw.Close();
            app.Shutdown();
        }
        catch (Exception e) { fail = e; }
    });
    ui.SetApartmentState(ApartmentState.STA);
    ui.Start();
    ui.Join();
    if (fail != null) Console.WriteLine("FAIL " + fail);
    foreach (var mm in Lib.Mounts) Console.WriteLine($"mount: {mm.Name} | {mm.Ref.Root} | {mm.Ref.StorageId} | online {mm.Online} | {mm.Error}");
    return fail == null ? 0 : 1;
}

// a real download from YouTube straight onto the phone over the cable (a few Shorts — they stay there to watch)
if (args.Contains("--mtp-download"))
{
    var lib = Mtp.Find().FirstOrDefault(l => l.Path.EndsWith("KouTubeStick"));
    if (lib == null) { Console.WriteLine("FAIL the test library on the phone is not found"); return 1; }
    var m = Lib.AddPhone(lib);
    Lib.Refresh(m);
    var before = m.Docs.Count;
    await Downloader.Add(["https://www.youtube.com/channel/UCY6zVRa3Km52bsBmpyQnk6A/shorts"], m.Ref.StorageId, s => Console.WriteLine("  " + s), limit: 5);
    foreach (var r in Downloader.Rows) Console.WriteLine($"  queued: {r.Item.Title} · {r.Item.Label} ≈{r.Item.Expected / 1e6:0.0} MB · {r.Status}");
    await Downloader.Run(s => Console.WriteLine("  " + s));
    foreach (var r in Downloader.Rows) Console.WriteLine($"  result: {r.Item.Title} → {r.Status} {r.Detail}");
    Lib.Refresh(m);
    await Lib.FetchThumbs(m, s => Console.WriteLine("  " + s));
    Lib.Flush(m);
    var got = Lib.Videos().Where(v => v.Mount == m && v.Present && v.Meta.Source == "manager" && v.IsShort).ToList();
    foreach (var v in got) Console.WriteLine($"  on the phone: {v.Meta.Path} · {v.Meta.Width}x{v.Meta.Height} {v.Meta.Vcodec} · thumb {v.Meta.Thumb} · comments {v.Meta.CommentCount}");
    var fine = got.Count >= 3 && m.Docs.Count >= before
        && got.All(v => v.Meta.Thumb != null && m.Storage.Folders.Any(f => v.Meta.Path!.StartsWith(f.Path + "/")));
    Console.WriteLine(fine ? "ok   Shorts downloaded straight onto the phone, with covers, in a folder the phone scans" : "FAIL see above");
    return fine ? 0 : 1;
}

// a phone on the USB cable (MTP): find its library, read it, put a clip on it, clear it as watched — then tidy up
if (args.Contains("--mtp"))
{
    int ok = 0, bad = 0;
    void Expect(bool c, string what) { if (c) ok++; else bad++; Console.WriteLine((c ? "ok   " : "FAIL ") + what); }
    var libs = Mtp.Find();
    Console.WriteLine("found: " + string.Join("; ", libs.Select(l => $"{l.Device} {l.Path} «{l.Storage.Name}»")));
    var lib = libs.FirstOrDefault(l => l.Path.EndsWith("KouTubeStick"));
    Expect(lib != null, "the test library on the phone is found");
    if (lib == null) return 1;
    var m = Lib.AddPhone(lib);
    Lib.Refresh(m);
    Expect(m.Online && m.Docs.Count >= 3, $"its videos are read over the cable ({m.Docs.Count}, {m.Error})");
    Expect(m.Space() is { Free: > 0 }, $"free space on the phone: {Ui.Gb(m.Space()?.Free ?? 0)}");

    var clip = Path.Combine(home, "clip.mp4");
    Directory.CreateDirectory(home);
    Tools.Run(Tools.FfMpeg, ["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc=size=640x360:rate=25", "-t", "5", "-pix_fmt", "yuv420p", clip], 60_000);
    var rel = "Videos/KT-кабель-тест.mp4";
    long last = 0;
    Mtp.Upload(m.Phone!, clip, rel, n => last = n);
    var key = Format.FileKey(clip);
    Lib.PutMeta(m, key, new Meta { Title = "Тест по кабелю", Channel = "KouTube", Kind = "video", DurationMs = 5000, Size = new FileInfo(clip).Length, Path = rel, Source = "manager", AddedAt = Format.Now() });
    Lib.Flush(m);
    Expect(last == new FileInfo(clip).Length && Mtp.Exists(m.Phone!, rel) && !Mtp.Exists(m.Phone!, rel + ".ktpart"), "the clip is on the phone under its real name, progress counted every byte");
    Lib.Refresh(m);
    Expect(m.Docs.ContainsKey(key), "after a re-read the clip is in the library");

    Lib.Edit(m, key, i => i with { Watch = new Watch { Ts = Format.Now(), Status = Watch.Finished, FinishedAt = Format.Now() } });
    var watched = Lib.Watched(m).Where(v => v.Key == key).ToList();
    var (gone, _) = Lib.DeleteWatched(m, watched);
    Expect(gone == 1 && !Mtp.Exists(m.Phone!, rel), "clearing it as watched deletes it from the phone");
    Lib.Refresh(m);
    Expect(Lib.Videos().First(v => v.Key == key) is { Present: false, Status: "Удалено после просмотра" }, "the card stays as «удалено после просмотра»");

    // leave the phone as it was: no device file from this throwaway test install
    foreach (var f in new[] { "", ".bak", ".tmp" }) Mtp.Delete(m.Phone!, $".koutube/devices/{Lib.DeviceId}.json{f}");
    Console.WriteLine($"{ok} passed, {bad} failed");
    try { Directory.Delete(home, true); } catch (IOException) { }
    return bad == 0 ? 0 : 1;
}

var dir = new DirectoryInfo(AppContext.BaseDirectory);
while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "spec", "fixtures"))) dir = dir.Parent;
var fx = Path.Combine(dir?.FullName ?? throw new Exception("spec/fixtures not found"), "spec", "fixtures");
int failed = 0, passed = 0;
void Check(bool ok, string what) { if (ok) passed++; else { failed++; Console.WriteLine("FAIL: " + what); } }

// a whole pick on the real YouTube account (its cookies from the real home): history with watch progress,
// subscriptions, every list, ranking, 12 GB filled — prints what it would download and why
if (args.Contains("--account"))
{
    var real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KouTube", "youtube", "cookies.txt");
    File.Copy(real, Path.Combine(Directory.CreateDirectory(Path.Combine(Lib.LocalDir, "youtube")).FullName, "cookies.txt"));
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var hist = Account.History()!;
    var subs = Account.Subscriptions();
    Console.WriteLine($"history {hist.Count} (progress known {hist.Count(h => h.Percent != null)}, finished {hist.Count(h => h.Percent >= 90)}), subscriptions {subs.Count} — {sw.Elapsed.TotalSeconds:0}s");
    var it = Recommend.Learn([], hist, subs);
    var c = Recommend.Implicit(Lib.Config with { MaxAgeDays = 14 }, it);
    const long gb = 1L << 30;
    var (d, sd) = Recommend.Depth(c, it, 10 * gb, 2 * gb);
    Console.WriteLine($"implicit: {string.Join(", ", c.Sources.Select(s => s.Name))} | {string.Join(", ", c.Keywords)} | depth {d}/{sd}");
    var cands = Recommend.Candidates(c, (s, f) => { if (s.StartsWith("Не")) Console.WriteLine("  " + s); }, d, sd);
    var ranked = Recommend.Rank(cands, it, [.. hist.Select(h => h.Id)]);
    var sel = new Recommend.Selection(ranked, 10 * gb, 2 * gb, null, true).Fill();
    Console.WriteLine($"candidates {cands.Count}, ranked {ranked.Count}; picked {sel.Videos.Count} videos {Ui.Gb(sel.Used(false))} + {sel.Shorts.Count} Shorts {Ui.Gb(sel.Used(true))} — {sw.Elapsed.TotalSeconds:0}s");
    foreach (var pk in sel.Videos.Take(12)) Console.WriteLine($"  {pk.Score,5:0.0}  {pk.Title[..Math.Min(60, pk.Title.Length)]}  ← {pk.Reason}");
    Check(hist.Count(h => h.Percent != null) > hist.Count / 3, "the history knows how much of each video you watched");
    Check(sel.Used(false) + sel.Used(true) > 11.5 * gb, "12 GB are filled");
    Console.WriteLine($"{passed} passed, {failed} failed");
    return failed == 0 ? 0 : 1;
}


// ---------- shared fixtures ----------

foreach (var c in JsonDocument.Parse(File.ReadAllText(Path.Combine(fx, "filekey.json"))).RootElement.GetProperty("cases").EnumerateArray())
{
    int size = c.GetProperty("gen").GetProperty("size").GetInt32(), mod = c.GetProperty("gen").GetProperty("mod").GetInt32();
    var b = Enumerable.Range(0, size).Select(i => (byte)(i % mod)).ToArray();
    var head = b.Length < 2 * Format.KeyChunk ? b : b[..Format.KeyChunk];
    var tail = b[Math.Max(0, b.Length - Format.KeyChunk)..];
    Check(Format.FileKey(size, head, tail) == c.GetProperty("key").GetString(), "fileKey " + c.GetProperty("name"));
    var tmp = Path.GetTempFileName(); File.WriteAllBytes(tmp, b);
    Check(Format.FileKey(tmp) == c.GetProperty("key").GetString(), "fileKey from file " + c.GetProperty("name"));
    File.Delete(tmp);
}

Check(Format.Unseal(File.ReadAllText(Path.Combine(fx, "envelope-ok.json")))?.Contains("кириллица ✓") == true, "envelope ok");
Check(Format.Unseal(File.ReadAllText(Path.Combine(fx, "envelope-bad.json"))) == null, "envelope bad");
Check(Format.Unseal(File.ReadAllText(Path.Combine(fx, "envelope-truncated.json"))) == null, "envelope truncated");
Check(Format.Unseal(Format.Seal("{\"a\":\"ё\"}")) == "{\"a\":\"ё\"}", "seal round trip");

foreach (var c in JsonDocument.Parse(File.ReadAllText(Path.Combine(fx, "merge.json"))).RootElement.GetProperty("cases").EnumerateArray())
{
    var devices = c.GetProperty("devices").Deserialize<List<DeviceFile>>(Format.Json)!;
    var expected = c.GetProperty("expected").Deserialize<Dictionary<string, Item>>(Format.Json)!;
    var got = Format.Merge(devices);
    Check(got.Count == expected.Count && expected.All(e => got.TryGetValue(e.Key, out var g) && Format.ToJson(g) == Format.ToJson(e.Value)),
        "merge: " + c.GetProperty("name"));
}

var p = Path.Combine(Path.GetTempPath(), "koutube-atomic-test.json");
Format.WriteAtomic(p, Format.Seal("{\"writtenAt\":1}"));
Format.WriteAtomic(p, Format.Seal("{\"writtenAt\":2}"));
File.WriteAllText(p + ".tmp", "{\"sha256\":\"torn");   // a crash mid-write leaves junk behind
Check(Format.Newest<DeviceFile>(Format.ReadCandidates(p), d => d.Schema, d => d.WrittenAt)?.WrittenAt == 2, "atomic write keeps the newest verified copy");
foreach (var s in new[] { "", ".tmp", ".bak" }) File.Delete(p + s);

Check(Format.ParseName("Utopia Show - ЧТО БУДЕТ ПОСЛЕ？.webm") == ("Utopia Show", "ЧТО БУДЕТ ПОСЛЕ?"), "parseName");
Check(Format.DetectKind("auto", null, 720, 1280, 50_000) == "short", "kind short");
Check(Format.DetectKind("auto", null, 720, 1280, 600_000) == "video", "kind long portrait");
Check(!Format.IsVideoName("a.webm.part") && Format.IsVideoName("a.WEBM"), "video names");
Check(FormatChoice.Test() is null, "format choice: " + FormatChoice.Test());

// ---------- the library on real files (copies; the originals are only read) ----------

// copies of four real videos from a KouTube stick (KOUTUBE_TEST_STICK = the folder they are in); without it this part is skipped
var stickSrc = Environment.GetEnvironmentVariable("KOUTUBE_TEST_STICK") ?? "";
var src = new[] { "Vert Dider - Колонизация Марса не нужна [Fraser Cain].webm", "Thoisoi - Эфир - Самое ЛЕТУЧЕЕ Вещество на Земле!.webm",
    "Thoisoi - Что Случится, Если в Атмосфере будет 100% КИСЛОРОДА？.webm", "Vert Dider - Паутина крепче стали？ [Veritasium].webm" }
    .Select(n => Path.Combine(stickSrc, n)).ToArray();
if (src.All(File.Exists))
{
    var stick = Path.Combine(home, "stick");
    var vids = Directory.CreateDirectory(Path.Combine(stick, "ВИДОСИКИ")).FullName;
    Directory.CreateDirectory(Path.Combine(stick, "МУЗОН"));
    File.WriteAllText(Path.Combine(stick, "МУЗОН", "song.mp3"), "not ours");
    foreach (var f in src) File.Copy(f, Path.Combine(vids, Path.GetFileName(f)));

    var (existing, cands) = Lib.Inspect(stick);
    Check(existing == null && cands.Count == 1 && cands[0] is { Path: "ВИДОСИКИ", Count: 4 }, "inspect finds the video folder only");
    var m = Lib.Add(stick, [new Folder("ВИДОСИКИ")]);
    Lib.Refresh(m);
    Check(m.Online && m.Docs.Count == 4, $"scan finds 4 videos (got {m.Docs.Count}, {m.Error})");
    var mars = Lib.Videos().FirstOrDefault(v => v.Meta.YtId == "AsCdQvvzBNk");
    Check(mars is { Meta: { Channel: "Vert Dider", Source: "ytdlnis", Kind: "video", Width: 1280, Height: 720, DurationMs: > 600_000 } },
        "YTDLnis file recognised by its embedded URL: " + Format.ToJson(mars?.Meta));
    Lib.Flush(m);
    var mineOnStick = Path.Combine(stick, ".koutube", "devices", Lib.DeviceId + ".json");
    Check(Format.Unseal(File.ReadAllText(mineOnStick)) != null, "PC device file written and sealed");
    Check(Lib.DoNotDownload().SetEquals(["AsCdQvvzBNk", "O0py1oItvxY", "UnG0VI8y5kQ", "Z2mEj2QbJ1w"]) || Lib.DoNotDownload().Count == 4, "files on the stick are not downloaded again");

    // the phone watches Mars to the end and deletes it (what the Android app writes)
    var now = Format.Now();
    var phone = new DeviceFile
    {
        DeviceId = "android-test", Kind = "android", WrittenAt = now,
        Caps = new Caps { Video = [new Decoder("video/av01", false, 1920, 1080), new Decoder("video/x-vnd.on2.vp9", true, 3840, 2160)] },
        Items = new()
        {
            [mars!.Key] = new Item
            {
                Watch = new Watch { Ts = now, Status = Watch.Finished, FinishedAt = now },
                File = new FileState { Ts = now, State = FileState.AfterWatch, At = now },
                Flags = new Flags { Ts = now, NoAutoRedownload = true },
            },
        },
    };
    void WritePhone(DeviceFile d) => Format.WriteAtomic(Path.Combine(stick, ".koutube", "devices", "android-test.json"), Format.Seal(Format.ToJson(d)));
    WritePhone(phone);
    File.Delete(mars.Found!.FullPath);
    Lib.Refresh(m);
    var after = Lib.Videos().First(v => v.Key == mars.Key);
    Check(after is { Present: false, Status: "Удалено после просмотра", Meta.Title: not null }, "deleted after watching, card kept: " + after.Status);
    Check(Lib.DoNotDownload().Contains("AsCdQvvzBNk"), "watched and deleted video is not downloaded again");
    Check(File.Exists(Path.Combine(stick, "МУЗОН", "song.mp3")), "files outside the library are untouched");
    Check(Downloader.ProfileForDevices().Codecs.SequenceEqual(["av01", "vp09"]), "codecs follow the phone: " + string.Join(",", Downloader.ProfileForDevices().Codecs));

    // "Download again" on the phone
    WritePhone(phone with { WrittenAt = now + 10, Items = new() { [mars.Key] = phone.Items[mars.Key] with { Flags = new Flags { Ts = now + 10, Redownload = true } } } });
    Lib.Refresh(m);
    Check(!Lib.DoNotDownload().Contains("AsCdQvvzBNk") && Lib.RedownloadRequests().Any(v => v.Meta.YtId == "AsCdQvvzBNk"), "explicit «download again» lifts the block");

    // a file that vanished on its own is "missing", never "watched"
    var ether = Lib.Videos().First(v => v.Meta.YtId == "O0py1oItvxY");
    File.Delete(ether.Found!.FullPath);
    Lib.Refresh(m);
    Check(Lib.Videos().First(v => v.Key == ether.Key) is { Missing: true, Watch.Status: Watch.New }, "vanished file is missing, not watched");

    // a torn write of our own device file falls back to the previous good copy
    File.WriteAllText(mineOnStick, "{\"sha256\":\"0000");
    Lib.Refresh(m);
    Check(Lib.Videos().Any(v => v.Key == mars.Key), "torn device file: history still readable");

    // the phone only marks videos watched; the PC lists them all and clears only the ones the user ticks
    var oxygen = Lib.Videos().First(v => v.Meta.YtId == "UnG0VI8y5kQ");
    var web = Lib.Videos().First(v => v.Present && v.Meta.Channel == "Vert Dider");
    var t1 = Format.Now() + 100;
    WritePhone(phone with
    {
        WrittenAt = t1,
        Items = new()
        {
            [oxygen.Key] = new Item { Watch = new Watch { Ts = t1, Status = Watch.Finished, FinishedAt = t1 }, Flags = new Flags { Ts = t1, NoAutoRedownload = true } },
            [web.Key] = new Item { Watch = new Watch { Ts = t1, Status = Watch.Finished, FinishedAt = t1 }, Flags = new Flags { Ts = t1, Keep = true, NoAutoRedownload = true } },
        },
    });
    Lib.Refresh(m);
    var watched = Lib.Watched(m);
    Check(watched.Select(v => v.Key).ToHashSet().SetEquals([oxygen.Key, web.Key]), "watched list: every finished video (old «не удалять» flags no longer hide one)");
    var (gone, freed) = Lib.DeleteWatched(m, watched.Where(v => v.Key == oxygen.Key));
    Check(gone == 1 && freed > 0 && !File.Exists(oxygen.Found!.FullPath) && File.Exists(web.Found!.FullPath), "only the ticked file is deleted");
    Check(Lib.Videos().First(v => v.Key == oxygen.Key) is { Present: false, Status: "Удалено после просмотра", Meta.Title: not null },
        "after clearing: the card stays as «удалено после просмотра»");
    Check(Lib.DoNotDownload().Contains("UnG0VI8y5kQ"), "a cleared watched video is not downloaded again");

    // ---------- recommendations: the logic, offline ----------
    {
        var mk = (string ch, string title, string st) => new Video(Guid.NewGuid().ToString(), m, new Item
        {
            Meta = new Meta { Channel = ch, Title = title, DurationMs = 20 * 60_000 },
            Watch = new Watch { Status = st },
        }, null);
        var lib = new List<Video> { mk("Vert Dider", "Антиматерия часть два", Watch.Finished), mk("Vert Dider", "Паутина крепче стали", Watch.Finished),
            mk("Дюшес", "Прятки в лагере", Watch.New), mk("Дюшес", "Прятки хамелеон", Watch.Started) };
        var it = Recommend.Learn(lib);
        var t0 = DateTimeOffset.UtcNow;
        var picks0 = new List<Pick> {
            new("aaaaaaaaaaa", "u", "Прятки в торговом центре", "Дюшес", 1200, t0.AddDays(-3), "video", 0, "", "Дюшес"),
            new("bbbbbbbbbbb", "u", "Антиматерия: часть три", "Vert Dider", 1300, t0.AddDays(-3), "video", 0, "", "Vert Dider"),
            new("ccccccccccc", "u", "Уже скачано", "Vert Dider", 1300, t0.AddDays(-3), "video", 0, "", "Vert Dider"),
        };
        var ranked = Recommend.Rank(picks0, it, ["ccccccccccc"]);
        Check(ranked.Count == 2 && ranked[0].Id == "bbbbbbbbbbb", "channel you finish + shared topic ranks first: " + string.Join(" | ", ranked.Select(r => r.Id + " " + r.Score.ToString("0.0"))));
        Check(ranked[0].Reason.Contains("досмотрено 2 из 2") && ranked[0].Reason.Contains("антиматерия"), "the reason is explained: " + ranked[0].Reason);
        // 👎 and deleting unwatched teach it: that channel stops coming back
        lib.Add(new Video("x1", m, new Item { Meta = new Meta { Channel = "Мусор ТВ", Title = "Кликбейт шок" }, Flags = new Flags { Rating = -1 } }, null));
        lib.Add(new Video("x2", m, new Item { Meta = new Meta { Channel = "Мусор ТВ", Title = "Кликбейт сенсация" }, File = new FileState { State = FileState.ByUser } }, null));
        var it2 = Recommend.Learn(lib);
        var junk = Recommend.Rank([.. picks0, new Pick("ddddddddddd", "u", "Кликбейт опять", "Мусор ТВ", 600, t0, "video", 0, "", "Мусор ТВ")], it2, []);
        Check(junk.All(p => p.Id != "ddddddddddd"), "a disliked channel is not recommended again");

        // links pasted into «Подборка»: every ID found, a linked video always goes in, the rest fills what is left
        var ids = Recommend.VideoIds("https://youtu.be/AsCdQvvzBNk?t=3 и https://www.youtube.com/shorts/Y2k2TplU50A\nwww.youtube.com/watch?v=AsCdQvvzBNk&list=x");
        Check(ids.SequenceEqual(["AsCdQvvzBNk", "Y2k2TplU50A"]), "links → video IDs, duplicates once: " + string.Join(",", ids));
        var big = Recommend.ToPick(new Wanted("eeeeeeeeeee", "u", "Длинное по ссылке", "X", 3 * 3600, "video"));
        var sel = new Recommend.Selection(ranked, big.Estimate + ranked[0].Estimate, 0, [big]).Fill();
        Check(sel.Videos.Count == 2 && sel.Videos[0].Id == "eeeeeeeeeee" && sel.Videos[1].Id == ranked[0].Id,
            "a linked video comes first and the best of the rest fills the space left: " + string.Join(" | ", sel.Videos.Select(v => v.Id)));

        // «Заполнить полностью»: a video just over its share borrows from the Shorts; what no video fits gets more Shorts
        Pick V(string id, double min) => new(id, "u", id, "C", min * 60, null, "video", 0, "", "s");
        var pool = new List<Pick> { V("a0000000000", 20), V("b0000000000", 10) };
        pool.AddRange(Enumerable.Range(0, 60).Select(i => new Pick($"s{i:D10}", "u", "s", "C", 45, null, "short", 0, "", "s")));
        long vb = pool[0].Estimate + pool[1].Estimate / 2, sb = 40 * pool[2].Estimate;
        var plain = new Recommend.Selection(pool, vb, sb).Fill();
        var full = new Recommend.Selection(pool, vb, sb, null, true).Fill();
        Check(plain.Videos.Count == 1 && plain.Shorts.Count == 40, $"without the box every part keeps its own budget: {plain.Videos.Count} + {plain.Shorts.Count}");
        Check(full.Videos.Count == 2 && full.Used(false) + full.Used(true) <= vb + sb && full.Used(false) + full.Used(true) > vb + sb - pool[2].Estimate,
            $"with it the 10-minute video gets in and Shorts fill the rest to the brim: {full.Videos.Count} + {full.Shorts.Count}");
    }

    // ---------- picking from YouTube (network): a keyword with "only new" finds plenty ----------
    if (args.Contains("--online") || args.Contains("--picks"))
    {
        var kp = Recommend.Candidates(Lib.Config with { Sources = [], Keywords = ["путешествие"], MaxAgeDays = 14 }, (s, _) => { if (s.StartsWith("Не")) Console.WriteLine("  " + s); }, 60, 60);
        Console.WriteLine($"  «путешествие», только новые: {kp.Count(p => p.Kind == "video")} видео, {kp.Count(p => p.Kind == "short")} Shorts");
        Check(kp.Count(p => p.Kind == "video") >= 30 && kp.Count(p => p.Kind == "short") >= 20, "a keyword with «only new» finds plenty of videos and #hashtag Shorts");
    }

    // ---------- a real download (network) ----------
    if (args.Contains("--online"))
    {
        var found = Recommend.FindChannels("Vert Dider");
        Check(found.Any(f => f.Name == "Vert Dider" && f.Subscribers > 1_000_000), "channel search by name finds Vert Dider");
        Lib.Update(c => c with { Sources = [new Source(found.First(f => f.Name == "Vert Dider").Url, "Vert Dider", "channel")], PerSource = 5, MaxAgeDays = 60 });
        var picks = Recommend.Candidates(Lib.Config, (_, _) => { }, 5, 5);
        Console.WriteLine($"  candidates: {picks.Count} ({picks.Count(p => p.Kind == "short")} shorts)");
        Check(picks.Count > 0 && picks.Where(p => p.Published != null).All(p => (DateTimeOffset.UtcNow - p.Published!.Value).TotalDays <= 61), "candidates respect the date window");
        // covers: a frame cut while YouTube was out of reach gives way to YouTube's own big picture
        var web2 = Lib.Videos().First(v => v.Present && v.Meta.Channel == "Vert Dider" && v.Meta.YtId != null);
        var td = Directory.CreateDirectory(Path.Combine(m.Ml, "thumbs")).FullName;
        File.WriteAllBytes(Path.Combine(td, web2.Meta.YtId + ".frame.jpg"), [0xFF, 0xD8, 0xFF]);
        Lib.PutMeta(m, web2.Key, web2.Meta with { Thumb = "thumbs/" + web2.Meta.YtId + ".frame.jpg" });
        await Lib.FetchThumbs(m);
        var cov = Lib.Videos().First(v => v.Key == web2.Key).Meta.Thumb;
        Check(cov == $"thumbs/{web2.Meta.YtId}.jpg" && new FileInfo(Path.Combine(m.Ml, cov)).Length > 20_000
            && !File.Exists(Path.Combine(td, web2.Meta.YtId + ".frame.jpg")), $"a frame cover is replaced by YouTube's own (got {cov})");
        Console.WriteLine("online: downloading AsCdQvvzBNk with the economy profile and SponsorBlock…");
        Lib.Update(c => c with { Profile = new Profile() });
        await Downloader.Add(["https://www.youtube.com/watch?v=AsCdQvvzBNk"], m.Ref.StorageId, s => Console.WriteLine("  " + s));
        var row = Downloader.Rows.FirstOrDefault(r => r.Item.Id == "AsCdQvvzBNk");
        Console.WriteLine($"  queued: {row?.Item.Label} {row?.Item.Spec} ≈{(row?.Item.Expected ?? 0) / 1e6:0} MB, SponsorBlock cut {row?.Item.SbCutSec:0} s, status {row?.Status}");
        Check(row?.Item.SbMarked == true && row.Item.SbCutSec > 60, "SponsorBlock segments found");
        await Downloader.Run(s => Console.WriteLine("  " + s));
        Console.WriteLine($"  result: {row?.Status} {row?.Progress}");
        Lib.Refresh(m);
        var got = Lib.Videos().FirstOrDefault(v => v.Present && v.Meta.YtId == "AsCdQvvzBNk");
        Console.WriteLine("  meta: " + Format.ToJson(got?.Meta));
        Check(got is { Meta: { Source: "manager", Vcodec: "av1", Height: 720 } }, "downloaded as AV1 720p");
        Check(got?.Meta.DurationMs is > 630_000 and < 670_000, $"sponsor segments cut: {got?.Meta.DurationMs / 1000} s (YTDLnis file: 648 s)");
        Check(!Directory.EnumerateFiles(Path.Combine(stick, "ВИДОСИКИ")).Any(f => f.EndsWith(".ktpart")), "no temporary files left in the library");
    }
}
else Console.WriteLine("skip: the library test needs KOUTUBE_TEST_STICK — a folder with the four test videos");

Console.WriteLine($"{passed} passed, {failed} failed");
try { Directory.Delete(home, true); } catch (IOException) { }
return failed == 0 ? 0 : 1;
