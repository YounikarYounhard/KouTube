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

    /// <summary>Covers fetched ahead, url → picture; a card without one loads its cover itself.</summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Windows.Media.ImageSource> Pics = new();
    public object? Img => Thumb != null && Pics.TryGetValue(Thumb, out var i) ? i : Thumb;
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
        Dictionary<string, int> YtSeen, Dictionary<string, string> ChannelIds, HashSet<string> Subscribed, HashSet<char> Scripts, HashSet<string> Blocked)
    {
        /// <summary>Words that make a topic: liked and found in at least two different videos (one title's "откуда" is not a topic).</summary>
        public IEnumerable<string> Topics => Words.Where(x => x.Value > 0 && WordVideos.GetValueOrDefault(x.Key) >= 2).OrderByDescending(x => x.Value).Select(x => x.Key);
    }

    // words of four letters and more that say nothing about a topic ("тебя", "после", "который") — titles are full of them
    static readonly HashSet<string> Stop = ["это", "этот", "эта", "этого", "этой", "этом", "этих", "этим", "эти", "как", "что", "чем", "для", "или", "когда", "если",
        "весь", "всех", "всё", "все", "всего", "всем", "вообще", "самый", "самая", "самое", "самые", "тебя", "меня", "себя", "тебе", "мной", "него", "нему",
        "после", "перед", "через", "между", "против", "около", "который", "которая", "которое", "которые", "которых", "новый", "новая", "новое", "новые",
        "снова", "опять", "очень", "можно", "нужно", "надо", "будет", "будут", "было", "были", "была", "есть", "тоже", "даже", "потом", "только", "просто",
        "теперь", "сейчас", "здесь", "там", "почему", "зачем", "сколько", "какой", "какая", "какие", "минут", "минуты", "минута", "часов", "часа", "день",
        "дней", "года", "году", "лет", "раз", "первый", "лучший", "лучшие", "много", "один", "одна", "одно", "одного", "двух", "часть", "выпуск", "серия",
        "сезон", "полное", "полный", "видео", "ролик", "смотреть", "the", "and", "with", "from", "this", "that", "your", "what", "when", "have", "will", "best",
        "first", "video", "part", "episode", "full", "shorts", "short", "official", "about", "into", "just", "like", "more", "most", "than", "they", "their"];

    public static IEnumerable<string> Words(string title) =>
        Regex.Split(Format.UnFullwidth(title).ToLowerInvariant(), @"[^\p{L}\p{Nd}]+").Where(w => w.Length >= 4 && !Stop.Contains(w) && !w.All(char.IsDigit));   // "2026" is no topic

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
        var hn = Math.Max(1, (yt ?? []).Count);
        foreach (var (h, i) in (yt ?? []).Select((h, i) => (h, i)))
        {
            if (IsMusic(h.Title, h.Channel)) continue;       // music you play says nothing about the videos you want
            // watched to the end on YouTube = liked; dropped in the first quarter = not really. The history is newest
            // first, and the newest weigh most: a channel you took to this month outranks one from spring
            var sig = h.Percent switch { >= 90 => 1.0, < 25 => -0.3, _ => 0.4 } * (1 - 0.6 * i / hn);
            if (h.Channel is { } c)
            {
                ch[c] = ch.GetValueOrDefault(c) + sig;
                ytSeen[c] = ytSeen.GetValueOrDefault(c) + 1;
                if (h.ChannelId != null) ids[c] = h.ChannelId;
            }
            foreach (var w in Words(h.Title).Distinct()) { words[w] = words.GetValueOrDefault(w) + sig * 0.6; inVideos[w] = inVideos.GetValueOrDefault(w) + 1; }
        }
        // ✕ in «Подборка» = "not this": its channel and words go down; two ✕ and the channel is not offered any more
        foreach (var x in Lib.Config.NotInterested)
        {
            if (x.Channel is { } c) ch[c] = ch.GetValueOrDefault(c) - 1;
            foreach (var w in Words(x.Title).Distinct()) words[w] = words.GetValueOrDefault(w) - 0.3;
        }
        var blocked = Lib.Config.NotInterested.Where(x => x.Channel != null).GroupBy(x => x.Channel!).Where(g => g.Count() >= 2).Select(g => g.Key).ToHashSet();
        // the writing systems you watch in (Cyrillic, Latin, …): three titles make one yours
        var scripts = all.Select(v => v.Title).Concat((yt ?? []).Select(h => h.Title)).Select(Script).Where(s => s != ' ')
            .GroupBy(s => s).Where(g => g.Count() >= 3).Select(g => g.Key).ToHashSet();
        var subscribed = new HashSet<string>();
        foreach (var s in subs ?? [])       // a subscription is a firm "I like this channel"
        {
            ch[s.Name] = ch.GetValueOrDefault(s.Name) + 1;
            ids[s.Name] = s.Id;
            subscribed.Add(s.Name);
        }
        // the lengths you finish — here and on YouTube — make the "familiar length" of a video
        var liked = all.Where(v => Signal(v) > 0).Select(v => (v.Meta.DurationMs ?? 0) / 60000.0)
            .Concat((yt ?? []).Where(h => h.Percent >= 90 && h.Seconds > 60).Select(h => h.Seconds!.Value / 60)).Where(m => m > 0).Order().ToList();
        if (liked.Count == 0) liked = all.Select(v => (v.Meta.DurationMs ?? 0) / 60000.0).Where(m => m > 0).Order().ToList();
        double Bias(bool shorts) { var l = all.Where(v => v.IsShort == shorts && Signal(v) != 0).ToList(); return l.Count == 0 ? 0 : l.Average(Signal); }
        return new(ch, counts, words, liked.Count > 0 ? liked[liked.Count / 2] : 20, Bias(true) - Bias(false), inVideos, ytSeen, ids, subscribed, scripts, blocked);
    }

    // ---------- candidates from the sources ----------

    /// <summary>
    /// "Just pick something for me": with no channels or words chosen, the library and your YouTube history
    /// speak for themselves. Signed in, YouTube's own lists carry the videos; the channels you finish add their
    /// newest, and Shorts come from the many channels you watch — a few each. Otherwise your top channels and topics.
    /// Returns the config to search with and the channels whose Shorts to look at.
    /// </summary>
    public static (Config Config, List<Source> ShortChannels) Implicit(Config c, Interests it, bool account)
    {
        if (c.Sources.Count + c.Keywords.Count > 0) return (c, c.Sources);
        string? Id(string name) => c.Avatars.TryGetValue(name, out var a) ? Path.GetFileNameWithoutExtension(a) : it.ChannelIds.GetValueOrDefault(name);
        var exclude = c.Exclude.Select(Plain).Where(w => w.Length > 0).ToList();     // a phonk channel takes no seat if "phonk" is excluded
        var liked = it.Channels.Where(x => x.Value > 0 && Id(x.Key) != null && !exclude.Any(Plain(x.Key).Contains) && !IsMusic("", x.Key)).OrderByDescending(x => x.Value)
            .Select(x => new Source("https://www.youtube.com/channel/" + Id(x.Key), x.Key, "channel")).ToList();
        return account ? (c with { Sources = liked.Take(12).ToList(), Keywords = [] }, liked.Take(150).ToList())
                       : (c with { Sources = liked.Take(6).ToList(), Keywords = it.Topics.Take(3).ToList() }, liked.Take(6).ToList());
    }

    /// <summary>How far down each list to look so the space can be filled ~3 times over: big budgets need deep lists.</summary>
    public static (int Videos, int Shorts) Depth(Config c, Interests it, long videoBytes, long shortBytes, int shortLists)
    {
        var lists = Math.Max(1, c.Sources.Count + c.Keywords.Count);
        var video = Math.Max(3, it.MedianMinutes) * MbPerMin * (1L << 20);
        var shorts = 0.75 * MbPerMin * (1L << 20);
        return ((int)Math.Clamp(videoBytes / video * 3 / lists, Math.Min(c.PerSource, 20), 200),
                (int)Math.Clamp(shortBytes / shorts * 2 / Math.Max(1, shortLists), 6, 300));
    }

    static JsonElement Flat(string url, int n) => Tools.Json(Tools.YtDlp, ["--flat-playlist", "-J", "--playlist-end", n.ToString(), "--no-warnings",
        "--extractor-args", "youtubetab:approximate_date", url], 300_000);

    /// <summary>One list (channel tab, search, feed) → picks. <paramref name="prior"/> is the list's own say: YouTube's picks for you start higher.</summary>
    static List<Pick> Parse(JsonElement j, string source, double prior, bool shortsTab, string? channel, Func<DateTimeOffset?, bool> fresh, double decay = 0)
    {
        var l = new List<Pick>();
        if (!j.TryGetProperty("entries", out var es) || es.ValueKind != JsonValueKind.Array) return l;
        var (n, i) = (Math.Max(1, es.GetArrayLength()), 0);
        foreach (var e in es.EnumerateArray())
        {
            var say = prior - decay * i++ / n;      // the home page's top is YouTube's surest guess
            // null: an entry lost with a failed page; channels and playlists among search results have longer ids
            if (e.ValueKind != JsonValueKind.Object || Str(e, "id") is not { Length: 11 } id) continue;
            // a premiere or a stream not over yet, or a members-only video: nothing to download, only a failure in the queue
            if (Str(e, "live_status") is "is_upcoming" or "is_live" || Str(e, "availability") is "subscriber_only" or "premium_only" or "needs_auth") continue;
            DateTimeOffset? pub = Long(e, "timestamp") is { } ts ? DateTimeOffset.FromUnixTimeSeconds(ts) : null;
            if (!fresh(pub)) continue;
            var isShort = shortsTab || (Str(e, "url") ?? "").Contains("/shorts/");
            l.Add(new Pick(id, (isShort ? "https://www.youtube.com/shorts/" : "https://www.youtube.com/watch?v=") + id, Str(e, "title") ?? id,
                Str(e, "channel") ?? Str(e, "uploader") ?? channel, Dbl(e, "duration"), pub, isShort ? "short" : "video", say, "", source, ThumbOf(e, id, isShort)));
        }
        return l;
    }

    /// <summary>
    /// Everything worth considering, four lists at a time: each source's newest videos and Shorts, YouTube's
    /// search for each keyword (plus its #hashtag Shorts) and, signed in, «Смотреть позже», YouTube's
    /// recommendations and the subscriptions feed. <paramref name="status"/> gets the share of lists done.
    /// </summary>
    public static List<Pick> Candidates(Config c, List<Source> shortChannels, Action<string, double> status, int depth, int shortDepth)
    {
        var now = DateTimeOffset.UtcNow;
        bool Fresh(DateTimeOffset? pub, Source? s) =>      // Shorts and search results come without a date: they pass the window
            pub is not { } p || (now - p).TotalDays is var age && age >= (s?.MinAgeDays ?? c.MinAgeDays ?? 0) && age <= (s?.MaxAgeDays ?? c.MaxAgeDays ?? double.MaxValue);
        var jobs = new List<(string What, Func<List<Pick>> Run)>();
        foreach (var src in c.Sources)
        {
            var u = src.Url.TrimEnd('/');
            jobs.Add(($"«{src.Name}»: видео", () => Parse(Flat(u + "/videos", depth), src.Name, 0, false, src.Name, p => Fresh(p, src))));
        }
        foreach (var src in shortChannels)
            jobs.Add(($"«{src.Name}»: Shorts", () => Parse(Flat(src.Url.TrimEnd('/') + "/shorts", shortDepth), src.Name, 0, true, src.Name, p => Fresh(p, src))));
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
        {
            // the home page, scrolled ~300 deep: YouTube's own picks for you, the Shorts shelf included
            for (var pass = 1; pass <= 3; pass++)      // each visit of the home page brings ~100 videos the last one did not
                jobs.Add(($"главная YouTube ({pass} из 3)", () =>
            {
                var (j, pages) = Account.FeedPages(":ytrec", 300);
                return [.. Parse(j, "главная YouTube", 3, false, null, p => Fresh(p, null), 1.5),
                        .. Account.ShortsIn(pages).Select(s => new Pick(s.Id, "https://www.youtube.com/shorts/" + s.Id, s.Title, null, null, null, "short", 2.5, "",
                            "Shorts с главной YouTube", $"https://i.ytimg.com/vi/{s.Id}/oar2.jpg"))];
            }));
            jobs.Add(("Shorts из подписок", () => Parse(Account.Feed("https://www.youtube.com/feed/subscriptions/shorts", 300), "Shorts из подписок", 2, true, null, _ => true, 1)));
            if (c.UseWatchLater) jobs.Add(("«Смотреть позже»", () => Parse(Account.Feed(":ytwatchlater", 300), "«Смотреть позже»", 2, false, null, _ => true)));
            jobs.Add(("ваши подписки", () => Parse(Account.Feed(":ytsubs", 200), "ваши подписки", 1, false, null, p => Fresh(p, null))));
        }

        var bag = new System.Collections.Concurrent.ConcurrentBag<Pick>();
        var done = 0;
        status($"Смотрю {jobs.Count} списков на YouTube…", 0);
        Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = 4 }, job =>
        {
            var what = "Готово: " + job.What;
            try { foreach (var p in job.Run()) bag.Add(p); }
            catch (Exception ex) when (!ex.Message.Contains("does not have a")) { what = $"Не открылось: {job.What} — {ex.Message}"; }
            catch (Exception) { }       // a channel without Shorts is no failure    // a channel without Shorts, a hiccup: the other lists still count
            status(what, Interlocked.Increment(ref done) / (double)jobs.Count);
        });
        // the same video in several lists (or on the home page twice): the strongest say wins, each extra sighting adds a little
        return bag.GroupBy(p => p.Id).Select(g => g.MaxBy(p => p.Score)! with { Score = g.Max(p => p.Score) + 0.3 * (g.Count() - 1) }).ToList();
    }

    static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>Covers fetched ahead, a dozen at a time, decoded at the card's size — so the picks show up whole and scroll smoothly.</summary>
    public static async Task Covers(List<Pick> picks, Action<double> progress)
    {
        var todo = picks.Where(p => p.Thumb != null && !Pick.Pics.ContainsKey(p.Thumb)).DistinctBy(p => p.Thumb).ToList();
        var done = 0;
        await Parallel.ForEachAsync(todo, new ParallelOptions { MaxDegreeOfParallelism = 12 }, async (p, ct) =>
        {
            try
            {
                var bytes = await Http.GetByteArrayAsync(p.Thumb, ct);       // first the bytes: the picture must be built on one thread
                var b = new System.Windows.Media.Imaging.BitmapImage();
                b.BeginInit();
                b.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                b.DecodePixelWidth = p.Kind == "short" ? 150 : 250;      // the card's size: hundreds of Shorts stay light
                b.StreamSource = new MemoryStream(bytes);
                b.EndInit();
                b.Freeze();
                Pick.Pics[p.Thumb!] = b;
            }
            catch (Exception) { }       // no cover now: the card tries again itself
            progress(Interlocked.Increment(ref done) / (double)Math.Max(1, todo.Count));
        });
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
        var exclude = Lib.Config.Exclude.Select(Plain).Where(w => w.Length > 0).ToList();
        return cands.Where(p => !skip.Contains(p.Id) && !IsMusic(p.Title, p.Channel) && (exclude.Count == 0 || !exclude.Any(Plain(p.Title + " " + p.Channel).Contains)) && (p.Channel == null || !it.Blocked.Contains(p.Channel)) && (it.Scripts.Count == 0 || Script(p.Title) is var s && (s == ' ' || it.Scripts.Contains(s)))).Select(p =>
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
            score += Math.Clamp(topical, -1.5, 1.5);
            var good = shared.Where(w => it.Words[w] > 0).OrderByDescending(w => it.Words[w]).Take(3).ToList();
            if (good.Count > 0) why.Add("темы: " + string.Join(", ", good));
            if (p.DurationSec is { } d && p.Kind == "video")
            {
                // the length you usually finish scores best, and every doubling away from it costs more: a three-hour stream
                // takes the room of ten ordinary videos, so it has to be really wanted to get in
                var ratio = d / 60 / Math.Max(1, it.MedianMinutes);
                score += Math.Clamp(0.5 - 0.6 * Math.Abs(Math.Log2(Math.Max(ratio, 0.01))), -2, 0.5);
            }
            score += (p.Kind == "short" ? 1 : -1) * Math.Clamp(it.ShortsBias, -1, 1) * 0.5;
            if (p.Published is { } pub) score += Math.Max(0, 1 - (DateTimeOffset.UtcNow - pub).TotalDays / 60) * 0.5;   // a little nudge for fresh
            return p with { Score = score, Reason = string.Join("; ", why) };
        }).Where(p => p.Score > -1)              // what you clearly dislike does not come back
          .OrderByDescending(p => p.Score).ToList();
    }

    /// <summary>
    /// Variety like YouTube's: the k-th pick of one channel loses k points × 0.8 (Shorts × 1.5), and a title that
    /// says the same as one already taken or watched — one stream cut by ten channels — is dropped.
    /// </summary>
    public static List<Pick> Diversify(List<Pick> ranked, IEnumerable<string> seenTitles)
    {
        var kept = seenTitles.Select(t => Words(t).ToHashSet()).Where(w => w.Count >= 3).ToList();
        var outList = new List<Pick>();
        foreach (var p in ranked.GroupBy(p => (p.Kind, p.Channel ?? p.Id))
                     .SelectMany(g => g.OrderByDescending(p => p.Score).Select((p, k) => p with { Score = p.Score - k * (p.Kind == "short" ? 1.5 : 0.8) }))
                     .OrderByDescending(p => p.Score))
        {
            var w = Words(p.Title).ToHashSet();
            if (w.Count >= 3 && kept.Any(k => Jaccard(k, w) >= 0.6)) continue;
            if (w.Count >= 3) kept.Add(w);
            outList.Add(p);
        }
        return outList;
    }

    /// <summary>Lower case with fancy letters made plain: 𝙋𝙃𝙊𝙉𝙆 and ＰＨＯＮＫ are "phonk" too.</summary>
    static readonly HashSet<string> MusicWords = ["phonk", "фонк", "slowed", "reverb", "remix", "ремикс", "nightcore", "mashup", "мэшап", "lofi", "funk",
        "jumpstyle", "hardstyle", "amv", "edit", "edits", "эдит", "эдиты", "lyrics", "speedup", "spedup", "клип", "capcut", "velocity", "alightmotion",
        "aftereffects", "sigmaedit"];
    static readonly string[] MusicPhrases = ["sped up", "speed up", "type beat", "official audio", "official music video", "music video", "bass boosted",
        "8d audio", "full album", "(audio", "audio)", "текст песни"];

    /// <summary>
    /// Music made into a video — phonk, slowed + reverb, anime edits, a still picture with a track — is never picked: it is a
    /// few MB of sound worth getting elsewhere, and it floods every list. Told by its words, by 𝙛𝙖𝙣𝙘𝙮 letters (the edit
    /// scene's typeface) and music notes, and by music channels ("… - Topic", "… Music", "… Records").
    /// </summary>
    public static bool IsMusic(string title, string? channel)
    {
        var raw = title + " " + channel;
        if (raw.Contains('\uD835') || raw.Contains("🎼") || raw.Contains("🎵") || raw.Contains("🎶") || raw.Contains("🎧")) return true;
        if (channel != null && (channel.EndsWith(" - Topic") || Regex.IsMatch(channel, @"\b(music|records|vevo|phonk|edits?)\b", RegexOptions.IgnoreCase))) return true;
        var t = Plain(title);
        // #animeedit, #gojoedit, #phonkmusic, #capcut, #velocity: an edit often says so only in its hashtags
        return MusicPhrases.Any(t.Contains) || Regex.Split(t, @"[^\p{L}\p{Nd}]+").Any(w => MusicWords.Contains(w)
            || (w.EndsWith("edit") || w.EndsWith("edits")) && w is not ("credit" or "credits") || w.Contains("phonk") || w.Contains("фонк"));
    }

    static string Plain(string s) => s.Normalize(System.Text.NormalizationForm.FormKC).ToLowerInvariant();

    static double Jaccard(HashSet<string> a, HashSet<string> b) { var i = a.Count(b.Contains); return i / (double)(a.Count + b.Count - i); }

    /// <summary>A title's writing system: 'c' Cyrillic, 'l' Latin, 'o' anything else, ' ' no letters (emoji, #tags of digits).</summary>
    public static char Script(string title)
    {
        int c = 0, l = 0, o = 0;
        foreach (var ch in title.Where(char.IsLetter))
            if (ch is >= '\u0400' and <= '\u04FF') c++; else if (ch < '\u0250') l++; else o++;
        return c + l + o == 0 ? ' ' : c >= l && c >= o ? 'c' : l >= o ? 'l' : 'o';
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
        var ranked = Rank(await Task.Run(() => Candidates(c, c.Sources, (s, _) => status(s), c.PerSource, c.PerSource), ct), it, skip);
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
        /// <summary>The next best, waiting to step in for a ✕.</summary>
        public IEnumerable<Pick> Spare => _pool.Take(80);

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

        /// <summary>Out of the list, the next best moves in. <paramref name="dislike"/>: the rest of that channel steps back at once.</summary>
        public void Remove(Pick p, bool dislike = false)
        {
            Videos.Remove(p); Shorts.Remove(p);
            if (dislike && p.Channel != null)
            {
                for (var i = 0; i < _pool.Count; i++) if (_pool[i].Channel == p.Channel) _pool[i] = _pool[i] with { Score = _pool[i].Score - 1.5 };
                _pool.Sort((a, b) => b.Score.CompareTo(a.Score));
            }
            Fill();
        }
    }
}
