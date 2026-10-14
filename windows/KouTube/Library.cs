// Storages on this PC (a stick, an SD card, any folder), the merged view of every device's
// history, the scanner, and the "never download this again" list.

using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KouTube;

public record StorageRef(string Root, string StorageId, string Name);

public record Found(string FullPath, string RelPath, long Size, DateTime Mtime, List<Sub> Subs, string? Thumb);

public record Video(string Key, Mount Mount, Item Item, Found? Found)
{
    public Meta Meta => Item.Meta ?? new Meta();
    public Watch Watch => Item.Watch ?? new Watch();
    public FileState FileState => Item.File ?? new FileState();
    public Flags Flags => Item.Flags ?? new Flags();
    public bool Present => Found != null;
    public bool Missing => Found == null && FileState.State == FileState.Present;
    public string Title => Meta.Title ?? Path.GetFileName(Found?.FullPath) ?? Key;
    public bool IsShort => Meta.Kind == "short";

    public string Status => !Present
        ? FileState.State switch { FileState.AfterWatch => "Удалено после просмотра", FileState.ByUser => "Удалено вручную", _ => "Файл пропал" }
        : Watch.Status switch { Watch.Finished => "Досмотрено", Watch.Started => "Начато", _ => "Не просмотрено" };
}

public sealed class Mount(StorageRef r)
{
    public StorageRef Ref { get; } = r;
    public StorageFile Storage { get; set; } = new() { StorageId = r.StorageId, Name = r.Name };
    public Dictionary<string, Item> Mine { get; } = [];
    public List<DeviceFile> Others { get; set; } = [];
    public Dictionary<string, Item> Merged { get; private set; } = [];
    public Dictionary<string, Found> Docs { get; set; } = [];
    public bool Online { get; set; }
    public string? Error { get; set; }
    public bool Dirty { get; set; }

    /// <summary>Set when the storage is a phone on the USB cable (no drive letter, see Mtp).</summary>
    public Mtp.Phone? Phone { get; } = Mtp.Parse(r.Root);
    public string Ml => Phone == null ? Path.Combine(Ref.Root, ".koutube")
        : Directory.CreateDirectory(Path.Combine(Lib.LocalDir, "mtp", Ref.StorageId, ".koutube")).FullName;
    public string Local => Directory.CreateDirectory(Path.Combine(Lib.LocalDir, "s", Ref.StorageId)).FullName;
    public string Name => Storage.Name ?? Ref.Name;

    public void Remerge() => Merged = Format.Merge([.. Others, new DeviceFile { DeviceId = Lib.DeviceId, Items = Mine }]);

    public (long Free, long Total)? Space()
    {
        if (Phone != null) return Mtp.Space(Phone);
        try { var d = new DriveInfo(Path.GetPathRoot(Ref.Root)!); return (d.AvailableFreeSpace, d.TotalSize); }
        catch (Exception) { return null; }
    }

    public long UsedBy(bool shorts) => Docs.Where(d => (Merged.GetValueOrDefault(d.Key)?.Meta?.Kind == "short") == shorts).Sum(d => d.Value.Size);
}

public record Config
{
    public string DeviceId { get; init; } = "windows-" + Guid.NewGuid().ToString("N")[..8];
    public List<StorageRef> Storages { get; init; } = [];
    public Profile Profile { get; init; } = new();
    public List<string> SubLangs { get; init; } = ["ru", "en"];
    public bool AutoSubs { get; init; } = true;
    /// <summary>The user's YTDLnis set: everything but Hook/Greetings.</summary>
    public List<string> SponsorRemove { get; init; } = ["sponsor", "selfpromo", "interaction", "intro", "outro", "preview", "filler", "music_offtopic"];
    public bool PreciseCuts { get; init; }
    public string? TempDir { get; init; }
    public List<Source> Sources { get; init; } = [];
    /// <summary>Latest N videos looked at per channel when picking.</summary>
    public int PerSource { get; init; } = 20;
    /// <summary>Skip videos younger than this — a few days give SponsorBlock time to mark the ads.</summary>
    public int? MinAgeDays { get; init; }
    public int? MaxAgeDays { get; init; }
    /// <summary>Topics searched on YouTube when picking ("майнкрафт моды", "космос").</summary>
    public List<string> Keywords { get; init; } = [];
    /// <summary>Words that keep a video out of every pick, in its title or channel name ("phonk", "asmr").</summary>
    public List<string> Exclude { get; init; } = [];
    /// <summary>Top comments fetched per video (0 = none, up to 1000). They weigh next to nothing.</summary>
    public int Comments { get; init; } = 100;
    /// <summary>Channel name → its picture, so each channel is looked up once.</summary>
    public Dictionary<string, string> Avatars { get; init; } = [];
    /// <summary>YouTube IDs the user already watched elsewhere: never picked again.</summary>
    public List<string> Seen { get; init; } = [];
    /// <summary>Videos added to «Подборка» by link: in every pick until they are downloaded.</summary>
    public List<Wanted> Wanted { get; init; } = [];
    /// <summary>IDs once added by link: a strong "more like this" for recommendations after they leave <see cref="Wanted"/>.</summary>
    public List<string> Linked { get; init; } = [];
    /// <summary>✕ in «Подборка»: not offered again, and its channel and topic go down.</summary>
    public List<Nope> NotInterested { get; init; } = [];
    /// <summary>How many GB the last pick filled — for «Как в прошлый раз».</summary>
    public double? LastPickGb { get; init; }
    /// <summary>«Заполнить место полностью»: videos and Shorts lend each other room so nothing is left empty.</summary>
    public bool FillAll { get; init; } = true;
    /// <summary>Take videos from «Смотреть позже» too. Off: many keep there what they watched long ago.</summary>
    public bool UseWatchLater { get; init; }
}

public record Wanted(string Id, string Url, string Title, string? Channel, double? DurationSec, string Kind);

public record Nope(string Id, string Title, string? Channel);

public record Source(string Url, string Name, string Kind, int? MinAgeDays = null, int? MaxAgeDays = null);

public static class Lib
{
    // KOUTUBE_HOME lets the tests run against a throwaway home instead of the real one
    public static readonly string LocalDir = Directory.CreateDirectory(Environment.GetEnvironmentVariable("KOUTUBE_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KouTube")).FullName;
    static string ConfigPath => Path.Combine(LocalDir, "config.json");

    public static Config Config { get; private set; } = Load();
    public static string DeviceId => Config.DeviceId;
    public static readonly List<Mount> Mounts = [];
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    static Config Load()
    {
        var c = File.Exists(ConfigPath) ? Format.FromJson<Config>(File.ReadAllText(ConfigPath)) ?? new Config() : new Config();
        if (!File.Exists(ConfigPath)) Save(c);
        return c;
    }

    static void Save(Config c) => Format.WriteAtomic(ConfigPath, Format.ToJson(c));

    public static void Update(Func<Config, Config> f) { Config = f(Config); Save(Config); }

    public static string TempDir => Directory.CreateDirectory(Config.TempDir ?? Path.Combine(
        // downloads go to the roomiest fixed drive, not necessarily C:
        DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).MaxBy(d => d.AvailableFreeSpace)!.RootDirectory.FullName,
        "KouTubeTemp")).FullName;

    public static void Init()
    {
        foreach (var r in Config.Storages.Where(r => Mounts.All(m => m.Ref.StorageId != r.StorageId))) { var m = new Mount(r); LoadLocal(m); Mounts.Add(m); }
    }

    // ---------- adding storages ----------

    public record Candidate(string Path, int Count);

    public static (StorageFile? Existing, List<Candidate> Candidates) Inspect(string root)
    {
        var existing = Format.Newest<StorageFile>(Format.ReadCandidates(Path.Combine(root, ".koutube", "storage.json")), s => s.Schema, s => s.UpdatedAt);
        var c = new List<Candidate>();
        int Count(string d) { try { return Directory.EnumerateFiles(d).Count(f => Format.IsVideoName(Path.GetFileName(f))); } catch (Exception) { return 0; } }
        if (Count(root) is var n and > 0) c.Add(new(".", n));
        foreach (var d in SafeDirs(root).Where(d => !Path.GetFileName(d).StartsWith('.')))
            if (Count(d) is var k and > 0) c.Add(new(Path.GetFileName(d), k));
        return (existing, c);
    }

    static IEnumerable<string> SafeDirs(string d)
    {
        try { return Directory.EnumerateDirectories(d).Where(x => (new DirectoryInfo(x).Attributes & FileAttributes.System) == 0).ToList(); }
        catch (Exception) { return []; }
    }

    public static Mount Add(string root, List<Folder> folders)
    {
        var ml = Directory.CreateDirectory(Path.Combine(root, ".koutube")).FullName;
        var sp = Path.Combine(ml, "storage.json");
        var sf = Format.Newest<StorageFile>(Format.ReadCandidates(sp), s => s.Schema, s => s.UpdatedAt);
        if (sf == null)
        {
            var label = TryLabel(root);
            sf = new StorageFile
            {
                StorageId = Guid.NewGuid().ToString(), Name = label, UpdatedAt = Format.Now(), Folders = folders,
                DownloadTo = new() { ["video"] = folders.FirstOrDefault(f => f.Role != "short")?.Path ?? ".",
                                     ["short"] = folders.FirstOrDefault(f => f.Role == "short")?.Path ?? "Shorts" },
            };
            Format.WriteAtomic(sp, Format.Seal(Format.ToJson(sf)));
        }
        var existing = Mounts.FirstOrDefault(m => m.Ref.StorageId == sf.StorageId);
        if (existing != null) return existing;
        var r = new StorageRef(root, sf.StorageId, sf.Name ?? "Хранилище");
        Update(c => c with { Storages = [.. c.Storages, r] });
        var mount = new Mount(r);
        LoadLocal(mount);
        Mounts.Add(mount);
        return mount;
    }

    /// <summary>A library found on a phone over the cable. The phone made it (KouTube there picked the folder), so it already has a storage.json.</summary>
    public static Mount AddPhone(Mtp.PhoneLibrary f)
    {
        var existing = Mounts.FirstOrDefault(m => m.Ref.StorageId == f.Storage.StorageId);
        if (existing != null) return existing;
        var r = new StorageRef(Mtp.RootOf(f.Serial, f.Path), f.Storage.StorageId, $"{f.Storage.Name ?? "Библиотека"} · {f.Device}");
        Update(c => c with { Storages = [.. c.Storages, r] });
        var mount = new Mount(r);
        LoadLocal(mount);
        Mounts.Add(mount);
        return mount;
    }

    static string TryLabel(string root)
    {
        try
        {
            var d = new DriveInfo(Path.GetPathRoot(root)!);
            var label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? d.Name.TrimEnd('\\') : d.VolumeLabel;
            return Path.GetPathRoot(root) == Path.GetFullPath(root) ? label : $"{label} · {Path.GetFileName(root.TrimEnd('\\'))}";
        }
        catch (Exception) { return Path.GetFileName(root.TrimEnd('\\')); }
    }

    public static void Remove(Mount m)
    {
        Mounts.Remove(m);
        Update(c => c with { Storages = c.Storages.Where(s => s.StorageId != m.Ref.StorageId).ToList() });
    }

    // ---------- local copy: history survives the stick being away ----------

    static void LoadLocal(Mount m)
    {
        T? Read<T>(string p) => File.Exists(p) ? Format.FromJson<T>(File.ReadAllText(p)) : default;
        if (Read<StorageFile>(Path.Combine(m.Local, "storage.json")) is { } s) m.Storage = s;
        if (File.Exists(Path.Combine(m.Local, "mine.json")) && Format.Unseal(File.ReadAllText(Path.Combine(m.Local, "mine.json"))) is { } body
            && Format.FromJson<DeviceFile>(body) is { } mine)
            foreach (var (k, v) in mine.Items) m.Mine[k] = v;
        var od = Directory.CreateDirectory(Path.Combine(m.Local, "others")).FullName;
        m.Others = Directory.EnumerateFiles(od, "*.json").Select(f => Read<DeviceFile>(f)).OfType<DeviceFile>().ToList();
        m.Remerge();
    }

    static void SaveLocal(Mount m) => Format.WriteAtomic(Path.Combine(m.Local, "mine.json"), Format.Seal(Format.ToJson(MineFile(m))));

    static DeviceFile MineFile(Mount m) => new()
    {
        DeviceId = DeviceId, DeviceName = Environment.MachineName, Kind = "windows",
        App = "KouTube на ПК " + typeof(Lib).Assembly.GetName().Version?.ToString(3), WrittenAt = Format.Now(), Items = new(m.Mine),
    };

    // ---------- reading a storage ----------

    /// <summary>Re-reads every device file and rescans the folders. Safe to call any time; offline storages just say so.</summary>
    public static void Refresh(Mount m, Action<string>? progress = null)
    {
        try
        {
            if (m.Phone != null) Mtp.Pull(m, m.Phone);
            else if (!Directory.Exists(m.Ref.Root)) throw new DirectoryNotFoundException();
            var ml = Directory.CreateDirectory(m.Ml).FullName;
            if (Format.Newest<StorageFile>(Format.ReadCandidates(Path.Combine(ml, "storage.json")), s => s.Schema, s => s.UpdatedAt) is { } sf
                && sf.UpdatedAt >= m.Storage.UpdatedAt)
            {
                m.Storage = sf;
                File.WriteAllText(Path.Combine(m.Local, "storage.json"), Format.ToJson(sf));
            }
            var dv = Directory.CreateDirectory(Path.Combine(ml, "devices")).FullName;
            var ids = Directory.EnumerateFiles(dv).Select(Path.GetFileName).OfType<string>()
                .Select(n => n.Replace(".json.tmp", ".json").Replace(".json.bak", ".json")).Where(n => n.EndsWith(".json"))
                .Select(n => n[..^5]).ToHashSet();
            var others = new List<DeviceFile>();
            var od = Directory.CreateDirectory(Path.Combine(m.Local, "others")).FullName;
            foreach (var id in ids)
            {
                var f = Format.Newest<DeviceFile>(Format.ReadCandidates(Path.Combine(dv, id + ".json")), d => d.Schema, d => d.WrittenAt);
                if (f == null) continue;
                if (id == DeviceId)
                    foreach (var (k, v) in Format.Merge([f, new DeviceFile { DeviceId = DeviceId, Items = m.Mine }])) m.Mine[k] = v;
                else { others.Add(f); File.WriteAllText(Path.Combine(od, id + ".json"), Format.ToJson(f)); }
            }
            m.Others = others;
            m.Remerge();
            m.Docs = m.Phone != null ? Mtp.Scan(m, m.Phone) : Scan(m, progress);
            m.Online = true; m.Error = null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException || m.Phone != null)
        {
            m.Online = false; m.Docs = [];
            m.Error = m.Phone != null ? (e is IOException ? e.Message : "не удалось прочитать телефон: " + e.Message) : "недоступно — флешка не подключена?";
        }
    }

    record ScanEntry(long Size, long Mtime, string Key);

    static Dictionary<string, Found> Scan(Mount m, Action<string>? progress)
    {
        var cachePath = Path.Combine(m.Local, "scan.json");
        var cache = File.Exists(cachePath) ? Format.FromJson<Dictionary<string, ScanEntry>>(File.ReadAllText(cachePath)) ?? [] : [];
        var next = new Dictionary<string, ScanEntry>();
        var found = new Dictionary<string, Found>();
        void Walk(string dir, string rel, string role, int depth)
        {
            var names = Directory.EnumerateFiles(dir).Select(Path.GetFileName).OfType<string>().ToList();
            foreach (var name in names.Where(Format.IsVideoName))
            {
                var full = Path.Combine(dir, name);
                var r = rel == "." ? name : rel + "/" + name;
                var fi = new FileInfo(full);
                var mt = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds();
                var key = cache.TryGetValue(r, out var c) && c.Size == fi.Length && c.Mtime == mt ? c.Key : Format.FileKey(full);
                next[r] = new(fi.Length, mt, key);
                if (found.ContainsKey(key)) continue;
                var b = Path.GetFileNameWithoutExtension(name);
                var subs = names.Where(n => n.StartsWith(b + ".") && (n.EndsWith(".vtt") || n.EndsWith(".srt")))
                    .Select(n => new Sub(n[(b.Length + 1)..].Split('.')[0], rel == "." ? n : rel + "/" + n)).ToList();
                var thumb = new[] { "jpg", "jpeg", "png", "webp" }.Select(e => b + "." + e).FirstOrDefault(names.Contains);
                found[key] = new Found(full, r, fi.Length, fi.LastWriteTime, subs, thumb == null ? null : Path.Combine(dir, thumb));
            }
            if (depth < 2)
                foreach (var d in SafeDirs(dir).Where(d => !Path.GetFileName(d).StartsWith('.')))
                    Walk(d, rel == "." ? Path.GetFileName(d) : rel + "/" + Path.GetFileName(d), role, depth + 1);
        }
        foreach (var f in m.Storage.Folders)
        {
            var d = f.Path == "." ? m.Ref.Root : Path.Combine(m.Ref.Root, f.Path.Replace('/', '\\'));
            if (Directory.Exists(d)) Walk(d, f.Path, f.Role, 0);
        }
        File.WriteAllText(cachePath, Format.ToJson(next));

        // anything we know too little about gets one ffprobe run (in parallel — this is the slow part)
        var todo = found.Where(e => m.Merged.GetValueOrDefault(e.Key)?.Meta is not { DurationMs: not null } meta || meta.Path != e.Value.RelPath).ToList();
        var done = 0;
        var probed = new System.Collections.Concurrent.ConcurrentDictionary<string, Meta>();
        Parallel.ForEach(todo, new ParallelOptions { MaxDegreeOfParallelism = 4 }, e =>
        {
            var have = m.Merged.GetValueOrDefault(e.Key)?.Meta;
            var meta = have is { DurationMs: not null } ? have with { Path = e.Value.RelPath } : Probe(e.Value, RoleOf(m, e.Value.RelPath), have);
            probed[e.Key] = meta;
            progress?.Invoke($"Анализ файлов: {Interlocked.Increment(ref done)} из {todo.Count}");
        });
        foreach (var (k, meta) in probed) PutMeta(m, k, meta);
        return found;
    }

    static string RoleOf(Mount m, string rel) =>
        m.Storage.Folders.Where(f => f.Path == "." || rel == f.Path || rel.StartsWith(f.Path + "/")).OrderByDescending(f => f.Path.Length).FirstOrDefault()?.Role ?? "auto";

    static readonly Regex YtId = new(@"(?:v=|youtu\.be/|shorts/)([A-Za-z0-9_-]{11})");

    /// <summary>Metadata from the file itself: YTDLnis/yt-dlp embed title, channel, date and the video URL (= the YouTube ID).</summary>
    static Meta Probe(Found f, string role, Meta? have)
    {
        var (channel, title) = Format.ParseName(f.FullPath);
        var meta = (have ?? new Meta()) with
        {
            Title = have?.Title ?? title, Channel = have?.Channel ?? channel, Size = f.Size, Path = f.RelPath,
            Subs = f.Subs.Count > 0 ? f.Subs : have?.Subs, AddedAt = have?.AddedAt ?? new DateTimeOffset(f.Mtime).ToUnixTimeMilliseconds(),
            Source = have?.Source ?? "scan",
        };
        try
        {
            var j = Tools.Json(Tools.FfProbe, ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", f.FullPath]);
            var fmt = j.GetProperty("format");
            var tags = fmt.TryGetProperty("tags", out var t) ? t.EnumerateObject().ToDictionary(p => p.Name.ToLowerInvariant(), p => p.Value.GetString() ?? "") : [];
            var streams = j.GetProperty("streams").EnumerateArray().ToList();
            var v = streams.FirstOrDefault(s => s.GetProperty("codec_type").GetString() == "video"
                && !(s.TryGetProperty("disposition", out var d) && d.TryGetProperty("attached_pic", out var ap) && ap.GetInt32() == 1));
            var a = streams.FirstOrDefault(s => s.GetProperty("codec_type").GetString() == "audio");
            string? Str(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var x) ? x.ToString() : null;
            int? w = int.TryParse(Str(v, "width"), out var ww) ? ww : null, h = int.TryParse(Str(v, "height"), out var hh) ? hh : null;
            double? fps = Str(v, "avg_frame_rate")?.Split('/') is [var n, var dd] && double.TryParse(n, out var nn) && double.TryParse(dd, out var dv) && dv > 0 ? Math.Round(nn / dv, 2) : null;
            long? dur = double.TryParse(Str(fmt, "duration"), System.Globalization.CultureInfo.InvariantCulture, out var ds) ? (long)(ds * 1000) : null;
            var url = tags.GetValueOrDefault("purl") ?? tags.GetValueOrDefault("comment");
            var id = url == null ? null : YtId.Match(url) is { Success: true } mm ? mm.Groups[1].Value : null;
            var date = tags.GetValueOrDefault("date");
            meta = meta with
            {
                YtId = meta.YtId ?? id, Url = meta.Url ?? (id != null ? url : null),
                Title = tags.GetValueOrDefault("title") is { Length: > 0 } tt ? tt : meta.Title,
                Channel = tags.GetValueOrDefault("artist") is { Length: > 0 } ar ? ar : meta.Channel,
                Published = meta.Published ?? (date is { Length: 8 } ? $"{date[..4]}-{date[4..6]}-{date[6..]}" : null),
                Category = meta.Category ?? tags.GetValueOrDefault("genre"),
                DurationMs = dur, Width = w, Height = h, Fps = fps,
                Vcodec = Str(v, "codec_name"), Acodec = Str(a, "codec_name"),
                Kind = Format.DetectKind(role, url, w, h, dur),
                Source = id != null && meta.Source == "scan" ? "ytdlnis" : meta.Source,
            };
        }
        catch (Exception) { meta = meta with { Kind = meta.Kind ?? Format.DetectKind(role, null, null, null, null) }; }
        return meta;
    }

    /// <summary>
    /// Covers, kept in .koutube/thumbs so phones and tablets show them offline: YouTube's own picture by ID
    /// when YouTube is reachable, otherwise a frame from the file. A frame or a small old cover is swapped
    /// for the real big one on a later run, once YouTube answers.
    /// </summary>
    public static async Task FetchThumbs(Mount m, Action<string>? progress = null)
    {
        if (!m.Online) return;
        var dir = Directory.CreateDirectory(Path.Combine(m.Ml, "thumbs")).FullName;
        var todo = m.Merged.Where(e => e.Value.Meta is { } mt && m.Docs.ContainsKey(e.Key)
            && (mt.Thumb == null || mt.YtId != null && !CoverTried.Contains(mt.YtId) && Upgradable(Path.Combine(m.Ml, mt.Thumb)))).ToList();
        var n = 0;
        var youtube = true;   // one failed connection is enough to know YouTube is out of reach today
        foreach (var (k, item) in todo)
        {
            var meta = item.Meta!;
            var name = (meta.YtId ?? k) + ".jpg";
            var path = Path.Combine(dir, name);
            string? got = null;
            if (youtube && meta.YtId != null)
                try
                {
                    CoverTried.Add(meta.YtId);
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    if (await YouTubeCover(meta.YtId, meta.Kind == "short", cts.Token) is { } b) { await File.WriteAllBytesAsync(path, b); got = name; }
                }
                catch (Exception) { youtube = false; }
            if (got == null && meta.Thumb != null) continue;               // keep the cover it has
            if (got == null)
            {
                var frame = (meta.YtId ?? k) + ".frame.jpg";
                await Task.Run(() => Frame(m.Docs[k].FullPath, meta.DurationMs, Path.Combine(dir, frame)));
                if (File.Exists(Path.Combine(dir, frame))) got = frame;
            }
            if (got == null) continue;
            if (meta.Thumb != null && meta.Thumb != "thumbs/" + got) try { File.Delete(Path.Combine(m.Ml, meta.Thumb)); } catch (IOException) { }
            PutMeta(m, k, meta with { Thumb = "thumbs/" + got });
            progress?.Invoke($"Обложки: {++n} из {todo.Count}");
        }
    }

    static readonly HashSet<string> CoverTried = [];

    /// <summary>
    /// A frame we cut ourselves, or a cover too small for a phone screen. Older frames have no ".frame" in the name
    /// but are exactly 480 px wide (ponytail: a size check, since nothing else marks them).
    /// </summary>
    static bool Upgradable(string file)
    {
        if (file.EndsWith(".frame.jpg")) return true;
        try
        {
            using var f = File.OpenRead(file);
            var w = System.Windows.Media.Imaging.BitmapDecoder.Create(f, System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation,
                System.Windows.Media.Imaging.BitmapCacheOption.None).Frames[0].PixelWidth;
            return w < 640;
        }
        catch (Exception) { return false; }
    }

    /// <summary>YouTube's own cover: 1280×720 for videos (the small one if that size doesn't exist), the vertical one for Shorts.</summary>
    public static async Task<byte[]?> YouTubeCover(string ytId, bool isShort, CancellationToken ct = default)
    {
        foreach (var name in isShort ? ["oardefault", "oar2"] : new[] { "hq720", "mqdefault" })
        {
            using var r = await Http.GetAsync($"https://i.ytimg.com/vi/{ytId}/{name}.jpg", ct);
            if (r.IsSuccessStatusCode) return await r.Content.ReadAsByteArrayAsync(ct);
        }
        return null;
    }

    /// <summary>
    /// Channel pictures for the round icons: one lookup per channel (video → channel → its avatar),
    /// saved as .koutube/avatars/&lt;channel id&gt;.jpg and remembered in the config. Quietly skipped offline.
    /// </summary>
    public static async Task FetchAvatars(Mount m, Action<string>? progress = null)
    {
        if (!m.Online) return;
        var dir = Directory.CreateDirectory(Path.Combine(m.Ml, "avatars")).FullName;
        await Task.Run(() => ShrinkAvatars(dir));
        var byChannel = m.Merged.Where(e => e.Value.Meta is { Channel: not null, Avatar: null } && m.Docs.ContainsKey(e.Key))
            .GroupBy(e => e.Value.Meta!.Channel!).ToList();
        foreach (var g in byChannel)
        {
            // a channel known here only by its Shorts gets a tiny picture; its first real video brings the full one
            var tiny = g.All(e => e.Value.Meta!.Kind == "short");
            var file = Config.Avatars.GetValueOrDefault(g.Key);
            // an empty file (a download cut off) counts as none
            if (file == null || !File.Exists(Path.Combine(dir, file)) || new FileInfo(Path.Combine(dir, file)).Length == 0 || !tiny && file.EndsWith(".s.jpg"))
            {
                var ytId = g.Select(e => e.Value.Meta!.YtId).FirstOrDefault(i => i != null);
                if (ytId == null) continue;
                progress?.Invoke($"Значок канала «{g.Key}»…");
                file = await Task.Run(() => DownloadAvatar(ytId, dir, tiny));
                if (file == null) return;                     // YouTube unreachable: try another day
                Update(c => c with { Avatars = new(c.Avatars) { [g.Key] = file } });
            }
            foreach (var (k, item) in g) PutMeta(m, k, item.Meta! with { Avatar = "avatars/" + file });
        }
    }

    /// <summary>
    /// Comments for videos that came without them (YTDLnis files, older downloads): a few per run in the
    /// background, so the library slowly gets them all. Quietly skipped offline.
    /// </summary>
    public static async Task FetchComments(Mount m, Action<string>? progress = null, int perRun = 15)
    {
        if (!m.Online || Config.Comments <= 0) return;
        var todo = m.Merged.Where(e => e.Value.Meta is { YtId: not null, Comments: null } && m.Docs.ContainsKey(e.Key)).Take(perRun).ToList();
        var n = 0;
        foreach (var (k, item) in todo)
        {
            var meta = item.Meta!;
            progress?.Invoke($"Комментарии: {++n} из {todo.Count}");
            var tmp = Directory.CreateDirectory(Path.Combine(TempDir, "c-" + meta.YtId)).FullName;
            var r = await Task.Run(() => Tools.Run(Tools.YtDlp, ["--skip-download", "--no-warnings", ..CommentArgs(),
                "-o", Path.Combine(tmp, "%(id)s.%(ext)s"), "https://www.youtube.com/watch?v=" + meta.YtId], 180_000));
            var saved = SaveComments(m, meta.YtId!, tmp);
            try { Directory.Delete(tmp, true); } catch (IOException) { }
            if (saved == null) { if (r.Code != 0) return; else continue; }       // offline: stop; no comments: next
            PutMeta(m, k, meta with { Comments = saved.Value.Path, CommentCount = saved.Value.Count });
        }
    }

    public static string[] CommentArgs() => Config.Comments <= 0 ? [] :
        ["--write-comments", "--write-info-json", "--extractor-args", $"youtube:max_comments={Math.Clamp(Config.Comments, 1, 1000)},all,0,0;comment_sort=top"];

    /// <summary>yt-dlp's info.json → a small list in .koutube/comments/&lt;id&gt;.json. Returns its path and the video's total count.</summary>
    public static (string Path, long Count)? SaveComments(Mount m, string ytId, string tmpDir)
    {
        var info = Directory.EnumerateFiles(tmpDir, "*.info.json").FirstOrDefault();
        if (info == null) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(info));
            var root = doc.RootElement;
            if (!root.TryGetProperty("comments", out var cs) || cs.ValueKind != System.Text.Json.JsonValueKind.Array) return null;
            string? S(System.Text.Json.JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
            long? L(System.Text.Json.JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number ? v.GetInt64() : null;
            bool B(System.Text.Json.JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.True;
            var list = cs.EnumerateArray().Where(c => S(c, "parent") is null or "root")
                .Select(c => new Comment(S(c, "author") ?? "", S(c, "text") ?? "", L(c, "like_count") ?? 0, L(c, "timestamp"), B(c, "is_pinned"), B(c, "author_is_uploader")))
                .Where(c => c.T.Length > 0).ToList();
            var dir = Directory.CreateDirectory(Path.Combine(m.Ml, "comments")).FullName;
            File.WriteAllText(Path.Combine(dir, ytId + ".json"), Format.ToJson(list));
            return ("comments/" + ytId + ".json", L(root, "comment_count") ?? list.Count);
        }
        catch (Exception) { return null; }
    }

    /// <summary>Pictures saved before 0.0.5 came full size (~550 KB, 983 px): 240 px looks the same on a card. Once, offline, file by file.</summary>
    public static void ShrinkAvatars(string dir)
    {
        foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*.jpg").Where(f => f.Length > 100_000 && !f.Name.EndsWith(".tmp.jpg")))
        {
            var tmp = f.FullName[..^4] + ".tmp.jpg";
            Tools.Run(Tools.FfMpeg, ["-v", "error", "-y", "-i", f.FullName, "-vf", "scale=240:-2", "-q:v", "3", tmp], 60_000);
            // swapped only when the small copy is really there: a pulled stick or a broken file leaves the old picture
            if (File.Exists(tmp) && new FileInfo(tmp).Length is > 0 and < 100_000) File.Move(tmp, f.FullName, true);
            else try { File.Delete(tmp); } catch (IOException) { }
        }
    }

    static string? DownloadAvatar(string ytId, string dir, bool tiny = false)
    {
        try
        {
            var v = Tools.Json(Tools.YtDlp, ["-J", "--no-playlist", "--no-warnings", "--skip-download", "https://www.youtube.com/watch?v=" + ytId], 60_000);
            var chId = v.GetProperty("channel_id").GetString()!;
            var name = chId + (tiny ? ".s.jpg" : ".jpg");
            if (File.Exists(Path.Combine(dir, name)) && new FileInfo(Path.Combine(dir, name)).Length > 0) return name;
            var c = Tools.Json(Tools.YtDlp, ["--flat-playlist", "-J", "--playlist-items", "0", "--no-warnings", "https://www.youtube.com/channel/" + chId], 60_000);
            var url = c.GetProperty("thumbnails").EnumerateArray()
                .Where(t => t.TryGetProperty("id", out var id) && id.GetString()?.StartsWith("avatar") == true)
                .Select(t => t.GetProperty("url").GetString()).FirstOrDefault();
            if (url == null) return null;
            // YouTube's picture server sizes by the "=s900" in the address: 240 px (~20 KB) looks the same as the 983 px original on a card;
            // 48 px for a Shorts-only channel is ~3 KB, unnoticeable that small
            url = Regex.Replace(url, @"=s\d+", tiny ? "=s48" : "=s240");
            var pic = Http.GetByteArrayAsync(url).GetAwaiter().GetResult();
            if (pic.Length == 0) return null;
            File.WriteAllBytes(Path.Combine(dir, name), pic);
            return name;
        }
        catch (Exception) { return null; }
    }

    /// <summary>A frame from a fifth into the video (past intros), 480 px wide.</summary>
    public static void Frame(string video, long? durationMs, string jpg)
    {
        var at = ((durationMs ?? 60_000) / 5000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        Tools.Run(Tools.FfMpeg, ["-v", "error", "-y", "-ss", at, "-i", video, "-frames:v", "1", "-vf", "scale=480:-2", "-q:v", "4", jpg], 60_000);
    }

    // ---------- writing (FORMAT.md §6.4) ----------

    public static void PutMeta(Mount m, string key, Meta meta)
    {
        var cur = m.Merged.GetValueOrDefault(key)?.Meta;
        if (cur != null && Format.ToJson(cur with { Ts = 0 }) == Format.ToJson(meta with { Ts = 0 })) return;
        Edit(m, key, i => i with { Meta = meta with { Ts = Format.NextTs(cur?.Ts) } });
    }

    public static void SetFlags(Mount m, string key, Func<Flags, Flags> f)
    {
        var cur = m.Merged.GetValueOrDefault(key)?.Flags ?? new Flags();
        Edit(m, key, i => i with { Flags = f(cur) with { Ts = Format.NextTs(cur.Ts) } });
    }

    public static void Edit(Mount m, string key, Func<Item, Item> f)
    {
        lock (m)
        {
            m.Mine[key] = f(m.Mine.GetValueOrDefault(key) ?? new Item());
            m.Remerge();
            m.Dirty = true;
        }
    }

    public static void Flush(Mount m)
    {
        SaveLocal(m);
        if (!m.Dirty || !m.Online) return;
        try
        {
            var dv = Directory.CreateDirectory(Path.Combine(m.Ml, "devices")).FullName;
            Format.WriteAtomic(Path.Combine(dv, DeviceId + ".json"), Format.Seal(Format.ToJson(MineFile(m))));
            if (m.Phone != null) Mtp.Push(m, m.Phone);
            m.Dirty = false;
        }
        catch (Exception e) when (e is IOException || m.Phone != null) { m.Online = false; m.Error = "не удалось записать: " + e.Message; }
    }

    // ---------- removing what was watched (the phone never deletes on its own) ----------

    public static List<Video> Watched(Mount m) => Videos().Where(v => v.Mount == m && v.Present && v.Watch.Status == Watch.Finished).ToList();

    /// <summary>
    /// Deletes watched videos the user ticked: only a file whose fingerprint still matches its record,
    /// plus its subtitles; the card, history and a cover stay, and it is never downloaded again by itself.
    /// </summary>
    public static (int Count, long Bytes) DeleteWatched(Mount m, IEnumerable<Video> list)
    {
        int n = 0; long bytes = 0;
        var gone = new HashSet<string>();
        foreach (var v in list)
        {
            if (v.Found is not { } f) continue;
            if (m.Phone != null)
            {
                // over the cable the file is matched by path and size (fingerprinting it would mean reading it all)
                if (f.Size != v.Meta.Size) continue;
                try { Mtp.Delete(m.Phone, f.RelPath); foreach (var s in f.Subs) Mtp.Delete(m.Phone, s.Path); }
                catch (Exception) { continue; }
                MarkGone(m, v);
                n++; bytes += f.Size; gone.Add(v.Key);
                continue;
            }
            if (!File.Exists(f.FullPath)) continue;
            try
            {
                if (Format.FileKey(f.FullPath) != v.Key) continue;          // not the file we know — leave it alone
                var thumb = v.Meta.Thumb;
                if (thumb == null && f.Thumb != null)
                {   // keep the cover for the history
                    var name = (v.Meta.YtId ?? v.Key) + Path.GetExtension(f.Thumb);
                    File.Copy(f.Thumb, Path.Combine(Directory.CreateDirectory(Path.Combine(m.Ml, "thumbs")).FullName, name), true);
                    thumb = "thumbs/" + name;
                }
                File.Delete(f.FullPath);
                foreach (var s in f.Subs) { var p = Path.Combine(m.Ref.Root, s.Path.Replace('/', Path.DirectorySeparatorChar)); if (File.Exists(p)) File.Delete(p); }
                if (f.Thumb != null && File.Exists(f.Thumb)) File.Delete(f.Thumb);
                var cur = m.Merged.GetValueOrDefault(v.Key);
                Edit(m, v.Key, i => i with
                {
                    Meta = thumb != v.Meta.Thumb ? (cur?.Meta ?? v.Meta) with { Thumb = thumb, Ts = Format.NextTs(cur?.Meta?.Ts) } : i.Meta,
                    File = new FileState { Ts = Format.NextTs(cur?.File?.Ts), State = FileState.AfterWatch, At = Format.Now(), Reason = "watched" },
                    Flags = (cur?.Flags ?? new Flags()) with { Ts = Format.NextTs(cur?.Flags?.Ts), NoAutoRedownload = true },
                });
                n++; bytes += f.Size; gone.Add(v.Key);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        m.Docs = m.Docs.Where(d => !gone.Contains(d.Key)).ToDictionary(d => d.Key, d => d.Value);
        Flush(m);
        return (n, bytes);
    }

    static void MarkGone(Mount m, Video v)
    {
        var cur = m.Merged.GetValueOrDefault(v.Key);
        Edit(m, v.Key, i => i with
        {
            File = new FileState { Ts = Format.NextTs(cur?.File?.Ts), State = FileState.AfterWatch, At = Format.Now(), Reason = "watched" },
            Flags = (cur?.Flags ?? new Flags()) with { Ts = Format.NextTs(cur?.Flags?.Ts), NoAutoRedownload = true },
        });
    }

    // ---------- the view ----------

    public static List<Video> Videos() => Mounts.SelectMany(m =>
        m.Merged.Select(e => new Video(e.Key, m, e.Value, m.Docs.GetValueOrDefault(e.Key)))
            .Concat(m.Docs.Where(d => !m.Merged.ContainsKey(d.Key)).Select(d => new Video(d.Key, m, new Item(), d.Value)))).ToList();

    /// <summary>
    /// YouTube IDs that must not be downloaded automatically (FORMAT.md §7): watched or deleted
    /// before, or already lying on some storage — unless the user pressed "Download again".
    /// Built from the local copies too, so it works with the stick unplugged.
    /// </summary>
    public static HashSet<string> DoNotDownload()
    {
        var all = Videos().Where(v => v.Meta.YtId != null).ToList();
        var again = all.Where(v => v.Flags.Redownload && !v.Present).Select(v => v.Meta.YtId!).ToHashSet();
        return all.Where(v => v.Present || (v.Flags.NoAutoRedownload && !again.Contains(v.Meta.YtId!)))
            .Select(v => v.Meta.YtId!).ToHashSet();
    }

    public static IEnumerable<Video> RedownloadRequests() => Videos().Where(v => v.Flags.Redownload && !v.Present && v.Meta.YtId != null);

    public static Caps? DeviceCaps() => Mounts.SelectMany(m => m.Others).Where(d => d.Caps != null).MaxBy(d => d.WrittenAt)?.Caps;

    public static void OpenInExplorer(string path) => Process.Start("explorer.exe", $"/select,\"{path}\"");
}
