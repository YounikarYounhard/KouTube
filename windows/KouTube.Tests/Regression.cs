using KouTube;

static class Regression
{
    public static void Run(Action<bool, string> check)
    {
        check(Downloader.IsSponsorFailure("Postprocessing: ffmpeg failed", true), "cut failure permits original download");
        check(!Downloader.IsSponsorFailure("HTTP Error 403", false), "network error is not an ad-cut failure");
        check(!Downloader.IsSponsorFailure("Postprocessing: merger failed", false), "unrelated merge error is not an ad-cut failure");
        Pick P(string id, string kind, double sec = 60, string? title = null) => new(id, "u", title ?? id, "Channel", sec, null, kind, 2, "", "test");
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
        var item = new Item { Meta = new Meta { Path = "old.mp4", Size = 4, AddedAt = Format.Now() - 40L * 86400_000 } };
        storage.Mine[key] = item; storage.Remerge();
        var found = new Found(file, "old.mp4", 4, DateTime.UtcNow, [], null);
        storage.Docs[key] = found;
        var video = new Video(key, storage, item, found);
        check(Lib.DeleteWatched(storage, [video]).Count == 0 && File.Exists(file), "watched cleanup cannot remove an unwatched file");
        check(Lib.DeleteWatched(storage, [video], replaceStale: true).Count == 1 && !File.Exists(file), "confirmed stale cleanup removes only selected file");
        check(storage.Merged[key].File?.State == FileState.ByUser && storage.Merged[key].Flags?.NoAutoRedownload == true,
            "replacement preserves history and blocks redownload");
    }
}
