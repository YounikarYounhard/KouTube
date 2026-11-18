using KouTube;

static class Regression
{
    public static void Run(Action<bool, string> check)
    {
        check(Downloader.IsSponsorFailure("Postprocessing: ffmpeg failed", true), "cut failure permits original download");
        check(!Downloader.IsSponsorFailure("HTTP Error 403", false), "network error is not an ad-cut failure");
        check(!Downloader.IsSponsorFailure("Postprocessing: merger failed", false), "unrelated merge error is not an ad-cut failure");
        Pick P(string id, string kind, double sec = 60, string? title = null) => new(id, "u", title ?? id, "Channel", sec, null, kind, 2, "", "test");
        var tiny = P("tiny", "video", 120);
        var medium = P("medium", "video", 20 * 60);
        var durationPick = new Recommend.Selection([tiny, medium], medium.Estimate, 0, duration: "medium").Fill();
        check(durationPick.Videos.Single().Id == medium.Id, "the medium preference fills space with medium videos before small ones");
        check(Downloader.Passing("Sign in to confirm you’re not a bot") && Downloader.Passing("HTTP Error 429") &&
            !Downloader.Passing("video unavailable") && !Downloader.Passing("403 seconds"),
            "temporary extraction errors retry, removed videos and incidental numbers do not");
        var api = FallbackDownload.Endpoint("https://example.org:8443/api/")!;
        check(FallbackDownload.Endpoint("http://example.org") == null &&
            FallbackDownload.Endpoint("https://user:pass@example.org") == null, "fallback endpoint requires HTTPS without credentials");
        check(FallbackDownload.IsTunnel(api, new Uri("https://example.org:8443/tunnel?id=1")) &&
            !FallbackDownload.IsTunnel(api, new Uri("https://example.net:8443/tunnel")) &&
            !FallbackDownload.IsTunnel(api, new Uri("https://example.org/tunnel")), "fallback file stays on the configured server");
        check(Downloader.YoutubePlaylistId("https://music.youtube.com/playlist?list=PLabc_123") == "yt-PLabc_123" &&
            Downloader.YoutubePlaylistId("https://example.org/playlist?list=PLabc_123") == null &&
            Downloader.YoutubePlaylistId("https://www.youtube.com/watch?v=jNQXAC9IVRw&list=PLabc_123") == null,
            "playlist imports require an explicit YouTube playlist URL");
        check(Downloader.Passing("Requested format is not available") &&
            Downloader.CompatibleFormat(true, 720).Contains("[width<=720]"), "format can retry using a bounded compatible preset");
        var pool = Enumerable.Range(0, 50).Select(i => P("v" + i, "video", 60 + i)).Concat(Enumerable.Range(0, 100).Select(i => P("s" + i, "short", 20))).ToList();
        foreach (var split in new[] { 0, 25, 100 })
        {
            const long budget = 100L << 20;
            var selection = new Recommend.Selection(pool, budget * (100 - split) / 100, budget * split / 100, fillAll: true).Fill();
            for (var i = 0; i < 10; i++)
            {
                check(selection.Used(false) + selection.Used(true) <= budget, "refill stays inside total budget");
                check(split != 0 || selection.Shorts.Count == 0, "zero Shorts means no Shorts after refill");
                check(split != 100 || selection.Videos.Count == 0, "zero videos means no videos after refill");
                var victim = selection.Videos.Concat(selection.Shorts).FirstOrDefault();
                if (victim != null) selection.Remove(victim, dislike: true);
            }
        }
        var episodes = new[] { 4, 2, 1 }.Select(i => P("ep" + i, "video", title: "Экспедиция серия " + i)).ToList();
        var series = new Recommend.Selection(episodes, 1L << 30, 0).Fill();
        check(series.Videos.Select(p => p.Id).SequenceEqual(new[] { "ep1", "ep2" }), "series start at 1, ordered, stop at a gap");
        series.Remove(series.Videos[0]);
        check(series.Videos.Count == 0, "removing start also removes dependent episodes");
        check(Recommend.Diversify(episodes, []).Count == 3, "episodes are not collapsed as duplicate titles");
        check(PickIntent.Series("Новости за 2026 год", "Channel") == null, "a year is not an episode");
        check(PickIntent.Topics("Мне для мамы надо видео про кухню, путешествия и сад и т.д.").SequenceEqual(new[] { "кухню", "путешествия", "сад" }), "request splits into editable topics");
        var mount = new Mount(new StorageRef("test", "test", "test"));
        var old = new Video("x", mount, new Item { Meta = new Meta { AddedAt = Format.Now() - 40L * 86400_000 } }, new Found("x", "x", 1, DateTime.UtcNow, [], null));
        var reset = new Config { Keywords = ["space"], Linked = ["x"], NotInterested = [new Nope("x", "title", "channel")], Seen = ["seen"], Exclude = ["phonk"] }.ResetRecommendations(100);
        check(reset.Keywords.Count + reset.Linked.Count + reset.NotInterested.Count == 0 && reset.RecommendationsResetAt == 100, "reset clears learned preferences and search topics");
        check(reset.Seen.SequenceEqual(new[] { "seen" }) && reset.Exclude.SequenceEqual(new[] { "phonk" }), "reset retains watched exclusions and blocked words");
        var training = old with { Item = old.Item with { Meta = old.Meta with { AddedAt = 1 }, Watch = new Watch { Ts = 50, Status = Watch.Finished }, Flags = new Flags { Ts = 50, Rating = 1 } } };
        check(Recommend.TrainingVideos([training], 100).Count == 0 && training.Watch.Status == Watch.Finished, "reset ignores old training without deleting history");
        var recent = training with { Item = training.Item with { Watch = training.Watch with { Ts = 101 } } };
        var learned = Recommend.TrainingVideos([recent], 100).Single();
        check(learned.Watch.Status == Watch.Finished && learned.Flags.Rating == 0, "new watch trains again without reviving old rating");
        check(Lib.StaleUnwatched(old, Format.Now()), "old never-watched file can be proposed for replacement");
        check(!Lib.StaleUnwatched(old with { Item = old.Item with { Flags = new Flags { Keep = true } } }, Format.Now()), "protected file never proposed");
        check(!Lib.StaleUnwatched(old with { Item = old.Item with { Watch = new Watch { LastWatchedAt = 1 } } }, Format.Now()), "any watch excludes replacement");
        check(!Lib.StaleUnwatched(old with { Item = old.Item with { Flags = new Flags { Rating = 1 } } }, Format.Now()), "liked file never proposed");
        mount.Others.Add(new DeviceFile { DeviceId = "phone", Items = new() { ["x"] = new Item { Watch = new Watch { PosMs = 1000 } } } });
        check(!Lib.StaleUnwatched(old, Format.Now()), "watch on any synchronized device protects a file");
        mount.Mine["a"] = new Item { Meta = new Meta { Title = "original", Ts = 1 } };
        mount.Mine["b"] = new Item { Meta = new Meta { Title = "other", Ts = 1 } };
        mount.Others.Add(new DeviceFile { DeviceId = "other", Items = new() { ["a"] = new Item { Watch = new Watch { Ts = 4, Status = Watch.Finished } } } });
        mount.Remerge();
        Lib.Edit(mount, "a", it => it with { Flags = new Flags { Ts = 5, Rating = 1 } });
        check(mount.Merged["a"].Watch?.Status == Watch.Finished && mount.Merged["b"].Meta?.Title == "other", "single-key merge preserves other devices and videos");
        var dir = Directory.CreateDirectory(Path.Combine(Lib.LocalDir, "replacement-check")).FullName;
        var storage = Lib.Add(dir, [new Folder(".")]);
        var file = Path.Combine(dir, "old.mp4");
        File.WriteAllBytes(file, [1, 2, 3, 4]);
        var key = Format.FileKey(file);
        var item = new Item { Meta = new Meta { YtId = "playlist-video", Path = "old.mp4", Size = 4, AddedAt = Format.Now() - 40L * 86400_000 } };
        storage.Mine[key] = item; storage.Remerge();
        var found = new Found(file, "old.mp4", 4, DateTime.UtcNow, [], null);
        storage.Docs[key] = found;
        var video = new Video(key, storage, item, found);
        check(Lib.DeleteWatched(storage, [video]).Count == 0 && File.Exists(file), "watched cleanup cannot remove an unwatched file");
        storage.Others.Add(new DeviceFile { DeviceId = "phone2", Items = new() {
            [key] = new Item { Playlists = new() { ["music"] = new PlaylistEntry(10, "Музыка") } } } });
        storage.Remerge(key);
        check(Lib.DeleteWatched(storage, [video], replaceStale: true).Count == 0 && File.Exists(file),
            "even a stale cleanup selection cannot remove a file protected by a playlist on another device");
        check(Lib.DeleteWatched(storage, [video], replaceStale: true, includePlaylistVideos: true).Count == 1 && !File.Exists(file),
            "explicitly allowed playlist file can be deleted when individually selected");
        check(Lib.InPlaylist(new Video(key, storage, storage.Merged[key], null)), "deleted file keeps its place in the playlist for redownload");
        Lib.RestorePlaylistEntries(storage, "new-key", "playlist-video", null);
        check(storage.Merged["new-key"].Playlists?["music"].Name == "Музыка" && storage.Merged[key].Playlists?["music"].Name == null,
            "redownloading with a new fingerprint moves the playlist entry without a duplicate");
        check(storage.Merged[key].File?.State == FileState.ByUser && storage.Merged[key].Flags?.NoAutoRedownload == true,
            "replacement preserves history and blocks redownload");
    }
}
