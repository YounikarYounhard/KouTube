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
    public record Watched(string Id, string Title, string? Channel, string? ChannelId, int? Percent);
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

    /// <summary>One of the account's lists (":ythistory", ":ytrec", ":ytsubs", ":ytwatchlater") as yt-dlp's flat JSON.</summary>
    public static JsonElement Feed(string feed, int n, string? pagesTo = null)
    {
        One.Wait();
        try
        {
            return Tools.Json(Tools.YtDlp, ["--cookies", CookieFile, "--flat-playlist", "-J", "--playlist-end", n.ToString(), "--no-warnings",
                "--extractor-args", "youtubetab:approximate_date", .. pagesTo != null ? ["--write-pages"] : Array.Empty<string>(), feed], 300_000, pagesTo);
        }
        finally { One.Release(); }
    }

    /// <summary>
    /// What you watched on YouTube lately. The history feed has no dates, so «the last few months» is the
    /// newest 400 entries. Null when YouTube refused (the sign-in went stale).
    /// </summary>
    public static List<Watched>? History()
    {
        if (!SignedIn) return [];
        // yt-dlp's list leaves out how much you watched; the raw pages it saves (--write-pages) have it
        var pages = Path.Combine(Dir, "pages");
        try
        {
            Directory.CreateDirectory(pages);
            var j = Feed(":ythistory", 400, pages);
            var pct = Progress(pages);
            return !j.TryGetProperty("entries", out var es) ? [] : es.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.Object && S(e, "id") is { Length: 11 })
                .Select(e => new Watched(S(e, "id")!, S(e, "title") ?? "", S(e, "channel") ?? S(e, "uploader"), S(e, "channel_id"),
                    pct.TryGetValue(S(e, "id")!, out var p) ? p : null)).ToList();
        }
        catch (Exception) { return null; }
        finally { try { Directory.Delete(pages, true); } catch (Exception) { } }
    }

    /// <summary>Video id → percent watched, from the history pages: each video is a "lockupViewModel" with a progress bar.</summary>
    static Dictionary<string, int> Progress(string dir)
    {
        var d = new Dictionary<string, int>();
        foreach (var f in Directory.GetFiles(dir, "*.dump"))
            foreach (var item in File.ReadAllText(f).Split("\"lockupViewModel\"").Skip(1))
                if (Regex.Match(item, "\"contentId\":\"([\\w-]{11})\"") is { Success: true } id && Regex.Match(item, "\"startPercent\":(\\d+)") is { Success: true } p)
                    d.TryAdd(id.Groups[1].Value, int.Parse(p.Groups[1].Value));
        return d;
    }

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
