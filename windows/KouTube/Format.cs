// The shared storage format (spec/FORMAT.md) — the C# twin of android/.../Format.kt.
// Both are tested against the same files in spec/fixtures.

using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KouTube;

public record Sub(string Lang, string Path, bool Auto = false);

public record Meta
{
    public long Ts { get; init; }
    public string? YtId { get; init; }
    public string? Title { get; init; }
    public string? Channel { get; init; }
    public string? Url { get; init; }
    public string? Published { get; init; }
    public string? Category { get; init; }
    public long? DurationMs { get; init; }
    public string? Kind { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public double? Fps { get; init; }
    public string? Vcodec { get; init; }
    public string? Acodec { get; init; }
    public long? Size { get; init; }
    public string? Path { get; init; }
    public List<Sub>? Subs { get; init; }
    public string? Thumb { get; init; }
    public long? AddedAt { get; init; }
    public string? Source { get; init; }
    public long? SponsorCutMs { get; init; }
    /// <summary>Channel picture inside .koutube, e.g. avatars/UC….jpg.</summary>
    public string? Avatar { get; init; }
    /// <summary>Top comments inside .koutube, e.g. comments/&lt;id&gt;.json, and how many the video has in total.</summary>
    public string? Comments { get; init; }
    public long? CommentCount { get; init; }
    /// <summary>YouTube's tags for the video: phones find a topic by them ("путешествие").</summary>
    public List<string>? Tags { get; init; }
}

/// <summary>One comment as stored in .koutube/comments: author, text, likes, time, pinned, by the channel itself.</summary>
public record Comment(string A, string T, long L = 0, long? Ts = null, bool P = false, bool U = false);

public record Watch
{
    public const string New = "new", Started = "started", Finished = "finished";
    public long Ts { get; init; }
    public string Status { get; init; } = New;
    public long PosMs { get; init; }
    public string? Coverage { get; init; }
    public long? LastWatchedAt { get; init; }
    public long? FinishedAt { get; init; }
}

public record FileState
{
    public const string Present = "present", AfterWatch = "deletedAfterWatch", ByUser = "deletedByUser";
    public long Ts { get; init; }
    public string State { get; init; } = Present;
    public long? At { get; init; }
    public string? Reason { get; init; }
}

public record Flags
{
    public long Ts { get; init; }
    public bool Keep { get; init; }
    public bool NoAutoRedownload { get; init; }
    public bool Redownload { get; init; }
    /// <summary>1 = 👍, -1 = 👎, 0 = none.</summary>
    public int Rating { get; init; }
}

public record Item
{
    public Meta? Meta { get; init; }
    public Watch? Watch { get; init; }
    public FileState? File { get; init; }
    public Flags? Flags { get; init; }
}

public record Decoder(string Mime, bool Hw, int MaxW, int MaxH);
public record Caps { public List<Decoder> Video { get; init; } = []; }

public record DeviceFile
{
    public int Schema { get; init; } = 1;
    public string DeviceId { get; init; } = "";
    public string? DeviceName { get; init; }
    public string? Kind { get; init; }
    public string? App { get; init; }
    public long WrittenAt { get; init; }
    public Caps? Caps { get; init; }
    public Dictionary<string, Item> Items { get; init; } = [];
}

public record Folder(string Path, string Role = "auto");
public record Budget { public long? TotalBytes { get; init; } public long ReserveBytes { get; init; } public double ShortsShare { get; init; } = 0.2; }

public record StorageFile
{
    public int Schema { get; init; } = 1;
    public string StorageId { get; init; } = "";
    public string? Name { get; init; }
    public long UpdatedAt { get; init; }
    public List<Folder> Folders { get; init; } = [];
    public Dictionary<string, string> DownloadTo { get; init; } = [];
    public Budget Budget { get; init; } = new();
}

public static class Format
{
    public const int Schema = 1;
    public const int KeyChunk = 16384;
    public const long SegmentMs = 5000;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // Cyrillic stays readable, like the Kotlin side
    };

    public static string ToJson<T>(T v) => JsonSerializer.Serialize(v, Json);
    public static T? FromJson<T>(string s) => JsonSerializer.Deserialize<T>(s, Json);

    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public static long NextTs(long? merged) => Math.Max(Now(), (merged ?? 0) + 1);

    // ---- envelope (§2) ----
    const string Prefix = "{\"sha256\":\"", Mid = "\",\"body\":";
    const int BodyAt = 11 + 64 + 9;

    static string Hex(byte[] b) => Convert.ToHexStringLower(b);
    static string Sha256Hex(string s) => Hex(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    public static string Seal(string body) => Prefix + Sha256Hex(body) + Mid + body + "}";

    /// <summary>The body of a sealed file, or null if it is torn, corrupted or not sealed.</summary>
    public static string? Unseal(string text)
    {
        var t = text.TrimStart('﻿');
        if (t.Length <= BodyAt || !t.StartsWith(Prefix, StringComparison.Ordinal) || t.Substring(75, 9) != Mid) return null;
        var end = t.LastIndexOf('}');
        if (end < BodyAt) return null;
        var body = t[BodyAt..end];
        return Sha256Hex(body) == t.Substring(11, 64) ? body : null;
    }

    // ---- fileKey (§5.1) ----
    public static string FileKey(long size, byte[] head, byte[] tail)
    {
        var data = size < 2 * KeyChunk ? head : [.. head, .. tail];
        return $"f1-{size}-" + Hex(SHA256.HashData(data))[..16];
    }

    public static string FileKey(string path)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var size = f.Length;
        byte[] Chunk(long at, int n) { var b = new byte[n]; f.Position = at; f.ReadExactly(b); return b; }
        return size < 2 * KeyChunk ? FileKey(size, Chunk(0, (int)size), [])
                                   : FileKey(size, Chunk(0, KeyChunk), Chunk(size - KeyChunk, KeyChunk));
    }

    // ---- merge (§6) ----
    public static Dictionary<string, Item> Merge(IEnumerable<DeviceFile> files)
    {
        var o = new Dictionary<string, Item>();
        foreach (var f in files.OrderBy(f => f.DeviceId, StringComparer.Ordinal))
            foreach (var (k, b) in f.Items)
            {
                var a = o.GetValueOrDefault(k) ?? new Item();
                o[k] = new Item
                {
                    Meta = Newer(a.Meta, b.Meta, x => x.Ts), Watch = Newer(a.Watch, b.Watch, x => x.Ts),
                    File = Newer(a.File, b.File, x => x.Ts), Flags = Newer(a.Flags, b.Flags, x => x.Ts),
                };
            }
        return o;
    }

    static T? Newer<T>(T? a, T? b, Func<T, long> ts) where T : class =>
        a == null ? b : b == null ? a : ts(b) >= ts(a) ? b : a;

    // ---- safe replace (§4) ----
    public static void WriteAtomic(string path, string text)
    {
        var tmp = path + ".tmp";
        using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var b = new UTF8Encoding(false).GetBytes(text);
            f.Write(b);
            f.Flush(true);
        }
        if (System.IO.File.Exists(path))
        {
            System.IO.File.Delete(path + ".bak");
            System.IO.File.Move(path, path + ".bak");
        }
        System.IO.File.Move(tmp, path);
    }

    /// <summary>Every verified copy of path (X, X.tmp, X.bak) — the caller picks the newest.</summary>
    public static IEnumerable<string> ReadCandidates(string path) =>
        new[] { path, path + ".tmp", path + ".bak" }.Where(System.IO.File.Exists)
            .Select(p => { try { return Unseal(System.IO.File.ReadAllText(p)); } catch (IOException) { return null; } })
            .OfType<string>();

    public static T? Newest<T>(IEnumerable<string> bodies, Func<T, int> schema, Func<T, long> at) where T : class =>
        bodies.Select(b => { try { return FromJson<T>(b); } catch (JsonException) { return null; } })
            .OfType<T>().Where(x => schema(x) == Schema).MaxBy(at);

    // ---- scanner helpers (§8) ----
    public static readonly HashSet<string> VideoExt = ["mp4", "m4v", "mov", "webm", "mkv", "3gp", "ts", "flv"];

    public static bool IsVideoName(string name) =>
        !name.StartsWith('.') && VideoExt.Contains(System.IO.Path.GetExtension(name).TrimStart('.').ToLowerInvariant());

    static readonly Dictionary<char, char> Fullwidth = new()
    { ['？'] = '?', ['：'] = ':', ['｜'] = '|', ['＊'] = '*', ['＂'] = '"', ['＜'] = '<', ['＞'] = '>', ['⧸'] = '/', ['＼'] = '\\' };

    public static string UnFullwidth(string s) => string.Concat(s.Select(c => Fullwidth.GetValueOrDefault(c, c)));

    public static (string? Channel, string Title) ParseName(string fileName)
    {
        var b = UnFullwidth(System.IO.Path.GetFileNameWithoutExtension(fileName));
        var i = b.IndexOf(" - ", StringComparison.Ordinal);
        return i > 0 ? (b[..i], b[(i + 3)..]) : (null, b);
    }

    public static string DetectKind(string role, string? url, int? w, int? h, long? durationMs) =>
        role is "video" or "short" ? role
        : url?.Contains("/shorts/") == true ? "short"
        : w != null && h != null && h > w && (durationMs ?? long.MaxValue) <= 180_000 ? "short"
        : "video";
}
