// Recommendations without magic: what you finish, from which channels, about what — and only
// from the sources you added. Every pick says why. Nothing is downloaded until you confirm.

using System.Text.Json;
using System.Text.RegularExpressions;

namespace KouTube;

public record Pick(string Id, string Url, string Title, string? Channel, double? DurationSec, DateTimeOffset? Published,
    string Kind, double Score, string Reason, string Source, string? Thumb = null)
{
    /// <summary>Size before asking YouTube for formats: minutes × what this library's 720p files weigh.</summary>
    public long Estimate => (long)((Kind == "short" ? Math.Max(DurationSec ?? 45, 15) : DurationSec ?? 900) / 60 * Recommend.MbPerMin * (1L << 20));
    public string DurationText => DurationSec is { } d ? TimeSpan.FromSeconds(d).ToString(d >= 3600 ? @"h\:mm\:ss" : @"m\:ss") : Kind == "short" ? "Shorts" : "";
    public string SizeText => "≈ " + Ui.Gb(Estimate);
}

public static class Recommend
{
    // ---------- channels by name ----------

    public record Found(string Name, string Url, long? Subscribers);

    /// <summary>Name, @handle or link → candidate channels. A name runs YouTube's channel search.</summary>
    public static List<Found> FindChannels(string q)
    {
        q = q.Trim();
        if (q.StartsWith('@')) q = "https://www.youtube.com/" + q;
        if (q.StartsWith("http"))
        {
            var j = Tools.Json(Tools.YtDlp, ["--flat-playlist", "-J", "--playlist-end", "1", "--no-warnings", q.TrimEnd('/')], 120_000);
            var name = Str(j, "channel") ?? Str(j, "uploader") ?? Str(j, "title") ?? q;
            var id = Str(j, "channel_id");
            return [new(name, id != null ? "https://www.youtube.com/channel/" + id : q, Long(j, "channel_follower_count"))];
        }
        var s = Tools.Json(Tools.YtDlp, ["--flat-playlist", "-J", "--playlist-end", "6", "--no-warnings",
            "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(q) + "&sp=EgIQAg%253D%253D"], 120_000);
        return s.TryGetProperty("entries", out var es)
            ? es.EnumerateArray().Where(e => Str(e, "ie_key") == "YoutubeTab" && Str(e, "url") != null)
                .Select(e => new Found(Str(e, "title") ?? "?", Str(e, "url")!, Long(e, "channel_follower_count"))).ToList()
            : [];
    }

    static string? Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static long? Long(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;
    static double? Dbl(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    // ---------- videos by link ----------

    static readonly Regex VideoLink = new(@"(?:v=|youtu\.be/|shorts/|live/)([A-Za-z0-9_-]{11})");

    /// <summary>Every YouTube video ID in pasted text (several links at once are fine).</summary>
    public static List<string> VideoIds(string text) => VideoLink.Matches(text).Select(m => m.Groups[1].Value).Distinct().ToList();

    /// <summary>What a pick needs to know about one video: title, channel, length, video or Short.</summary>
    public static Wanted ProbeWanted(string id)
    {
        var j = Tools.Json(Tools.YtDlp, ["-J", "--no-playlist", "--no-warnings", "--skip-download", "https://www.youtube.com/watch?v=" + id], 120_000);
        var dur = Dbl(j, "duration");
        var vertical = Long(j, "height") > Long(j, "width");
        var isShort = vertical && dur is <= 180;
        return new(id, (isShort ? "https://www.youtube.com/shorts/" : "https://www.youtube.com/watch?v=") + id,
            Str(j, "title") ?? id, Str(j, "channel") ?? Str(j, "uploader"), dur, isShort ? "short" : "video");
    }

    public static Pick ToPick(Wanted w) => new(w.Id, w.Url, w.Title, w.Channel, w.DurationSec, null, w.Kind, 100, "вы добавили по ссылке", "ссылка",
        $"https://i.ytimg.com/vi/{w.Id}/{(w.Kind == "short" ? "oar2" : "mqdefault")}.jpg");

    // ---------- what the user likes ----------

    /// <summary>
    /// Channel and topic weights from what you did: 👍 / finished push up; 👎, deleting unwatched and
    /// dropping a video early push down. Counts are kept for the "why" text.
    /// </summary>
    public record Interests(Dictionary<string, double> Channels, Dictionary<string, (int Finished, int Total, int Likes)> Counts,
        Dictionary<string, double> Words, double MedianMinutes, double ShortsBias, Dictionary<string, int> WordVideos)
    {
        /// <summary>Words that make a topic: liked and found in at least two different videos (one title's "откуда" is not a topic).</summary>
        public IEnumerable<string> Topics => Words.Where(x => x.Value > 0 && WordVideos.GetValueOrDefault(x.Key) >= 2).OrderByDescending(x => x.Value).Select(x => x.Key);
    }

    static readonly HashSet<string> Stop = ["это", "этот", "эта", "этого", "как", "что", "чем", "для", "или", "когда", "если", "весь", "всех", "всё", "все",
        "самый", "самая", "самое", "the", "and", "with", "from", "this", "that", "your", "what", "часть", "выпуск", "серия", "сезон"];

    public static IEnumerable<string> Words(string title) =>
        Regex.Split(Format.UnFullwidth(title).ToLowerInvariant(), @"[^\p{L}\p{Nd}]+").Where(w => w.Length >= 4 && !Stop.Contains(w));

    public static double Signal(Video v)
    {
        double s = 0;
        if (v.Flags.Rating > 0) s += 2;
        if (v.Flags.Rating < 0) s -= 2.5;
        if (v.Watch.Status == Watch.Finished) s += 1;
        if (v.FileState.State == FileState.ByUser && v.Watch.Status != Watch.Finished) s -= 1.5;         // deleted without watching
        if (v.Watch.Status == Watch.Started && v.Meta.DurationMs > 0 && v.Watch.PosMs < v.Meta.DurationMs * 0.25
            && v.Watch.LastWatchedAt < Format.Now() - 3 * 86_400_000L) s -= 0.7;                           // gave up early
        return s;
    }

    public static Interests Learn(List<Video> all)
    {
        var ch = new Dictionary<string, double>();
        var counts = new Dictionary<string, (int, int, int)>();
        var words = new Dictionary<string, double>();
        var inVideos = new Dictionary<string, int>();
        foreach (var v in all)
        {
            var sig = Signal(v) + 0.2;      // being in the library at all is a small "yes"
            if (v.Meta.Channel is { } c)
            {
                ch[c] = ch.GetValueOrDefault(c) + sig;
                var (f, t, l) = counts.GetValueOrDefault(c);
                counts[c] = (f + (v.Watch.Status == Watch.Finished ? 1 : 0), t + 1, l + (v.Flags.Rating > 0 ? 1 : 0));
            }
            foreach (var w in Words(v.Title).Distinct()) { words[w] = words.GetValueOrDefault(w) + sig; inVideos[w] = inVideos.GetValueOrDefault(w) + 1; }
        }
        var liked = all.Where(v => Signal(v) > 0).Select(v => (v.Meta.DurationMs ?? 0) / 60000.0).Where(m => m > 0).Order().ToList();
        if (liked.Count == 0) liked = all.Select(v => (v.Meta.DurationMs ?? 0) / 60000.0).Where(m => m > 0).Order().ToList();
        double Bias(bool shorts) { var l = all.Where(v => v.IsShort == shorts && Signal(v) != 0).ToList(); return l.Count == 0 ? 0 : l.Average(Signal); }
        return new(ch, counts, words, liked.Count > 0 ? liked[liked.Count / 2] : 20, Bias(true) - Bias(false), inVideos);
    }

    // ---------- candidates from the sources ----------

    /// <summary>The newest videos (and Shorts) of every source, filtered by the publication-age window.</summary>
    /// <summary>
    /// "Just pick something for me": with no channels or words chosen, the library speaks for itself —
    /// the channels you like most (those we know the id of from their avatars) and your top topics.
    /// </summary>
    public static Config Implicit(Config c, Interests it)
    {
        if (c.Sources.Count + c.Keywords.Count > 0) return c;
        var chans = it.Channels.Where(x => x.Value > 0 && c.Avatars.ContainsKey(x.Key)).OrderByDescending(x => x.Value).Take(5)
            .Select(x => new Source("https://www.youtube.com/channel/" + Path.GetFileNameWithoutExtension(c.Avatars[x.Key]), x.Key, "channel")).ToList();
        var words = it.Topics.Take(3).ToList();
        return c with { Sources = chans, Keywords = words, PerSource = Math.Min(c.PerSource, 12) };
    }

    public static List<Pick> Candidates(Config c, Action<string> status)
    {
        var outList = new List<Pick>();
        var now = DateTimeOffset.UtcNow;
        foreach (var src in c.Sources)
        {
            foreach (var tab in new[] { "videos", "shorts" })
            {
                status($"Смотрю «{src.Name}» ({(tab == "videos" ? "видео" : "Shorts")})…");
                JsonElement j;
                try
                {
                    j = Tools.Json(Tools.YtDlp, ["--flat-playlist", "-J", "--playlist-end", c.PerSource.ToString(), "--no-warnings",
                        "--extractor-args", "youtubetab:approximate_date", src.Url.TrimEnd('/') + "/" + tab], 180_000);
                }
                catch (Exception) { continue; }    // a channel without Shorts, or offline
                if (!j.TryGetProperty("entries", out var es)) continue;
                foreach (var e in es.EnumerateArray())
                {
                    var id = Str(e, "id");
                    if (id is not { Length: 11 }) continue;
                    DateTimeOffset? pub = Long(e, "timestamp") is { } ts ? DateTimeOffset.FromUnixTimeSeconds(ts) : null;
                    var age = pub is { } p ? (now - p).TotalDays : (double?)null;
                    // Shorts come without a date: they pass the window rather than vanish
                    if (age != null && (age < (src.MinAgeDays ?? c.MinAgeDays ?? 0) || age > (src.MaxAgeDays ?? c.MaxAgeDays ?? double.MaxValue))) continue;
                    outList.Add(new Pick(id, tab == "shorts" ? "https://www.youtube.com/shorts/" + id : "https://www.youtube.com/watch?v=" + id,
                        Str(e, "title") ?? id, src.Name, Dbl(e, "duration"), pub, tab == "shorts" ? "short" : "video", 0, "", src.Name,
                        ThumbOf(e, id, tab == "shorts")));
                }
            }
        }
        foreach (var kw in c.Keywords)
        {
            var tag = new string(kw.Where(char.IsLetterOrDigit).ToArray());
            if (tag.Length > 0)
                try
                {
                    status($"Ищу Shorts «#{tag}»…");
                    var h = Tools.Json(Tools.YtDlp, ["--flat-playlist", "-J", "--no-warnings", "--playlist-end", c.PerSource.ToString(),
                        "https://www.youtube.com/hashtag/" + Uri.EscapeDataString(tag.ToLowerInvariant()) + "/shorts"], 180_000);
                    if (h.TryGetProperty("entries", out var hs))
                        foreach (var e in hs.EnumerateArray())
                            if (Str(e, "id") is { Length: 11 } sid)
                                outList.Add(new Pick(sid, "https://www.youtube.com/shorts/" + sid, Str(e, "title") ?? sid, Str(e, "channel") ?? Str(e, "uploader"),
                                    Dbl(e, "duration"), null, "short", 0, "", "Shorts «#" + tag + "»", ThumbOf(e, sid, true)));
                }
                catch (Exception) { }
            status($"Ищу «{kw}»…");
            // "only new" sorts the search by date; YouTube's search gives no dates to filter otherwise
            var newest = c.MaxAgeDays is <= 30;
            JsonElement j;
            try { j = Tools.Json(Tools.YtDlp, ["--flat-playlist", "-J", "--no-warnings", $"ytsearch{(newest ? "date" : "")}{c.PerSource}:{kw}"], 180_000); }
            catch (Exception) { continue; }
            if (!j.TryGetProperty("entries", out var es)) continue;
            foreach (var e in es.EnumerateArray())
            {
                var id = Str(e, "id");
                if (id is not { Length: 11 }) continue;
                var url = Str(e, "url") ?? "";
                outList.Add(new Pick(id, url.Length > 0 ? url : "https://www.youtube.com/watch?v=" + id, Str(e, "title") ?? id,
                    Str(e, "channel") ?? Str(e, "uploader"), Dbl(e, "duration"), null, url.Contains("/shorts/") ? "short" : "video", 0, "", "поиск «" + kw + "»",
                    ThumbOf(e, id, url.Contains("/shorts/"))));
            }
        }
        return outList.DistinctBy(p => p.Id).ToList();
    }

    /// <summary>Shorts need their own vertical picture; for videos YouTube's 16:9 one by ID.</summary>
    static string ThumbOf(JsonElement e, string id, bool shorts) =>
        shorts && e.TryGetProperty("thumbnails", out var ts) && ts.ValueKind == JsonValueKind.Array && ts.GetArrayLength() > 0
            ? ts[0].GetProperty("url").GetString()! : $"https://i.ytimg.com/vi/{id}/mqdefault.jpg";

    public static double MbPerMin = 6;

    /// <summary>How heavy a minute of video is in this library (720p AV1 ≈ 5–8 MB) — for the estimates.</summary>
    public static void Calibrate(List<Video> all)
    {
        var r = all.Where(v => v.Present && v.Meta is { Size: > 0, DurationMs: > 60_000 })
            .Select(v => v.Meta.Size!.Value / (double)(1L << 20) / (v.Meta.DurationMs!.Value / 60000.0)).Order().ToList();
        if (r.Count >= 3) MbPerMin = Math.Clamp(r[r.Count / 2], 2, 20);
    }

    /// <summary>Score from channel and topic weights, a familiar length, Shorts-or-not and a nudge for fresh. With reasons.</summary>
    public static List<Pick> Rank(List<Pick> cands, Interests it, HashSet<string> skip)
    {
        return cands.Where(p => !skip.Contains(p.Id)).Select(p =>
        {
            var why = new List<string>();
            double score = 0;
            if (p.Channel != null && it.Channels.TryGetValue(p.Channel, out var cw))
            {
                score += 2.5 * Math.Tanh(cw / 3);
                var (f, t, l) = it.Counts[p.Channel];
                if (cw > 0) why.Add($"канал «{p.Channel}»: " + string.Join(", ", new[] { l > 0 ? $"👍 {l}" : null, f > 0 ? $"досмотрено {f} из {t}" : $"уже {t} в библиотеке" }.OfType<string>()));
            }
            else why.Add($"источник «{p.Source}»");
            var shared = Words(p.Title).Distinct().Where(it.Words.ContainsKey).ToList();
            var topical = shared.Sum(w => Math.Clamp(it.Words[w], -2, 2));
            score += Math.Clamp(topical, -3, 3);
            var good = shared.Where(w => it.Words[w] > 0).OrderByDescending(w => it.Words[w]).Take(3).ToList();
            if (good.Count > 0) why.Add("темы: " + string.Join(", ", good));
            if (p.DurationSec is { } d && p.Kind == "video")
            {
                var ratio = d / 60 / Math.Max(1, it.MedianMinutes);
                score += ratio is > 0.4 and < 2.5 ? 0.5 : -0.5;
            }
            score += (p.Kind == "short" ? 1 : -1) * Math.Clamp(it.ShortsBias, -1, 1) * 0.5;
            if (p.Published is { } pub) score += Math.Max(0, 1 - (DateTimeOffset.UtcNow - pub).TotalDays / 60) * 0.5;   // a little nudge for fresh
            return p with { Score = score, Reason = string.Join("; ", why) };
        }).Where(p => p.Score > -1)              // what you clearly dislike does not come back
          .OrderByDescending(p => p.Score).ToList();
    }

    /// <summary>
    /// "Докачать": fill the free part of each budget (videos / Shorts) with the best picks. Each pick is
    /// probed for its exact size first, so the queue never promises more than fits.
    /// </summary>
    public static async Task Fill(Mount m, Action<string> status, CancellationToken ct = default)
    {
        var c = Lib.Config;
        if (c.Sources.Count == 0) { status("Сначала добавьте каналы-источники"); return; }
        var it = Learn(Lib.Videos());
        var skip = Lib.DoNotDownload();
        skip.UnionWith(Lib.Config.Seen);
        foreach (var r in Downloader.Rows) skip.Add(r.Item.Id);
        var ranked = Rank(await Task.Run(() => Candidates(c, status), ct), it, skip);
        if (ranked.Count == 0) { status("Новых подходящих видео в источниках нет"); return; }

        var sp = m.Space();
        if (sp == null) { status($"{m.Name} не подключено"); return; }
        var b = m.Storage.Budget;
        var allowance = b.TotalBytes ?? (m.UsedBy(false) + m.UsedBy(true) + sp.Value.Free - b.ReserveBytes);
        var free = sp.Value.Free - b.ReserveBytes - (long)(Downloader.Rows.Where(r => r.Item.Include && r.Item.Status == Q.Pending && r.Item.StorageId == m.Ref.StorageId).Sum(r => r.Item.Expected) * 1.05);
        long Room(bool shorts) => (long)(allowance * (shorts ? b.ShortsShare : 1 - b.ShortsShare)) - m.UsedBy(shorts)
            - (long)(Downloader.Rows.Where(r => r.Item.Include && r.Item.Status == Q.Pending && (r.Item.Kind == "short") == shorts).Sum(r => r.Item.Expected) * 1.05);
        var room = new Dictionary<bool, long> { [false] = Room(false), [true] = Room(true) };
        var added = 0;
        foreach (var p in ranked)
        {
            ct.ThrowIfCancellationRequested();
            var shorts = p.Kind == "short";
            if (room[shorts] < 20L << 20 || free < 20L << 20) continue;   // that part is full
            // a rough pre-check saves a slow probe: 720p AV1 is ~6 MB a minute here
            if (p.DurationSec is { } d && d / 60 * 6 * (1L << 20) > Math.Min(room[shorts], free)) continue;
            status($"Проверяю «{p.Title}»…");
            QueueItem q;
            try { q = await Downloader.Probe(p.Url, p.Kind, "канал «" + p.Source + "»", p.Reason, m.Ref.StorageId); }
            catch (Exception) { continue; }
            var need = (long)(q.Expected * 1.05);
            if (q.Status != Q.Pending || need > room[shorts] || need > free) continue;
            Ui.OnUi(() => { Downloader.Rows.Add(new QRow(q)); Downloader.Changed(); });
            room[shorts] -= need; free -= need; added++;
        }
        status(added == 0 ? "Подходящих видео, которые влезли бы в свободное место, не нашлось"
            : $"Подобрано {added} — проверьте список и нажмите «Подтвердить загрузку»");
    }

    /// <summary>
    /// The wizard's third step: the best picks that fit each budget (videos / Shorts). Removing one
    /// pulls in the next best that still fits — the list always fills the space you gave it.
    /// </summary>
    public sealed class Selection(List<Pick> ranked, long videoBytes, long shortBytes, List<Pick>? pinned = null)
    {
        readonly List<Pick> _pool = [.. ranked];
        public System.Collections.ObjectModel.ObservableCollection<Pick> Videos { get; } = [];
        public System.Collections.ObjectModel.ObservableCollection<Pick> Shorts { get; } = [];
        public long VideoBudget { get; } = videoBytes;
        public long ShortBudget { get; } = shortBytes;
        public long Used(bool shorts) => (shorts ? Shorts : Videos).Sum(p => p.Estimate);

        public Selection Fill()
        {
            // videos added by link go in first, whatever their size; the best of the rest fill what space is left
            foreach (var p in pinned ?? []) (p.Kind == "short" ? Shorts : Videos).Add(p);
            pinned = null;
            foreach (var shorts in new[] { false, true })
            {
                var list = shorts ? Shorts : Videos;
                var room = (shorts ? ShortBudget : VideoBudget) - Used(shorts);
                foreach (var p in _pool.Where(p => (p.Kind == "short") == shorts).ToList())
                {
                    if (p.Estimate > room) continue;
                    list.Add(p); _pool.Remove(p); room -= p.Estimate;
                }
            }
            return this;
        }

        public void Remove(Pick p) { Videos.Remove(p); Shorts.Remove(p); Fill(); }
    }
}
