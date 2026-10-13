// A phone on the USB cable ("Передача файлов", MTP). It gets no drive letter, so its library's .koutube lives in
// a local mirror: pulled on every refresh, pushed back after every write. Videos go straight to the phone.

using MediaDevices;

namespace KouTube;

public static class Mtp
{
    /// <summary>[Root] is the library folder on the phone, e.g. \Внутренний общий накопитель\Movies\KouTube.</summary>
    public record Phone(string Serial, string Root);

    public static Phone? Parse(string root) => root.StartsWith("mtp:") && root[4..].Split('|') is [var s, var r] ? new(s, r) : null;
    public static string RootOf(string serial, string path) => $"mtp:{serial}|{path}";

    static readonly Dictionary<string, MediaDevice> Open = [];

    static MediaDevice Device(string serial)
    {
        lock (Open)
        {
            if (Open.TryGetValue(serial, out var d) && d.IsConnected) return d;
            foreach (var x in MediaDeviceManager.Instance.GetDevices())
                try
                {
                    x.Connect();
                    if (x.SerialNumber == serial) return Open[serial] = x;
                    x.Disconnect();
                }
                catch (Exception) { }
            throw new IOException("телефон не подключён по кабелю — или на нём не выбрано «Передача файлов»");
        }
    }

    static string Remote(Phone p, string rel) => rel is "." or "" ? p.Root : p.Root + "\\" + rel.Replace('/', '\\');

    static void Dirs(MediaDevice d, string path)
    {
        if (d.DirectoryExists(path)) return;
        Dirs(d, Path.GetDirectoryName(path)!);
        d.CreateDirectory(path);
    }

    public record PhoneLibrary(string Serial, string Device, string Path, StorageFile Storage);

    /// <summary>KouTube libraries on every connected phone: folders up to two levels deep holding .koutube (a few seconds per phone).</summary>
    public static List<PhoneLibrary> Find()
    {
        var res = new List<PhoneLibrary>();
        foreach (var d in MediaDeviceManager.Instance.GetDevices())
        {
            try { d.Connect(); } catch (Exception) { continue; }
            if (d.Protocol?.StartsWith("MTP", StringComparison.OrdinalIgnoreCase) != true) { d.Disconnect(); continue; }   // flash drives show up here too
            var name = new[] { d.FriendlyName, d.Model, d.Description }.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "Телефон";
            IEnumerable<string> Sub(string p) { try { return d.EnumerateDirectories(p).Where(x => !Path.GetFileName(x).StartsWith('.') || Path.GetFileName(x) == ".koutube").ToList(); } catch (Exception) { return []; } }
            var libs = new List<string>();
            foreach (var root in d.GetDrives()?.Select(x => x.RootDirectory.FullName) ?? [])
                foreach (var a in Sub(root))
                    foreach (var b in Sub(a))
                    {
                        if (Path.GetFileName(b) == ".koutube") libs.Add(a);
                        else foreach (var c in Sub(b)) if (Path.GetFileName(c) == ".koutube") libs.Add(b);
                    }
            foreach (var lib in libs)
                try
                {
                    var ms = new MemoryStream();
                    d.DownloadFile(lib + "\\.koutube\\storage.json", ms);
                    if (Format.Unseal(System.Text.Encoding.UTF8.GetString(ms.ToArray())) is { } body && Format.FromJson<StorageFile>(body) is { } sf)
                        res.Add(new(d.SerialNumber, name, lib, sf));
                }
                catch (Exception) { }
            lock (Open) Open[d.SerialNumber] = d;
        }
        return res;
    }

    public static (long Free, long Total)? Space(Phone p)
    {
        try
        {
            var dr = Device(p.Serial).GetDrives()?.FirstOrDefault(x => p.Root.StartsWith(x.Name, StringComparison.OrdinalIgnoreCase));
            return dr == null ? null : ((long)dr.AvailableFreeSpace, (long)dr.TotalSize);
        }
        catch (Exception) { return null; }
    }

    // ---------- the mirror of .koutube ----------

    static string PushedFile(Mount m) => Path.Combine(Path.GetDirectoryName(m.Ml)!, "pushed.json");

    static Dictionary<string, long> Pushed(Mount m) =>
        File.Exists(PushedFile(m)) ? Format.FromJson<Dictionary<string, long>>(File.ReadAllText(PushedFile(m))) ?? [] : [];

    /// <summary>The phone's storage.json and every device file → the mirror. They count as "in sync", so they are never pushed back.</summary>
    public static void Pull(Mount m, Phone p)
    {
        var d = Device(p.Serial);
        var ml = Remote(p, ".koutube");
        if (!d.DirectoryExists(ml)) throw new IOException("на телефоне больше нет этой библиотеки");
        var pushed = Pushed(m);
        void Get(string remoteDir, string rel)
        {
            if (!d.DirectoryExists(remoteDir)) return;
            foreach (var f in d.GetFiles(remoteDir).Where(f => f.EndsWith(".json") || f.EndsWith(".json.tmp") || f.EndsWith(".json.bak")))
            {
                var r = rel + Path.GetFileName(f);
                var local = Path.Combine(m.Ml, r.Replace('/', '\\'));
                Directory.CreateDirectory(Path.GetDirectoryName(local)!);
                using (var s = File.Create(local)) d.DownloadFile(f, s);
                pushed[r] = File.GetLastWriteTimeUtc(local).Ticks;
            }
        }
        Get(ml, "");
        Get(ml + "\\devices", "devices/");
        // covers and channel pictures for the PC's own cards: each fetched once, then kept in the mirror
        foreach (var dir in new[] { "thumbs", "avatars" })
        {
            if (!d.DirectoryExists(ml + "\\" + dir)) continue;
            foreach (var f in d.GetFiles(ml + "\\" + dir))
            {
                var local = Path.Combine(m.Ml, dir, Path.GetFileName(f));
                if (File.Exists(local)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(local)!);
                try { using (var s = File.Create(local)) d.DownloadFile(f, s); pushed[dir + "/" + Path.GetFileName(f)] = File.GetLastWriteTimeUtc(local).Ticks; }
                catch (Exception) { File.Delete(local); }
            }
        }
        File.WriteAllText(PushedFile(m), Format.ToJson(pushed));
    }

    /// <summary>Everything we wrote into the mirror since the last push → the phone. Library files keep the crash-safe order of FORMAT.md §4.</summary>
    public static void Push(Mount m, Phone p)
    {
        var d = Device(p.Serial);
        var pushed = Pushed(m);
        foreach (var file in Directory.EnumerateFiles(m.Ml, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(m.Ml, file).Replace('\\', '/');
            if (rel.EndsWith(".tmp") || rel.EndsWith(".bak")) continue;
            var mt = File.GetLastWriteTimeUtc(file).Ticks;
            if (pushed.GetValueOrDefault(rel) == mt) continue;
            var remote = Remote(p, ".koutube/" + rel);
            Dirs(d, Path.GetDirectoryName(remote)!);
            if (rel == "storage.json" || rel.StartsWith("devices/"))
            {
                if (d.FileExists(remote + ".tmp")) d.DeleteFile(remote + ".tmp");
                using (var s = File.OpenRead(file)) d.UploadFile(s, remote + ".tmp");
                if (d.FileExists(remote))
                {
                    if (d.FileExists(remote + ".bak")) d.DeleteFile(remote + ".bak");
                    d.Rename(remote, Path.GetFileName(remote) + ".bak");
                }
                d.Rename(remote + ".tmp", Path.GetFileName(remote));
            }
            else
            {
                if (d.FileExists(remote)) d.DeleteFile(remote);
                using var s = File.OpenRead(file);
                d.UploadFile(s, remote);
            }
            pushed[rel] = mt;
        }
        File.WriteAllText(PushedFile(m), Format.ToJson(pushed));
    }

    // ---------- the videos themselves ----------

    /// <summary>
    /// Video files in the library's folders. The key comes from the phone's own records (same path and size) —
    /// reading every video over the cable to fingerprint it would take ages. Files the phone hasn't registered yet are left out;
    /// the phone adds them the next time KouTube opens there.
    /// </summary>
    public static Dictionary<string, Found> Scan(Mount m, Phone p)
    {
        var d = Device(p.Serial);
        var known = m.Merged.Where(e => e.Value.Meta is { Path: not null, Size: not null })
            .GroupBy(e => e.Value.Meta!.Path!).ToDictionary(g => g.Key, g => g.ToList());
        var found = new Dictionary<string, Found>();
        void Walk(string dir, string rel, int depth)
        {
            List<MediaFileInfo> files;
            try { files = d.GetDirectoryInfo(dir).EnumerateFiles().ToList(); } catch (ArgumentException) { return; }   // a name MTP can't spell
            var names = files.Select(f => f.Name).ToList();
            foreach (var f in files.Where(f => Format.IsVideoName(f.Name)))
            {
                var r = rel == "." ? f.Name : rel + "/" + f.Name;
                var key = known.GetValueOrDefault(r)?.FirstOrDefault(e => e.Value.Meta!.Size == (long)f.Length).Key;
                if (key == null || found.ContainsKey(key)) continue;
                var b = Path.GetFileNameWithoutExtension(f.Name);
                var subs = names.Where(n => n.StartsWith(b + ".") && (n.EndsWith(".vtt") || n.EndsWith(".srt")))
                    .Select(n => new Sub(n[(b.Length + 1)..].Split('.')[0], rel == "." ? n : rel + "/" + n)).ToList();
                found[key] = new(dir + "\\" + f.Name, r, (long)f.Length, f.LastWriteTime ?? DateTime.Now, subs, null);
            }
            if (depth < 2)
                try { foreach (var sub in d.GetDirectoryInfo(dir).EnumerateDirectories().Where(x => !x.Name.StartsWith('.')).ToList()) Walk(sub.FullName, rel == "." ? sub.Name : rel + "/" + sub.Name, depth + 1); }
                catch (ArgumentException) { }
        }
        foreach (var fo in m.Storage.Folders)
            if (d.DirectoryExists(Remote(p, fo.Path))) Walk(Remote(p, fo.Path), fo.Path, 0);
        return found;
    }

    public static bool Exists(Phone p, string rel) => Device(p.Serial).FileExists(Remote(p, rel));

    /// <summary>A file onto the phone: a temporary name first, the real one only once every byte is there.</summary>
    public static void Upload(Phone p, string local, string rel, Action<long>? progress = null, CancellationToken ct = default)
    {
        var d = Device(p.Serial);
        var remote = Remote(p, rel);
        var part = remote + ".ktpart";
        Dirs(d, Path.GetDirectoryName(remote)!);
        if (d.FileExists(part)) d.DeleteFile(part);
        using (var s = new Counting(File.OpenRead(local), progress, ct)) d.UploadFile(s, part);
        if ((long)d.GetFileInfo(part).Length != new FileInfo(local).Length) { d.DeleteFile(part); throw new IOException("копия на телефоне не совпала по размеру"); }
        if (d.FileExists(remote)) d.DeleteFile(remote);
        d.Rename(part, Path.GetFileName(remote));
    }

    public static void Delete(Phone p, string rel)
    {
        var d = Device(p.Serial);
        if (d.FileExists(Remote(p, rel))) d.DeleteFile(Remote(p, rel));
    }

    /// <summary>Reports how much of the file the upload has read, and stops it on cancel.</summary>
    sealed class Counting(Stream inner, Action<long>? progress, CancellationToken ct) : Stream
    {
        long _done;
        public override int Read(byte[] buffer, int offset, int count)
        {
            ct.ThrowIfCancellationRequested();
            var n = inner.Read(buffer, offset, count);
            _done += n; progress?.Invoke(_done);
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => _done; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
