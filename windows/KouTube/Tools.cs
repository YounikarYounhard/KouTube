// yt-dlp, ffmpeg, ffprobe and deno: found in tools\ next to the exe (release zip) or in the repo's
// tools\ (development). The binaries are never committed.

using System.Diagnostics;
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

    public static (int Code, string Out, string Err) Run(string exe, IEnumerable<string> args, int timeoutMs = 120_000)
    {
        using var p = Process.Start(Start(exe, args))!;
        var err = p.StandardError.ReadToEndAsync();
        var o = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch (Exception) { } }
        return (p.HasExited ? p.ExitCode : -1, o.Result, err.Result);
    }

    public static JsonElement Json(string exe, IEnumerable<string> args, int timeoutMs = 120_000)
    {
        var (code, o, e) = Run(exe, args, timeoutMs);
        if (code != 0 || string.IsNullOrWhiteSpace(o)) throw new IOException(LastLine(e) ?? $"код {code}");
        return JsonDocument.Parse(o).RootElement.Clone();
    }

    public static string? LastLine(string s) => s.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);

    /// <summary>YouTube changes often and old yt-dlp versions stop working: update on start, quietly.</summary>
    public static string UpdateYtDlp() { var (_, o, e) = Run(YtDlp, ["-U"], 120_000); return LastLine(o + "\n" + e) ?? ""; }
}
