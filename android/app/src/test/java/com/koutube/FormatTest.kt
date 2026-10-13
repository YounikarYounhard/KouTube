package com.koutube

import kotlinx.serialization.Serializable
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

// Runs the shared fixtures in spec/fixtures — the Windows tests read the same files.
class FormatTest {
    private val dir = File("../../spec/fixtures")

    @Serializable data class KeyGen(val size: Int, val mod: Int)
    @Serializable data class KeyCase(val name: String, val gen: KeyGen, val key: String)
    @Serializable data class KeyCases(val cases: List<KeyCase>)
    @Serializable data class MergeCase(val name: String, val devices: List<DeviceFile>, val expected: Map<String, Item>)
    @Serializable data class MergeCases(val cases: List<MergeCase>)

    @Test fun fileKey() {
        for (c in json.decodeFromString<KeyCases>(File(dir, "filekey.json").readText()).cases) {
            val b = ByteArray(c.gen.size) { (it % c.gen.mod).toByte() }
            val head = if (b.size < 2 * KEY_CHUNK) b else b.copyOfRange(0, KEY_CHUNK)
            val tail = b.copyOfRange(maxOf(0, b.size - KEY_CHUNK), b.size)
            assertEquals(c.name, c.key, fileKey(b.size.toLong(), head, tail))
        }
    }

    @Test fun envelope() {
        val ok = unseal(File(dir, "envelope-ok.json").readText())
        assertTrue(ok!!.contains("кириллица ✓"))
        assertNull(unseal(File(dir, "envelope-bad.json").readText()))
        assertNull(unseal(File(dir, "envelope-truncated.json").readText()))
        assertEquals("{\"a\":1}", unseal(seal("{\"a\":1}")))
    }

    @Test fun merge() {
        for (c in json.decodeFromString<MergeCases>(File(dir, "merge.json").readText()).cases)
            assertEquals(c.name, c.expected, merge(c.devices))
    }

    @Test fun coverage() {
        val c = Coverage(60_000)          // 12 segments
        c.mark(0); c.mark(11); c.mark(11); c.mark(20)
        assertEquals(3, c.count())
        assertEquals(3, Coverage.decode(c.encode(), 60_000).count())
    }

    @Test fun names() {
        assertEquals("Utopia Show" to "ЧТО БУДЕТ ПОСЛЕ?", parseName("Utopia Show - ЧТО БУДЕТ ПОСЛЕ？.webm"))
        assertEquals(null to "просто файл", parseName("просто файл.mp4"))
        assertTrue(isVideoName("a.WEBM")); assertTrue(!isVideoName("a.webm.part")); assertTrue(!isVideoName(".x.mp4"))
        assertEquals("short", detectKind("auto", null, 720, 1280, 50_000))
        assertEquals("video", detectKind("auto", null, 720, 1280, 600_000))
        assertEquals("short", detectKind("auto", "https://youtube.com/shorts/x", 1280, 720, null))
    }
}
