// Picks the video+audio formats for the "720p economy" profile from yt-dlp's format list.
// We choose the format ids ourselves (instead of a yt-dlp sort string) so the size shown before
// downloading is exactly what gets downloaded.

using System.Text.Json;

namespace KouTube;

public record YtFormat(string Id, string? Vcodec, string? Acodec, int? Width, int? Height, double? Fps,
    long? Filesize, long? FilesizeApprox, double? Tbr, string? Ext, string? FormatNote, int? LanguagePreference,
    string? DynamicRange, string? Protocol, string? Language = null)
{
    public bool IsVideo => Vcodec is not (null or "none") && Acodec is null or "none";
    public bool IsAudio => Acodec is not (null or "none") && Vcodec is null or "none";
    public int ShortSide => Math.Min(Width ?? 0, Height ?? 0);
    public string Family => FormatChoice.Family(Vcodec);

    /// <summary>Bytes for a video of [sec] seconds; approx when YouTube did not say the exact size.</summary>
    public (long Bytes, bool Approx) Size(double sec) =>
        Filesize is { } f ? (f, false) : FilesizeApprox is { } a ? (a, true) : ((long)((Tbr ?? 0) * 125 * sec), true);

    public static YtFormat From(JsonElement e)
    {
        string? S(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        double? D(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
        return new(S("format_id") ?? "", S("vcodec"), S("acodec"), (int?)D("width"), (int?)D("height"), D("fps"),
            (long?)D("filesize"), (long?)D("filesize_approx"), D("tbr"), S("ext"), S("format_note"), (int?)D("language_preference"),
            S("dynamic_range"), S("protocol"), S("language"));
    }
}

public record Profile
{
    public int MaxShortSide { get; init; } = 720;
    /// <summary>Allowed codec families, best first. Default follows the user's YTDLnis habit: AV1 when available.</summary>
    public List<string> Codecs { get; init; } = ["av01", "vp09", "avc1"];
    public int MaxFps { get; init; } = 60;
    /// <summary>
    /// Codecs some phone or tablet can only decode in software. Fine at 25–30 fps, but a Redmi Note 7
    /// drops ~12 of every 60 frames of AV1 720p60 — so above 30 fps those codecs are skipped.
    /// </summary>
    public List<string> SlowCodecs { get; init; } = [];
    /// <summary>Audio language to prefer when YouTube has several tracks (MrBeast's Russian dub); null = the system's language.</summary>
    public string? AudioLang { get; init; }
}

/// <summary>[Dub] = the audio is a translation; the video also has a track marked as its original.</summary>
public record Choice(YtFormat Video, YtFormat Audio, long Bytes, bool Approx, bool Dub = false)
{
    public string Spec => $"{Video.Id}+{Audio.Id}";
    public string Label => $"{Video.ShortSide}p{(Video.Fps >= 50 ? Math.Round(Video.Fps ?? 0).ToString() : "")} {FormatChoice.Pretty(Video.Family)}{DubNote}";
    /// <summary>" · русская озвучка" when the chosen track is a dub, not the original.</summary>
    string DubNote => Dub && Audio.Language is { } l ? $" · {Lang(l)} озвучка" : "";
    static string Lang(string code)
    {
        try { var n = new System.Globalization.CultureInfo(code).NativeName.Split(' ')[0]; return n.EndsWith("ий") ? n[..^2] + "ая" : n; }
        catch (System.Globalization.CultureNotFoundException) { return code; }
    }
    public string Text => $"{Label} · {(Approx ? "≈ " : "")}{Bytes / 1048576.0:0} МБ";
}

public static class FormatChoice
{
    public static string Family(string? vcodec) => vcodec switch
    {
        null => "",
        var c when c.StartsWith("av01") => "av01",
        var c when c.StartsWith("vp09") || c.StartsWith("vp9") => "vp09",
        var c when c.StartsWith("avc1") || c.StartsWith("h264") => "avc1",
        var c when c.StartsWith("hev1") || c.StartsWith("hvc1") => "hevc",
        var c => c.Split('.')[0],
    };

    public static string Pretty(string family) => family switch { "av01" => "AV1", "vp09" => "VP9", "avc1" => "H.264", _ => family.ToUpperInvariant() };

    static bool Usable(YtFormat f) => f.Protocol is null or "https" or "http" && f.DynamicRange is null or "SDR";

    public static string SystemLang => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

    static bool Speaks(YtFormat f, string lang) => f.Language?.StartsWith(lang, StringComparison.OrdinalIgnoreCase) == true;

    public static YtFormat? BestAudio(IEnumerable<YtFormat> fs, string? lang = null) => fs.Where(f => f.IsAudio && Usable(f))
        .OrderByDescending(f => Speaks(f, lang ?? SystemLang))                     // a track in the viewer's language, if YouTube has one
        .ThenByDescending(f => f.LanguagePreference ?? 0)                       // otherwise the original over other dubs
        .ThenByDescending(f => f.FormatNote?.Contains("original", StringComparison.OrdinalIgnoreCase) == true)
        .ThenBy(f => f.Id.Contains("drc", StringComparison.OrdinalIgnoreCase))   // not the loudness-squashed copy
        .ThenByDescending(f => f.Acodec?.StartsWith("opus") == true)              // the collection is Opus
        .ThenByDescending(f => Math.Min(f.Tbr ?? 0, 160))
        .FirstOrDefault();

    /// <summary>
    /// Highest resolution up to the profile's limit (by the SHORT side, so a 720×1280 Short counts as 720p),
    /// then the most efficient allowed codec, then the frame rate the source has.
    /// </summary>
    public static Choice? Pick(IReadOnlyList<YtFormat> fs, Profile p, double sec)
    {
        var audio = BestAudio(fs, p.AudioLang);
        if (audio == null) return null;
        var vids = fs.Where(f => f.IsVideo && Usable(f) && f.ShortSide > 0 && f.ShortSide <= p.MaxShortSide && p.Codecs.Contains(f.Family)
            && (f.Fps ?? 30) <= p.MaxFps && !(p.SlowCodecs.Contains(f.Family) && (f.Fps ?? 30) > 31)).ToList();
        var best = vids.OrderByDescending(f => f.ShortSide)
            .ThenBy(f => p.Codecs.IndexOf(f.Family))
            .ThenByDescending(f => f.Fps ?? 0)
            .ThenBy(f => f.Size(sec).Bytes)
            .FirstOrDefault();
        return best == null ? null : Make(best, audio, sec, IsDub(fs, audio));
    }

    /// <summary>One option per (resolution, codec) for the "compare formats" dialog, smallest first within a resolution.</summary>
    public static List<Choice> Variants(IReadOnlyList<YtFormat> fs, double sec)
    {
        var audio = BestAudio(fs);
        if (audio == null) return [];
        return fs.Where(f => f.IsVideo && Usable(f) && f.ShortSide is >= 360 and <= 1080)
            .GroupBy(f => (f.ShortSide, f.Family))
            .Select(g => g.OrderByDescending(f => f.Fps ?? 0).ThenBy(f => f.Size(sec).Bytes).First())
            .OrderByDescending(f => f.ShortSide).ThenBy(f => f.Size(sec).Bytes)
            .Select(v => Make(v, audio, sec, IsDub(fs, audio))).ToList();
    }

    static Choice Make(YtFormat v, YtFormat a, double sec, bool dub)
    {
        var (vb, va) = v.Size(sec); var (ab, aa) = a.Size(sec);
        return new Choice(v, a, vb + ab, va || aa, dub);
    }

    /// <summary>A Russian channel's only track is not a "dub": only when YouTube marks some other track as the original.</summary>
    static bool IsDub(IEnumerable<YtFormat> fs, YtFormat a) => (a.LanguagePreference ?? 0) <= 0 && fs.Any(f => f.IsAudio && (f.LanguagePreference ?? 0) > 0);

    /// <summary>Self-check: null when the rules hold, else what broke.</summary>
    public static string? Test()
    {
        YtFormat V(string id, string c, int w, int h, double fps, long size) => new(id, c, "none", w, h, fps, size, null, null, "mp4", null, null, "SDR", "https");
        YtFormat A(string id, string c, long size, int? lp = null) => new(id, "none", c, null, null, null, size, null, 128, "webm", null, lp, null, "https");
        // the exact set from the YTDLnis screenshot (MB)
        var fs = new List<YtFormat> {
            V("399", "av01.0.08M.08", 1920, 1080, 60, 179_700_000), V("248", "vp9", 1920, 1080, 60, 233_100_000),
            V("137", "avc1.64002a", 1920, 1080, 60, 381_100_000), V("398", "av01.0.05M.08", 1280, 720, 60, 110_200_000),
            V("247", "vp9", 1280, 720, 60, 142_900_000), V("136", "avc1.64001f", 1280, 720, 60, 214_200_000),
            V("397", "av01.0.04M.08", 854, 480, 30, 60_000_000), A("251", "opus", 9_000_000), A("140", "mp4a.40.2", 8_000_000),
        };
        var p = Pick(fs, new Profile(), 600);
        if (p?.Spec != "398+251") return "default should be 398+251, got " + p?.Spec;
        p = Pick(fs, new Profile { Codecs = ["vp09", "avc1"] }, 600);
        if (p?.Spec != "247+251") return "without AV1 should be 247+251, got " + p?.Spec;
        p = Pick(fs, new Profile { Codecs = ["avc1"] }, 600);
        if (p?.Spec != "136+251") return "H.264 only should be 136+251, got " + p?.Spec;
        // a vertical Short: 720×1280 is "720p", 1080×1920 is over the limit
        var sh = new List<YtFormat> { V("a", "av01.0.05M", 720, 1280, 30, 5_000_000), V("b", "av01.0.08M", 1080, 1920, 30, 9_000_000),
            V("c", "av01.0.04M", 480, 854, 30, 3_000_000), A("251", "opus", 500_000) };
        p = Pick(sh, new Profile(), 40);
        if (p?.Video.Id != "a") return "short should pick 720x1280, got " + p?.Video.Id;
        // a dubbed audio track must lose to the original one
        if (BestAudio([A("251-drc", "opus", 9_000_000), A("251", "opus", 9_000_000)])?.Id != "251") return "DRC audio should lose to the normal one";
        // a phone that decodes AV1 only in software: 60 fps goes VP9, 30 fps stays AV1
        if (Pick(fs, new Profile { SlowCodecs = ["av01"] }, 600)?.Spec != "247+251") return "60 fps with software-only AV1 should be VP9 (247)";
        var fs30 = new List<YtFormat> { V("398", "av01.0.05M.08", 1280, 720, 30, 60_000_000), V("247", "vp9", 1280, 720, 30, 80_000_000), A("251", "opus", 9_000_000) };
        if (Pick(fs30, new Profile { SlowCodecs = ["av01"] }, 600)?.Spec != "398+251") return "30 fps should stay AV1 even when it is software-only";
        var dub = new List<YtFormat>(fs) { A("251-dub", "opus", 9_000_000, lp: -1), A("251-orig", "opus", 9_000_000, lp: 10) };
        if (BestAudio(dub, "de")?.Id != "251-orig") return "original audio should win, got " + BestAudio(dub, "de")?.Id;
        // several tracks with languages: the viewer's language first, else the original
        YtFormat L(string id, string lang, int lp) => A(id, "opus", 9_000_000, lp) with { Language = lang };
        var multi = new List<YtFormat> { L("251-0", "en-US", 10), L("251-1", "ru", -1), L("251-2", "de", -1) };
        if (BestAudio(multi, "ru")?.Id != "251-1") return "a Russian viewer should get the Russian dub, got " + BestAudio(multi, "ru")?.Id;
        if (BestAudio(multi, "fr")?.Id != "251-0") return "no French track: the original, got " + BestAudio(multi, "fr")?.Id;
        var v720 = V("398", "av01.0.05M.08", 1280, 720, 30, 60_000_000);
        if (Pick([v720, .. multi], new Profile { AudioLang = "ru" }, 600) is not { Dub: true } || Pick([v720, L("251", "ru", -1)], new Profile { AudioLang = "ru" }, 600) is not { Dub: false })
            return "a dub is a dub only next to a marked original";
        if (Variants(fs, 600).Count != 7) return "variants should list 7 (res,codec) options, got " + Variants(fs, 600).Count;
        return null;
    }
}
