package com.koutube

// The shared storage format (spec/FORMAT.md). Pure Kotlin on purpose: the JVM unit tests run it
// against the same fixtures the Windows app is tested with.

import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import java.security.MessageDigest
import kotlin.io.encoding.Base64
import kotlin.io.encoding.ExperimentalEncodingApi

@Serializable data class Sub(val lang: String, val path: String, val auto: Boolean = false)

@Serializable data class Meta(
    val ts: Long = 0, val ytId: String? = null, val title: String? = null, val channel: String? = null,
    val url: String? = null, val published: String? = null, val category: String? = null,
    val durationMs: Long? = null, val kind: String? = null, val width: Int? = null, val height: Int? = null,
    val fps: Double? = null, val vcodec: String? = null, val acodec: String? = null, val size: Long? = null,
    val path: String? = null, val subs: List<Sub>? = null, val thumb: String? = null, val addedAt: Long? = null,
    val source: String? = null, val sponsorCutMs: Long? = null, val avatar: String? = null,
    val comments: String? = null, val commentCount: Long? = null,
    /** YouTube's tags for the video (the PC copies them when it downloads) — search finds a topic by them too. */
    val tags: List<String>? = null,
)

/** One comment as the PC stores it in .koutube/comments: author, text, likes, time, pinned, by the channel. */
@Serializable data class Comment(val a: String = "", val t: String = "", val l: Long = 0, val ts: Long? = null, val p: Boolean = false, val u: Boolean = false)

@Serializable data class Watch(
    val ts: Long = 0, val status: String = NEW, val posMs: Long = 0, val coverage: String? = null,
    val lastWatchedAt: Long? = null, val finishedAt: Long? = null,
) { companion object { const val NEW = "new"; const val STARTED = "started"; const val FINISHED = "finished" } }

@Serializable data class FileState(val ts: Long = 0, val state: String = PRESENT, val at: Long? = null, val reason: String? = null) {
    companion object { const val PRESENT = "present"; const val AFTER_WATCH = "deletedAfterWatch"; const val BY_USER = "deletedByUser" }
}

/** rating: 1 = 👍, -1 = 👎, 0 = none — what recommendations learn from. */
@Serializable data class Flags(val ts: Long = 0, val keep: Boolean = false, val noAutoRedownload: Boolean = false, val redownload: Boolean = false, val rating: Int = 0)

@Serializable data class Item(val meta: Meta? = null, val watch: Watch? = null, val file: FileState? = null, val flags: Flags? = null)

@Serializable data class Decoder(val mime: String, val hw: Boolean, val maxW: Int, val maxH: Int)
@Serializable data class Caps(val video: List<Decoder> = emptyList())

@Serializable data class DeviceFile(
    val schema: Int = 1, val deviceId: String, val deviceName: String? = null, val kind: String? = null,
    val app: String? = null, val writtenAt: Long = 0, val caps: Caps? = null, val items: Map<String, Item> = emptyMap(),
)

@Serializable data class Folder(val path: String, val role: String = "auto")
@Serializable data class Budget(val totalBytes: Long? = null, val reserveBytes: Long = 0, val shortsShare: Double = 0.2)
@Serializable data class StorageFile(
    val schema: Int = 1, val storageId: String, val name: String? = null, val updatedAt: Long = 0,
    val folders: List<Folder> = emptyList(), val downloadTo: Map<String, String> = emptyMap(), val budget: Budget = Budget(),
)

const val SCHEMA = 1

val json = Json { ignoreUnknownKeys = true; explicitNulls = false; encodeDefaults = true }

fun sha256(b: ByteArray): ByteArray = MessageDigest.getInstance("SHA-256").digest(b)
fun ByteArray.hex() = joinToString("") { "%02x".format(it) }

// Envelope (§2): {"sha256":"<64 hex>","body":<json>} — the hash covers the body bytes exactly as written.
private const val PREFIX = "{\"sha256\":\""
private const val MID = "\",\"body\":"
private const val BODY_AT = 11 + 64 + 9

fun seal(body: String) = PREFIX + sha256(body.toByteArray()).hex() + MID + body + "}"

/** The body of a sealed file, or null if it is torn, corrupted or not sealed at all. */
fun unseal(text: String): String? {
    val t = text.removePrefix("﻿")
    if (t.length <= BODY_AT || !t.startsWith(PREFIX) || t.substring(75, BODY_AT) != MID) return null
    val end = t.lastIndexOf('}')
    if (end < BODY_AT) return null
    val body = t.substring(BODY_AT, end)
    return body.takeIf { sha256(it.toByteArray()).hex() == t.substring(11, 75) }
}

// fileKey (§5.1): size + sha256 of the first and last 16 KiB.
const val KEY_CHUNK = 16384

/** [head] is the first min(size, 16 KiB) bytes, or the whole file if it is under 32 KiB; [tail] is the last 16 KiB (ignored for small files). */
fun fileKey(size: Long, head: ByteArray, tail: ByteArray): String {
    val data = if (size < 2 * KEY_CHUNK) head else head + tail
    return "f1-$size-" + sha256(data).hex().take(16)
}

// Merge (§6): union by key, newest ts per group, ties go to the larger deviceId.
fun merge(files: List<DeviceFile>): MutableMap<String, Item> {
    val out = HashMap<String, Item>()
    for (f in files.sortedBy { it.deviceId }) for ((k, b) in f.items) {
        val a = out[k] ?: Item()
        out[k] = Item(
            newer(a.meta, b.meta) { it.ts }, newer(a.watch, b.watch) { it.ts },
            newer(a.file, b.file) { it.ts }, newer(a.flags, b.flags) { it.ts },
        )
    }
    return out
}

private inline fun <T> newer(a: T?, b: T?, ts: (T) -> Long): T? = when {
    a == null -> b
    b == null -> a
    ts(b) >= ts(a) -> b
    else -> a
}

/** Timestamp for a group being rewritten on top of [merged] (writer rule, §6.4). */
fun nextTs(merged: Long?, now: Long = System.currentTimeMillis()) = maxOf(now, (merged ?: 0) + 1)

// Coverage (§5.2): one bit per 5-second segment actually played, LSB of byte 0 = segment 0.
const val SEGMENT_MS = 5000L

@OptIn(ExperimentalEncodingApi::class)
class Coverage(private var bits: ByteArray) {
    constructor(durationMs: Long) : this(ByteArray(((segments(durationMs) + 7) / 8).toInt()))

    fun mark(segment: Int) {
        if (segment < 0) return
        if (segment / 8 >= bits.size) bits = bits.copyOf(segment / 8 + 1)
        bits[segment / 8] = (bits[segment / 8].toInt() or (1 shl (segment % 8))).toByte()
    }

    fun count() = bits.sumOf { Integer.bitCount(it.toInt() and 0xff) }
    fun encode(): String = Base64.encode(bits)

    companion object {
        fun segments(durationMs: Long) = (durationMs + SEGMENT_MS - 1) / SEGMENT_MS
        fun decode(s: String?, durationMs: Long) = s?.let { runCatching { Coverage(Base64.decode(it)) }.getOrNull() } ?: Coverage(durationMs)
    }
}

// Scanner helpers (§8).
val VIDEO_EXT = setOf("mp4", "m4v", "mov", "webm", "mkv", "3gp", "ts", "flv")
private val SKIP_EXT = setOf("part", "tmp", "ytdl", "ktpart")

fun isVideoName(name: String): Boolean {
    if (name.startsWith(".")) return false
    val ext = name.substringAfterLast('.', "").lowercase()
    return ext in VIDEO_EXT && ext !in SKIP_EXT
}

private val FULLWIDTH = mapOf('？' to '?', '：' to ':', '｜' to '|', '＊' to '*', '＂' to '"', '＜' to '<', '＞' to '>', '⧸' to '/', '＼' to '\\')

fun unFullwidth(s: String) = buildString(s.length) { s.forEach { append(FULLWIDTH[it] ?: it) } }

/** "Канал - Название.webm" → (Канал, Название); the template YTDLnis and yt-dlp use. */
fun parseName(fileName: String): Pair<String?, String> {
    val base = unFullwidth(fileName.substringBeforeLast('.'))
    val i = base.indexOf(" - ")
    return if (i > 0) base.substring(0, i) to base.substring(i + 3) else null to base
}

fun detectKind(role: String, url: String?, width: Int?, height: Int?, durationMs: Long?): String = when {
    role == "video" || role == "short" -> role
    url?.contains("/shorts/") == true -> "short"
    width != null && height != null && height > width && (durationMs ?: Long.MAX_VALUE) <= 180_000 -> "short"
    else -> "video"
}
