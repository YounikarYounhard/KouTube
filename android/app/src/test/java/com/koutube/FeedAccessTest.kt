package com.koutube

import org.junit.Assert.*
import org.junit.Test

class FeedAccessTest {
    @Test fun failedRetryCannotReplaceNewerProgress() {
        val newer = RemoteEdit("s", "key", watch = Watch(ts = 20, posMs = 120_000), flags = Flags(ts = 3, rating = 1))
        val retry = RemoteEdit("s", "key", watch = Watch(ts = 10, posMs = 30_000))
        assertEquals(120_000L, mergeRemoteEdit(newer, retry).watch?.posMs)
        assertEquals(1, mergeRemoteEdit(newer, retry).flags?.rating)
        assertEquals(120_000L, mergeRemoteEdit(retry, newer).watch?.posMs)
    }

    @Test fun startedVideosReceiveAModestBoost() {
        val all = (1..100).map { i -> Video("$i", "s", Item(meta = Meta(title = "$i")), null, remotePresent = true) }
        val started = all.map { if (it.key == "50") it.copy(item = it.item.copy(watch = Watch(status = Watch.STARTED, posMs = 10_000))) else it }
        assertTrue(localFeed(started, 4).indexOfFirst { it.key == "50" } <= localFeed(all, 4).indexOfFirst { it.key == "50" })
    }
    @Test fun folderBoundaries() {
        val grants = listOf(ShareFolder("s", "Videos"))
        assertTrue(canReadFolder(grants, "s", "Videos/a.mp4"))
        assertTrue(canReadFolder(grants, "s", "Videos/sub/a.mp4"))
        assertFalse(canReadFolder(grants, "s", "Videos2/a.mp4"))
        assertFalse(canReadFolder(grants, "other", "Videos/a.mp4"))
        assertFalse(canReadFolder(grants, "s", "Videos/../private/a.mp4"))
        assertFalse(canReadFolder(emptyList(), "s", "Videos/a.mp4"))
        assertFalse(canReadFolder(listOf(ShareFolder("s", ".")), "s", "/private/a.mp4"))
    }

    @Test fun recommendationsExcludeFinishedAndVaryBetweenVisits() {
        val all = (1..100).map { i -> Video("$i", "s", Item(meta = Meta(title = "Видео $i", channel = "Канал ${i % 8}"),
            watch = Watch(status = if (i % 4 == 0) Watch.FINISHED else Watch.NEW)), null, remotePresent = true) }
        val a = localFeed(all, 100)
        assertEquals(75, a.size)
        assertFalse(a.any { it.finished })
        assertEquals(a, localFeed(all, 100))
        assertNotEquals(a, localFeed(all, 200))
        assertFalse(shortsFeed(all.map { it.copy(item = it.item.copy(meta = it.meta.copy(kind = "short"))) }, null).any { it.finished })
        assertTrue(searchScore(all[0], "Видео 1") > searchScore(all[10], "Видео 1"))
    }
}
