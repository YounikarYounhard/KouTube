using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KouTube;

public static class FallbackDownload
{
    static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        Timeout = TimeSpan.FromSeconds(60),
        MaxResponseContentBufferSize = 128 * 1024
    };

    public static Uri? Endpoint(string? input) => Uri.TryCreate(input, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
        ? uri : null;

    public static async Task<string> Get(QueueItem item, string dir, QRow row, CancellationToken ct)
    {
        var endpoint = Endpoint(Lib.Config.FallbackApi) ?? throw new IOException("Укажите адрес резервного сервера в настройках.");
        if (!Regex.IsMatch(item.Id, @"\A[A-Za-z0-9_-]{11}\z")) throw new IOException("У ролика нет допустимого идентификатора YouTube.");
        var height = Downloader.ProfileForDevices().MaxShortSide;
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new {
                url = "https://www.youtube.com/watch?v=" + item.Id,
                videoQuality = height.ToString(System.Globalization.CultureInfo.InvariantCulture),
                alwaysProxy = true, localProcessing = "disabled",
                youtubeVideoCodec = "h264", youtubeVideoContainer = "mp4"
            })
        };
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await Http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new IOException($"Резервный сервер ответил ошибкой {(int)response.StatusCode}.");
        using var result = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        var json = result.RootElement;
        if (!json.TryGetProperty("status", out var status) || status.GetString() != "tunnel" ||
            !json.TryGetProperty("url", out var value) || !Uri.TryCreate(value.GetString(), UriKind.Absolute, out var url) ||
            !IsTunnel(endpoint, url)) throw new IOException("Резервный сервер не вернул подходящий поток видео.");

        using var download = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        download.EnsureSuccessStatusCode();
        var path = Path.Combine(dir, item.Id + ".mp4");
        var free = new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace - (512L << 20);
        var max = Math.Min(free, 8L << 30);
        var length = download.Content.Headers.ContentLength;
        if (max <= 0 || length > max) throw new IOException("Для резервной загрузки не хватает места во временной папке.");
        await using var input = await download.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
        var bytes = new byte[1 << 17];
        long done = 0;
        while (true)
        {
            using var read = CancellationTokenSource.CreateLinkedTokenSource(ct);
            read.CancelAfter(TimeSpan.FromSeconds(45));
            var n = await input.ReadAsync(bytes, read.Token);
            if (n == 0) break;
            done += n;
            if (done > max) throw new IOException("Резервная загрузка превышает доступный объём.");
            await output.WriteAsync(bytes.AsMemory(0, n), ct);
            if (length is > 0) row.SetProgress(done / (double)length * 0.9, 0, 0, "резервный сервер");
        }
        return path;
    }

    public static bool IsTunnel(Uri api, Uri file) => file.Scheme == Uri.UriSchemeHttps &&
        file.UserInfo.Length == 0 && api.IdnHost == file.IdnHost && api.Port == file.Port;
}
