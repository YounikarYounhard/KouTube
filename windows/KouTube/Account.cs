// Your YouTube account, for picking only: history, subscriptions, YouTube's recommendations and
// «Смотреть позже» are read with its cookies. Downloads never use them. You sign in on Google's own
// page inside a WebView2 window; KouTube never sees the password, only the cookies YouTube sets after.

using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace KouTube;

public static class Account
{
    static string Dir => Path.Combine(Lib.LocalDir, "youtube");
    static string CookieFile => Path.Combine(Dir, "cookies.txt");
    public static bool SignedIn => File.Exists(CookieFile);
    // yt-dlp writes refreshed cookies back into the file: one yt-dlp at a time may use it
    static readonly SemaphoreSlim One = new(1, 1);

    /// <summary><paramref name="Percent"/>: how much of it you watched (YouTube's red bar), null when YouTube shows none.</summary>
    public record Watched(string Id, string Title, string? Channel, string? ChannelId, int? Percent, double? Seconds = null);
    public record Channel(string Id, string Name);

    public static bool SignIn(Window owner)
    {
        var w = new Window { Title = "Вход в YouTube", Width = 560, Height = 780, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var web = new WebView2();
        w.Content = web;
        var ok = false;
        w.Loaded += async (_, _) =>
        {
            try { await web.EnsureCoreWebView2Async(await CoreWebView2Environment.CreateAsync(null, Path.Combine(Dir, "webview"))); }
            catch (Exception ex)
            {
                MessageBox.Show(w, "Не открылось окно входа: " + ex.Message + "\n\nНужен Microsoft Edge WebView2 Runtime (в Windows 10/11 обычно уже есть).", "KouTube");
                w.Close();
                return;
            }
            const string login = "https://accounts.google.com/ServiceLogin?service=youtube&passive=true&hl=ru&continue=https%3A%2F%2Fwww.youtube.com%2F";
            var retries = 0;
            web.CoreWebView2.NavigationCompleted += async (_, _) =>
            {
                if (ok || !web.Source.Host.EndsWith("youtube.com")) return;
                var cookies = await web.CoreWebView2.CookieManager.GetCookiesAsync("https://www.youtube.com");
                if (cookies.Any(c => c.Name == "LOGIN_INFO")) { Save(cookies); ok = true; w.Close(); return; }
                // signed in to Google, yet YouTube is still a guest (Google's security check broke the hop back):
                // one more pass through Google's sign-in, which now goes straight through and sets YouTube's cookies
                var google = await web.CoreWebView2.CookieManager.GetCookiesAsync("https://accounts.google.com");
                if (google.Any(c => c.Name == "SID") && retries++ < 2) web.CoreWebView2.Navigate(login);
            };
            web.CoreWebView2.Navigate(login);
        };
        w.ShowDialog();
        web.Dispose();
        return ok;
    }

    /// <summary>The cookies in the Netscape format yt-dlp reads.</summary>
    static void Save(IEnumerable<CoreWebView2Cookie> cs)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllLines(CookieFile, ["# Netscape HTTP Cookie File", .. cs.Select(c => string.Join('\t',
            (c.IsHttpOnly ? "#HttpOnly_" : "") + c.Domain, c.Domain.StartsWith('.') ? "TRUE" : "FALSE", c.Path, c.IsSecure ? "TRUE" : "FALSE",
            c.IsSession ? "0" : new DateTimeOffset(c.Expires).ToUnixTimeSeconds().ToString(), c.Name, c.Value))]);
    }

    public static void SignOut()
    {
        try { Directory.Delete(Dir, true); } catch (Exception) { }
        try { File.Delete(CookieFile); } catch (Exception) { }     // if the WebView still holds its folder, the cookies at least go
    }

    /// <summary>One of the account's lists (":ytrec", ":ytsubs", ":ytwatchlater", a feed URL) as yt-dlp's flat JSON.</summary>
    public static JsonElement Feed(string feed, int n) => FeedPages(feed, n, false).Json;

    /// <summary>The same, plus the raw pages YouTube sent (yt-dlp --write-pages): they hold what its list leaves out.</summary>
    public static (JsonElement Json, string Pages) FeedPages(string feed, int n, bool pages = true)
    {
        One.Wait();
        var dir = Path.Combine(Dir, "pages");
        try
        {
            if (pages) { try { Directory.Delete(dir, true); } catch (Exception) { } Directory.CreateDirectory(dir); }
            var j = Tools.Json(Tools.YtDlp, ["--cookies", CookieFile, "--flat-playlist", "-J", "--playlist-end", n.ToString(), "--no-warnings",
                "--extractor-args", "youtubetab:approximate_date", .. pages ? ["--write-pages"] : Array.Empty<string>(), feed], 300_000, pages ? dir : null);
            return (j, pages ? string.Concat(Directory.GetFiles(dir, "*.dump").Order().Select(File.ReadAllText)) : "");
        }
        finally
        {
            if (pages) try { Directory.Delete(dir, true); } catch (Exception) { }
            One.Release();
        }
    }

    /// <summary>
    /// What you watched on YouTube lately. The history feed has no dates, so «the last few months» is the
    /// newest 800 entries. Null when YouTube refused (the sign-in went stale).
    /// </summary>
    public static List<Watched>? History()
    {
        if (!SignedIn) return [];
        try
        {
            var (j, pages) = FeedPages(":ythistory", 800);
            var raw = Lockups(pages).DistinctBy(w => w.Id).ToDictionary(w => w.Id);
            // Shorts are no lockups: they come from yt-dlp's list, without channel or progress
            return !j.TryGetProperty("entries", out var es) ? [.. raw.Values] : es.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.Object && S(e, "id") is { Length: 11 })
                .Select(e => (raw.GetValueOrDefault(S(e, "id")!) ?? new Watched(S(e, "id")!, S(e, "title") ?? "", S(e, "channel"), S(e, "channel_id"), null))
                    with { Seconds = e.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : null }).ToList();
        }
        catch (Exception) { return null; }
    }

    const string Text = "\"((?:[^\"\\\\]|\\\\.)*)\"";      // a JSON string, escapes and all
    static readonly Regex LockupId = new("\"contentId\":\"([\\w-]{11})\""), LockupTitle = new("\"lockupMetadataViewModel\":\\{\"title\":\\{\"content\":" + Text),
        Byline = new("\"metadataParts\":\\[\\{\"text\":\\{\"content\":" + Text), ChannelId = new("\"browseId\":\"(UC[\\w-]{22})\""),
        Bar = new("\"startPercent\":(\\d+)"), ShortId = new("\"videoId\":\"([\\w-]{11})\""), ShortTitle = new("\"primaryText\":\\{\"content\":" + Text);

    static string Unescape(string s) { try { return JsonSerializer.Deserialize<string>("\"" + s + "\"") ?? s; } catch (JsonException) { return s; } }
    static string? G(Match m) => m.Success ? Unescape(m.Groups[1].Value) : null;

    /// <summary>Every video on a raw page is a "lockupViewModel": id, title, channel (the byline) with its id, and the red progress bar.</summary>
    static IEnumerable<Watched> Lockups(string pages) =>
        pages.Split("\"lockupViewModel\":{").Skip(1).Where(i => LockupId.IsMatch(i)).Select(i => new Watched(LockupId.Match(i).Groups[1].Value,
            G(LockupTitle.Match(i)) ?? "", G(Byline.Match(i)), G(ChannelId.Match(i)), Bar.Match(i) is { Success: true } p ? int.Parse(p.Groups[1].Value) : null));

    /// <summary>The Shorts shelf on the home page ("shortsLockupViewModel"): YouTube's own Shorts for you.</summary>
    public static IEnumerable<(string Id, string Title)> ShortsIn(string pages) =>
        pages.Split("\"shortsLockupViewModel\":{").Skip(1).Where(i => ShortId.IsMatch(i)).Select(i => (ShortId.Match(i).Groups[1].Value, G(ShortTitle.Match(i)) ?? ""));

    /// <summary>The channels you are subscribed to — the history feed mostly leaves the channel out, this list has them all.</summary>
    public static List<Channel> Subscriptions()
    {
        if (!SignedIn) return [];
        try
        {
            var j = Feed("https://www.youtube.com/feed/channels", 1000);
            return !j.TryGetProperty("entries", out var es) ? [] : es.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.Object && S(e, "id") is { } id && id.StartsWith("UC") && S(e, "title") != null)
                .Select(e => new Channel(S(e, "id")!, S(e, "title")!)).ToList();
        }
        catch (Exception) { return []; }
    }

    static string? S(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
