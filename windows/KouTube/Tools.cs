// yt-dlp, ffmpeg, ffprobe and deno: found in tools\ next to the exe or in the repo's tools\ (development).
// The release is a single KouTube.exe — it fetches them on its first start. The binaries are never committed.

using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace KouTube;

public static class Tools
{
    public static readonly string Dir = Find();

    static string Find()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "tools", "yt-dlp.exe"))) return Path.Combine(d.FullName, "tools");
        return Path.Combine(AppContext.BaseDirectory, "tools");
    }

    public static string YtDlp => Path.Combine(Dir, "yt-dlp.exe");
    public static string FfProbe => Path.Combine(Dir, "ffprobe.exe");
    public static string FfMpeg => Path.Combine(Dir, "ffmpeg.exe");
    public static string Missing() => string.Join(", ", new[] { "yt-dlp.exe", "ffmpeg.exe", "ffprobe.exe", "deno.exe" }
        .Where(f => !File.Exists(Path.Combine(Dir, f))));

    /// <summary>First start of the bare exe: yt-dlp, ffmpeg + ffprobe and deno (~200 MB, once) from their own GitHub releases.</summary>
    public static async Task Fetch(Action<string> status)
    {
        Directory.CreateDirectory(Dir);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        async Task<string> Get(string url, string name)
        {
            var part = Path.Combine(Dir, name + ".part");
            using var r = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            r.EnsureSuccessStatusCode();
            var len = r.Content.Headers.ContentLength;
            await using var src = await r.Content.ReadAsStreamAsync();
            await using var dst = File.Create(part);
            var buf = new byte[1 << 20];
            long got = 0;
            for (int n; (n = await src.ReadAsync(buf)) > 0;)
            {
                await dst.WriteAsync(buf.AsMemory(0, n));
                if ((got >> 20) != ((got += n) >> 20)) status($"Первый запуск: скачиваю {name} — {got >> 20} МБ" + (len is > 0 ? $" из {len >> 20}" : ""));
            }
            return part;
        }
        void Unzip(string zip, params string[] names)
        {
            using (var z = ZipFile.OpenRead(zip))
                foreach (var e in z.Entries.Where(e => names.Contains(e.Name))) e.ExtractToFile(Path.Combine(Dir, e.Name), true);
            File.Delete(zip);
        }
        if (!File.Exists(YtDlp)) File.Move(await Get("https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe", "yt-dlp"), YtDlp, true);
        if (!File.Exists(FfMpeg) || !File.Exists(FfProbe))
            Unzip(await Get("https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip", "ffmpeg"), "ffmpeg.exe", "ffprobe.exe");
        if (!File.Exists(Path.Combine(Dir, "deno.exe")))
            Unzip(await Get("https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip", "deno"), "deno.exe");
    }

    public static ProcessStartInfo Start(string exe, IEnumerable<string> args)
    {
        var p = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        // into a pipe yt-dlp writes in the ANSI code page (cp1251) — Cyrillic file names would arrive broken
        if (exe == YtDlp) { p.ArgumentList.Add("--encoding"); p.ArgumentList.Add("utf-8"); }
        foreach (var a in args) p.ArgumentList.Add(a);
        p.Environment["PATH"] = Dir + ";" + Environment.GetEnvironmentVariable("PATH");   // yt-dlp finds ffmpeg and deno here
        p.Environment["PYTHONIOENCODING"] = "utf-8";
        return p;
    }

    public static (int Code, string Out, string Err) Run(string exe, IEnumerable<string> args, int timeoutMs = 120_000, string? cwd = null)
    {
        var si = Start(exe, args);
        if (cwd != null) si.WorkingDirectory = cwd;
        using var p = Process.Start(si)!;
        var err = p.StandardError.ReadToEndAsync();
        var o = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch (Exception) { } }
        return (p.HasExited ? p.ExitCode : -1, o.Result, err.Result);
    }

    public static JsonElement Json(string exe, IEnumerable<string> args, int timeoutMs = 120_000, string? cwd = null)
    {
        var (code, o, e) = Run(exe, args, timeoutMs, cwd);
        // a yt-dlp list whose later page fails (YouTube answers 500 on hashtag pages) still prints what it got: keep it
        if (code != 0 && !(exe == YtDlp && o.TrimStart().StartsWith('{')) || string.IsNullOrWhiteSpace(o)) throw new IOException(LastLine(e) ?? $"код {code}");
        return JsonDocument.Parse(o).RootElement.Clone();
    }

    public static string? LastLine(string s) => s.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);

    /// <summary>YouTube changes often and old yt-dlp versions stop working: update on start, quietly.</summary>
    public static string UpdateYtDlp() { var (_, o, e) = Run(YtDlp, ["-U"], 120_000); return LastLine(o + "\n" + e) ?? ""; }
}
