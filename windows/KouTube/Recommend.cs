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
        Dictionary<string, double> Words, double MedianMinutes, double ShortsBias, Dictionary<string, int> WordVideos,
        Dictionary<string, int> YtSeen, Dictionary<string, string> ChannelIds, HashSet<string> Subscribed)
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
        if (v.Meta.YtId is { } y && Lib.Config.Linked.Contains(y)) s += 1.5;                           // you asked for it by link
        if (v.Flags.Rating < 0) s -= 2.5;
        if (v.Watch.Status == Watch.Finished) s += 1;
        if (v.FileState.State == FileState.ByUser && v.Watch.Status != Watch.Finished) s -= 1.5;         // deleted without watching
        if (v.Watch.Status == Watch.Started && v.Meta.DurationMs > 0 && v.Watch.PosMs < v.Meta.DurationMs * 0.25
            && v.Watch.LastWatchedAt < Format.Now() - 3 * 86_400_000L) s -= 0.7;                           // gave up early
        return s;
    }

    /// <summary>The library, plus (signed in) what you watched on YouTube — each a smaller "yes" than a video you finished here.</summary>
    public static Interests Learn(List<Video> all, List<Account.Watched>? yt = null, List<Account.Channel>? subs = null)
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
        var ytSeen = new Dictionary<string, int>();
        var ids = new Dictionary<string, string>();
        foreach (var h in yt ?? [])
        {
            // watched to the end on YouTube = liked; dropped in the first quarter = not really
            var sig = h.Percent switch { >= 90 => 1.0, < 25 => -0.3, _ => 0.4 };
            if (h.Channel is { } c)
            {
                ch[c] = ch.GetValueOrDefault(c) + sig;
                ytSeen[c] = ytSeen.GetValueOrDefault(c) + 1;
                if (h.ChannelId != null) ids[c] = h.ChannelId;
            }
            foreach (var w in Words(h.Title).Distinct()) { words[w] = words.GetValueOrDefault(w) + sig * 0.6; inVideos[w] = inVideos.GetValueOrDefault(w) + 1; }
        }
        var subscribed = new HashSet<string>();
        foreach (var s in subs ?? [])       // a subscription is a firm "I like this channel"
        {
            ch[s.Name] = ch.GetValueOrDefault(s.Name) + 1;
            ids[s.Name] = s.Id;
            subscribed.Add(s.Name);
        }
        var liked = all.Where(v => Signal(v) > 0).Select(v => (v.Meta.DurationMs ?? 0) / 60000.0).Where(m => m > 0).Order().ToList();
        if (liked.Count == 0) liked = all.Select(v => (v.Meta.DurationMs ?? 0) / 60000.0).Where(m => m > 0).Order().ToList();
        double Bias(bool shorts) { var l = all.Where(v => v.IsShort == shorts && Signal(v) != 0).ToList(); return l.Count == 0 ? 0 : l.Average(Signal); }
        return new(ch, counts, words, liked.Count > 0 ? liked[liked.Count / 2] : 20, Bias(true) - Bias(false), inVideos, ytSeen, ids, subscribed);
    }

    // ---------- candidates from the sources ----------

    /// <summary>
    /// "Just pick something for me": with no channels or words chosen, the library and your YouTube history
    /// speak for themselves — the channels you like most (those we know the id of) and your top topics.
    /// </summary>
    public static Config Implicit(Config c, Interests it)
    {
        if (c.Sources.Count + c.Keywords.Count > 0) return c;
        string? Id(string name) => c.Avatars.TryGetValue(name, out var a) ? Path.GetFileNameWithoutExtension(a) : it.ChannelIds.GetValueOrDefault(name);
        var chans = it.Channels.Where(x => x.Value > 0 && Id(x.Key) != null).OrderByDescending(x => x.Value).Take(6)
            .Select(x => new Source("https://www.youtube.com/channel/" + Id(x.Key), x.Key, "channel")).ToList();
        return c with { Sources = chans, Keywords = it.Topics.Take(3).ToList() };
    }

    /// <summary>How far down each list to look so the space can be filled ~3 times over: big budgets need deep lists.</summary>
    public static (int Videos, int Shorts) Depth(Config c, Interests it, long videoBytes, long shortBytes)
    {
        var lists = Math.Max(1, c.Sources.Count + c.Keywords.Count);
        var video = Math.Max(3, it.MedianMinutes) * MbPerMin * (1L << 20);
        var shorts = 0.75 * MbPerMin * (1L << 20);
        return ((int)Math.Clamp(videoBytes / video * 3 / lists, Math.Min(c.PerSource, 20), 200),
                (int)Math.Clamp(shortBytes / shorts * 2 / lists, Math.Min(c.PerSource, 20), 300));
    }

    static JsonElement Flat(string url, int n) => Tools.Json(Tools.YtDlp, ["--flat-playlist", "-J", "--playlist-end", n.ToString(), "--no-warnings",
        "--extractor-args", "youtubetab:approximate_date", url], 300_000);

    /// <summary>One list (channel tab, search, feed) → picks. <paramref name="prior"/> is the list's own say: YouTube's picks for you start higher.</summary>
    static List<Pick> Parse(JsonElement j, string source, double prior, bool shortsTab, string? channel, Func<DateTimeOffset?, bool> fresh)
    {
        var l = new List<Pick>();
        if (!j.TryGetProperty("entries", out var es) || es.ValueKind != JsonValueKind.Array) return l;
        foreach (var e in es.EnumerateArray())
        {
            // null: an entry lost with a failed page; channels and playlists among search results have longer ids
            if (e.ValueKind != JsonValueKind.Object || Str(e, "id") is not { Length: 11 } id) continue;
            DateTimeOffset? pub = Long(e, "timestamp") is { } ts ? DateTimeOffset.FromUnixTimeSeconds(ts) : null;
            if (!fresh(pub)) continue;
            var isShort = shortsTab || (Str(e, "url") ?? "").Contains("/shorts/");
            l.Add(new Pick(id, (isShort ? "https://www.youtube.com/shorts/" : "https://www.youtube.com/watch?v=") + id, Str(e, "title") ?? id,
                Str(e, "channel") ?? Str(e, "uploader") ?? channel, Dbl(e, "duration"), pub, isShort ? "short" : "video", prior, "", source, ThumbOf(e, id, isShort)));
        }
        return l;
    }

    /// <summary>
    /// Everything worth considering, four lists at a time: each source's newest videos and Shorts, YouTube's
    /// search for each keyword (plus its #hashtag Shorts) and, signed in, «Смотреть позже», YouTube's
    /// recommendations and the subscriptions feed. <paramref name="status"/> gets the share of lists done.
    /// </summary>
    public static List<Pick> Candidates(Config c, Action<string, double> status, int depth, int shortDepth)
    {
        var now = DateTimeOffset.UtcNow;
        bool Fresh(DateTimeOffset? pub, Source? s) =>      // Shorts and search results come without a date: they pass the window
            pub is not { } p || (now - p).TotalDays is var age && age >= (s?.MinAgeDays ?? c.MinAgeDays ?? 0) && age <= (s?.MaxAgeDays ?? c.MaxAgeDays ?? double.MaxValue);
        var jobs = new List<(string What, Func<List<Pick>> Run)>();
        foreach (var src in c.Sources)
        {
            var u = src.Url.TrimEnd('/');
            jobs.Add(($"«{src.Name}»: видео", () => Parse(Flat(u + "/videos", depth), src.Name, 0, false, src.Name, p => Fresh(p, src))));
            jobs.Add(($"«{src.Name}»: Shorts", () => Parse(Flat(u + "/shorts", shortDepth), src.Name, 0, true, src.Name, p => Fresh(p, src))));
        }
        // YouTube's own upload-date filter for "only new" (yt-dlp's ytsearchdate is gone): this week / this month
        var since = c.MaxAgeDays switch { <= 7 => "&sp=EgIIAw%253D%253D", <= 31 => "&sp=EgIIBA%253D%253D", _ => "" };
        foreach (var kw in c.Keywords)
        {
            var tag = new string(kw.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            if (tag.Length > 0)
                jobs.Add(($"Shorts #{tag}", () => Parse(Flat("https://www.youtube.com/hashtag/" + tag + "/shorts", shortDepth),
                    "Shorts «#" + tag + "»", 0, true, null, _ => true)));
            jobs.Add(($"поиск «{kw}»", () => Parse(Flat("https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(kw) + since, Math.Max(depth, 100)),
                "поиск «" + kw + "»", 0, false, null, _ => true)));
        }
        if (Account.SignedIn)
            foreach (var (feed, name, prior, n) in new[] { (":ytwatchlater", "«Смотреть позже»", 3.0, 300), (":ytrec", "рекомендации YouTube", 1.5, Math.Max(depth * 3, 150)),
                         (":ytsubs", "ваши подписки", 1.0, Math.Max(depth * 3, 150)) })
                jobs.Add((name, () => Parse(Account.Feed(feed, n), name, prior, false, null, p => feed == ":ytwatchlater" || Fresh(p, null))));

        var bag = new System.Collections.Concurrent.ConcurrentBag<Pick>();
        var done = 0;
        status($"Смотрю {jobs.Count} списков на YouTube…", 0);
        Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = 4 }, job =>
        {
            var what = "Готово: " + job.What;
            try { foreach (var p in job.Run()) bag.Add(p); }
            catch (Exception ex) { what = $"Не открылось: {job.What} — {ex.Message}"; }    // a channel without Shorts, a hiccup: the other lists still count
            status(what, Interlocked.Increment(ref done) / (double)jobs.Count);
        });
        // the same video in several lists: the strongest say wins
        return bag.GroupBy(p => p.Id).Select(g => g.MaxBy(p => p.Score)!).ToList();
    }

    /// <summary>Shorts need their own vertical picture; for videos YouTube's 16:9 one by ID.</summary>
    static string ThumbOf(JsonElement e, string id, bool shorts) =>
        shorts && e.TryGetProperty("thumbnails", out var ts) && ts.ValueKind == JsonValueKind.Array && ts.GetArrayLength() > 0
            && ts[0].ValueKind == JsonValueKind.Object && Str(ts[0], "url") is { } u ? u : $"https://i.ytimg.com/vi/{id}/mqdefault.jpg";

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
            double score = p.Score;                 // «Смотреть позже» and YouTube's own picks start ahead
            if (p.Score > 0) why.Add(p.Source);
            if (p.Channel != null && it.Channels.TryGetValue(p.Channel, out var cw))
            {
                score += 2.5 * Math.Tanh(cw / 3);
                var (f, t, l) = it.Counts.GetValueOrDefault(p.Channel);
                var yt = it.YtSeen.GetValueOrDefault(p.Channel);
                if (cw > 0) why.Add($"канал «{p.Channel}»: " + string.Join(", ", new[] { l > 0 ? $"👍 {l}" : null,
                    f > 0 ? $"досмотрено {f} из {t}" : t > 0 ? $"уже {t} в библиотеке" : null, it.Subscribed.Contains(p.Channel) ? "вы подписаны" : null,
                    yt > 0 ? $"на YouTube смотрели {yt}" : null }.OfType<string>()));
            }
            else if (p.Score <= 0) why.Add($"источник «{p.Source}»");
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
        var ranked = Rank(await Task.Run(() => Candidates(c, (s, _) => status(s), c.PerSource, c.PerSource), ct), it, skip);
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
    public sealed class Selection(List<Pick> ranked, long videoBytes, long shortBytes, List<Pick>? pinned = null, bool fillAll = false)
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
            var total = VideoBudget + ShortBudget;
            Take(false, VideoBudget - Used(false));
            if (fillAll) Take(false, VideoBudget + ShortBudget / 4 - Used(false));    // a good video just over the line takes a few Shorts' place
            Take(true, (fillAll ? total - Used(false) : ShortBudget) - Used(true));
            if (fillAll)                                                               // the rest, too small for any video, gets more Shorts
                foreach (var shorts in new[] { false, true }) Take(shorts, total - Used(false) - Used(true));
            return this;
        }

        /// <summary>The best picks of one kind that fit in <paramref name="room"/>, in rank order.</summary>
        void Take(bool shorts, long room)
        {
            foreach (var p in _pool.Where(p => (p.Kind == "short") == shorts).ToList())
            {
                if (p.Estimate > room) continue;
                (shorts ? Shorts : Videos).Add(p); _pool.Remove(p); room -= p.Estimate;
            }
        }

        public void Remove(Pick p) { Videos.Remove(p); Shorts.Remove(p); Fill(); }
    }
}
