package com.koutube

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.ImageFormat
import android.graphics.Rect
import android.graphics.YuvImage
import android.media.Image
import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaExtractor
import android.media.MediaFormat
import java.io.ByteArrayOutputStream
import android.media.MediaMetadataRetriever
import android.net.Uri
import android.os.Build
import android.util.LruCache
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.async
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import java.io.File
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL
import java.util.Collections

/**
 * Covers: YouTube's own picture when there is internet, else a picture next to the video, else a kept
 * cover in .koutube/thumbs, else a frame from the video itself. Each is made once and kept in the app
 * cache — never written to the stick, so browsing costs no USB writes.
 */
object Thumbs {
    private const val SIZE = 480
    private val mem = object : LruCache<String, ImageBitmap>(48 shl 20) { override fun sizeOf(k: String, b: ImageBitmap) = b.width * b.height * 4 }
    private val failed = Collections.synchronizedSet(HashSet<String>())
    private val gen = Dispatchers.IO.limitedParallelism(2)
    private val dir by lazy { File(App.instance.cacheDir, "thumbs").apply { mkdirs() } }

    fun fileFor(v: Video): File? = File(dir, "${v.key}.yt.jpg").takeIf { it.exists() } ?: File(dir, "${v.key}.jpg").takeIf { it.exists() }

    /** The channel picture the PC put into .koutube/avatars, or null (then a letter is drawn). */
    suspend fun avatar(video: Video): ImageBitmap? {
        // a video the PC hasn't given the picture yet borrows it from another video of the same channel
        val v = if (video.meta.avatar != null) video
                else Lib.videos.value.firstOrNull { it.meta.avatar != null && it.meta.channel != null && it.meta.channel == video.meta.channel } ?: return null
        val rel = v.meta.avatar ?: return null
        val key = "a:" + v.storageId + rel
        mem.get(key)?.let { return it }
        if (key in failed) return null
        return withContext(gen) {
            val f = File(dir, "av-" + rel.substringAfterLast('/'))
            val bmp = if (f.exists()) BitmapFactory.decodeFile(f.path) else (
                if (Lib.isClient) runCatching {
                    val (st, b) = Remote.get(Remote.avatarUrl(v)); if (st == 200) BitmapFactory.decodeByteArray(b, 0, b.size) else null
                }.getOrNull()
                else Lib.storageFile(v.storageId, rel)?.let(::decode)
            )?.also { b -> f.outputStream().use { b.compress(Bitmap.CompressFormat.JPEG, 85, it) } }
            if (bmp == null) { failed += key; null } else bmp.asImageBitmap().also { mem.put(key, it) }
        }
    }

    suspend fun get(v: Video): ImageBitmap? {
        mem.get(v.key)?.let { return it }
        if (v.key in failed) return null
        return withContext(gen) {
            val yt = File(dir, "${v.key}.yt.jpg")
            val f = File(dir, "${v.key}.jpg")
            // a cover already here shows at once (no internet in an outage must not mean blank cards);
            // YouTube's own one replaces it in the background when there is internet
            val local = if (yt.exists()) null else if (f.exists()) BitmapFactory.decodeFile(f.path) else cheap(v)?.also { save(it, f) }
            val bmp = when {
                yt.exists() -> BitmapFactory.decodeFile(yt.path)
                local != null -> local.also { upgrade(v) }
                else -> youtubeSoon(v)?.also { save(it, yt) } ?: run {
                    // cutting a frame needs a decoder; while a video plays it would compete for one
                    while (Playback.busy) delay(1500)
                    make(v)?.also { save(it, f) }
                }
            }
            if (bmp == null) { if (v.present) failed += v.key; null }
            else bmp.asImageBitmap().also { mem.put(v.key, it) }
        }
    }

    /** Bumped when a better cover arrives, so the cards on screen pick it up. */
    val version = MutableStateFlow(0)
    private val upgrades = CoroutineScope(SupervisorJob() + Dispatchers.IO.limitedParallelism(1))
    private val upgrading = Collections.synchronizedSet(HashSet<String>())

    private fun save(b: Bitmap, f: File) = f.outputStream().use { b.compress(Bitmap.CompressFormat.JPEG, 85, it) }

    private fun upgrade(v: Video) {
        if (v.meta.ytId == null || !upgrading.add(v.key)) return
        upgrades.launch {
            // a tablet without internet still gets YouTube's cover if the phone it watches from has one (wider than our 480-px frames)
            val b = youtubeSoon(v) ?: (if (Lib.isClient) runCatching {
                val (st, bytes) = Remote.get(Remote.thumbUrl(v))
                if (st == 200) BitmapFactory.decodeByteArray(bytes, 0, bytes.size)?.takeIf { it.width > SIZE } else null
            }.getOrNull() else null) ?: return@launch
            save(b, File(dir, "${v.key}.yt.jpg"))
            mem.put(v.key, b.asImageBitmap())
            version.value++
        }
    }

    /**
     * At most 5 s: a blocked network can hang in DNS far longer than any connect timeout, and a thread stuck
     * there ignores interrupts — so the request runs on its own and we simply stop waiting for it.
     */
    private suspend fun youtubeSoon(v: Video): Bitmap? {
        if (v.meta.ytId == null || System.currentTimeMillis() < offlineUntil) return null
        val req = net.async { youtube(v) }
        val b = withTimeoutOrNull(5000) { req.await() }
        if (b == null && req.isActive) offlineUntil = System.currentTimeMillis() + 600_000
        return b
    }
    private val net = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    @Volatile private var offlineUntil = 0L
    private val noCover = Collections.synchronizedSet(HashSet<String>())

    /**
     * The cover people know from YouTube, by the video's ID: 1280×720 for videos, the vertical one for
     * Shorts (their 16:9 picture is a crop). One failed connection = offline; try again in 10 minutes.
     */
    private fun youtube(v: Video): Bitmap? {
        val id = v.meta.ytId ?: return null
        if (id in noCover || System.currentTimeMillis() < offlineUntil) return null
        for (name in if (v.isShort) listOf("oardefault", "oar2") else listOf("hq720", "mqdefault")) {
            val c = URL("https://i.ytimg.com/vi/$id/$name.jpg").openConnection() as HttpURLConnection
            try {
                c.connectTimeout = 4000; c.readTimeout = 8000
                if (c.responseCode != 200) continue
                val b = c.inputStream.use { it.readBytes() }
                BitmapFactory.decodeByteArray(b, 0, b.size)?.let { return shrink(it, 640) }
            } catch (e: IOException) { offlineUntil = System.currentTimeMillis() + 600_000; return null }
            finally { c.disconnect() }
        }
        noCover += id
        return null
    }

    /** Covers that need no decoding: from the server, a picture next to the file, or one the PC saved. */
    private fun cheap(v: Video): Bitmap? {
        if (Lib.isClient) return runCatching {
            val (st, b) = Remote.get(Remote.thumbUrl(v))
            if (st == 200) BitmapFactory.decodeByteArray(b, 0, b.size)?.let(::shrink) else null
        }.getOrNull()
        v.found?.thumb?.let { t -> Lib.uriOf(v.storageId, t.id)?.let(::decode)?.let { return it } }
        return Lib.storageThumb(v)?.let(::decode)
    }

    private fun make(v: Video): Bitmap? {
        if (Lib.isClient) return null
        val uri = Lib.uriOf(v) ?: return null
        val r = MediaMetadataRetriever()
        return try {
            r.setDataSource(App.instance, uri)
            val dur = r.extractMetadata(MediaMetadataRetriever.METADATA_KEY_DURATION)?.toLongOrNull() ?: 0
            val at = minOf(dur / 5, 60_000) * 1000
            val frame = if (Build.VERSION.SDK_INT >= 27) r.getScaledFrameAtTime(at, MediaMetadataRetriever.OPTION_CLOSEST_SYNC, SIZE, SIZE)
                        else r.getFrameAtTime(at, MediaMetadataRetriever.OPTION_CLOSEST_SYNC)
            frame?.let(::shrink)
        } catch (e: Exception) { null } finally { r.release() } ?: codecFrame(uri)
    }

    /**
     * The platform's frame grabber gives up after a few milliseconds per frame — far too little for a
     * software AV1 decoder on a phone without AV1 hardware (Redmi Note 7: "FrameDecoder err -11").
     * So decode the frame ourselves and simply wait for it.
     */
    private fun codecFrame(uri: Uri): Bitmap? {
        val ex = MediaExtractor()
        var codec: MediaCodec? = null
        try {
            ex.setDataSource(App.instance, uri, null)
            val track = (0 until ex.trackCount).firstOrNull { ex.getTrackFormat(it).getString(MediaFormat.KEY_MIME)?.startsWith("video/") == true } ?: return null
            val fmt = ex.getTrackFormat(track)
            ex.selectTrack(track)
            val dur = if (fmt.containsKey(MediaFormat.KEY_DURATION)) fmt.getLong(MediaFormat.KEY_DURATION) else 0L
            ex.seekTo(minOf(dur / 5, 60_000_000L), MediaExtractor.SEEK_TO_PREVIOUS_SYNC)
            fmt.setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatYUV420Flexible)
            // Android 10's AV1 decoder rejects WebM's av1C box as codec data; the stream carries the same header itself
            if (fmt.getString(MediaFormat.KEY_MIME) == "video/av01" && Build.VERSION.SDK_INT >= 29) fmt.removeKey("csd-0")
            codec = MediaCodec.createDecoderByType(fmt.getString(MediaFormat.KEY_MIME)!!).apply { configure(fmt, null, null, 0); start() }
            val info = MediaCodec.BufferInfo()
            val deadline = System.currentTimeMillis() + 15_000
            var inputDone = false
            while (System.currentTimeMillis() < deadline) {
                if (!inputDone) codec.dequeueInputBuffer(10_000).takeIf { it >= 0 }?.let { i ->
                    val n = ex.readSampleData(codec.getInputBuffer(i)!!, 0)
                    if (n < 0) { codec.queueInputBuffer(i, 0, 0, 0, MediaCodec.BUFFER_FLAG_END_OF_STREAM); inputDone = true }
                    else { codec.queueInputBuffer(i, 0, n, ex.sampleTime, 0); ex.advance() }
                }
                val o = codec.dequeueOutputBuffer(info, 10_000)
                if (o < 0) continue
                val bmp = codec.getOutputImage(o)?.use(::toBitmap)
                codec.releaseOutputBuffer(o, false)
                if (bmp != null) return shrink(bmp)
                if (info.flags and MediaCodec.BUFFER_FLAG_END_OF_STREAM != 0) break
            }
            return null
        } catch (e: Exception) { return null } finally {
            runCatching { codec?.stop() }; runCatching { codec?.release() }; ex.release()
        }
    }

    /** YUV_420_888 → NV21 → JPEG → Bitmap: the stock route, good enough for a 480 px cover. */
    private fun toBitmap(img: Image): Bitmap? {
        val crop = img.cropRect
        val w = crop.width(); val h = crop.height()
        val nv21 = ByteArray(w * h * 3 / 2)
        val y = img.planes[0]
        for (row in 0 until h) {
            val buf = y.buffer
            buf.position((row + crop.top) * y.rowStride + crop.left * y.pixelStride)
            if (y.pixelStride == 1) buf.get(nv21, row * w, w)
            else for (col in 0 until w) nv21[row * w + col] = buf.get((row + crop.top) * y.rowStride + (col + crop.left) * y.pixelStride)
        }
        val u = img.planes[1]; val v = img.planes[2]
        var k = w * h
        for (row in 0 until h / 2) for (col in 0 until w / 2) {
            val ui = (row + crop.top / 2) * u.rowStride + (col + crop.left / 2) * u.pixelStride
            val vi = (row + crop.top / 2) * v.rowStride + (col + crop.left / 2) * v.pixelStride
            nv21[k++] = v.buffer.get(vi); nv21[k++] = u.buffer.get(ui)
        }
        val out = ByteArrayOutputStream()
        YuvImage(nv21, ImageFormat.NV21, w, h, null).compressToJpeg(Rect(0, 0, w, h), 85, out)
        val b = out.toByteArray()
        return BitmapFactory.decodeByteArray(b, 0, b.size, BitmapFactory.Options().apply { inSampleSize = if (w >= 1280) 2 else 1 })
    }

    private fun decode(uri: Uri): Bitmap? = runCatching {
        val cr = App.instance.contentResolver
        val o = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        cr.openInputStream(uri)?.use { BitmapFactory.decodeStream(it, null, o) }
        var s = 1
        while (maxOf(o.outWidth, o.outHeight) / (s * 2) >= SIZE) s *= 2
        cr.openInputStream(uri)?.use { BitmapFactory.decodeStream(it, null, BitmapFactory.Options().apply { inSampleSize = s }) }?.let(::shrink)
    }.getOrNull()

    private fun shrink(b: Bitmap, size: Int = SIZE): Bitmap {
        val k = size.toFloat() / maxOf(b.width, b.height)
        return if (k >= 1f) b else Bitmap.createScaledBitmap(b, (b.width * k).toInt(), (b.height * k).toInt(), true)
    }
}
