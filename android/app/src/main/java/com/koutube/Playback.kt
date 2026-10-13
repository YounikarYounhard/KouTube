package com.koutube

import androidx.media3.common.C
import androidx.media3.common.MediaItem
import androidx.media3.common.MimeTypes
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.exoplayer.DefaultRenderersFactory
import androidx.media3.exoplayer.ExoPlayer
import kotlinx.coroutines.Job
import kotlinx.coroutines.MainScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

/**
 * One player for the whole app, living outside the activity so a rotation never interrupts it.
 * Besides playing it keeps the honest watch record: which 5-second segments really played
 * (seeking to the end does not count), when a video counts as finished, and when to auto-delete.
 */
object Playback {
    private val main = MainScope()

    val player: ExoPlayer by lazy {
        ExoPlayer.Builder(App.instance, DefaultRenderersFactory(App.instance).setEnableDecoderFallback(true))
            .setSeekBackIncrementMs(10_000).setSeekForwardIncrementMs(10_000)
            .setHandleAudioBecomingNoisy(true)
            .build().also {
                it.addListener(listener)
                // debug builds log decoder choice and dropped frames: `adb logcat -s EventLogger`
                if (BuildConfig.DEBUG) it.addAnalyticsListener(androidx.media3.exoplayer.util.EventLogger())
            }
    }

    val queue = MutableStateFlow<List<Video>>(emptyList())
    val current = MutableStateFlow<Video?>(null)
    val error = MutableStateFlow<String?>(null)
    val resumedAt = MutableStateFlow<Long?>(null)

    // Sleep mode: nothing played while it is on counts as watched or gets deleted.
    /** In the small picture-in-picture window; and "open fullscreen" when it is expanded again. */
    val pip = MutableStateFlow(false)
    val expandFull = MutableStateFlow(false)
    /** Swiped down: the video keeps playing in a bar above the tabs, like YouTube's miniplayer. */
    val minimized = MutableStateFlow(false)
    /** Set when the small window is tapped: the page then grows out of its corner instead of fading in. */
    @Volatile var fromMini = false

    val sleepEndsAt = MutableStateFlow<Long?>(null)     // wall clock; Long.MAX_VALUE = "at the end of this video"
    val dimmed = MutableStateFlow(false)
    private var sleepJob: Job? = null
    val sleeping get() = sleepEndsAt.value != null

    private var cov: Coverage? = null
    private var lastPos = -1L
    private var ticks = 0
    private var ticker: Job? = null
    private var lastFlush = 0L
    private val finished = HashSet<String>()
    private var retries = 0
    /** True while something plays — heavy background work (cutting cover frames) waits for it. */
    val busy get() = current.value != null
    private val seen = HashMap<String, Long>()        // last position seen per video, for when the player already moved on
    private var heldSpeed: Float? = null

    /** Press-and-hold: 2x while the finger is down, then back to whatever speed was set. */
    fun hold2x(on: Boolean) {
        if (on) { heldSpeed = player.playbackParameters.speed; player.setPlaybackSpeed(2f) }
        else { heldSpeed?.let { player.setPlaybackSpeed(it) }; heldSpeed = null }
    }

    fun setSubtitles(on: Boolean) {
        player.trackSelectionParameters = player.trackSelectionParameters.buildUpon().setTrackTypeDisabled(C.TRACK_TYPE_TEXT, !on).build()
    }

    fun play(list: List<Video>, start: Video) {
        val q = list.filter { it.present }.ifEmpty { listOf(start) }
        val i = q.indexOfFirst { it.key == start.key }.coerceAtLeast(0)
        error.value = null
        minimized.value = false
        queue.value = q
        becomeCurrent(q[i])
        player.setMediaItems(q.map(::item), i, resumePos(q[i]))
        player.prepare()
        player.play()
        ticker?.cancel()
        ticker = main.launch { while (isActive) { delay(1000); tick() } }
    }

    fun close() {
        current.value?.let { save(it, player.currentPosition) }
        current.value = null                 // first, so the playlist-cleared event below saves nothing
        ticker?.cancel()
        stopSleep()
        player.stop()
        player.clearMediaItems()
        queue.value = emptyList()
        Lib.flushAll()
    }

    fun restart() { player.seekTo(0); resumedAt.value = null }

    private fun item(v: Video) = MediaItem.Builder().setUri(Lib.uriOf(v)).setMediaId(v.key)
        .setSubtitleConfigurations(subtitles(v).mapNotNull { (s, uri) ->
            uri?.let {
                MediaItem.SubtitleConfiguration.Builder(it)
                    .setMimeType(if (s.path.endsWith(".srt", true)) MimeTypes.APPLICATION_SUBRIP else MimeTypes.TEXT_VTT)
                    .setLanguage(s.lang).setLabel(s.lang + if (s.auto) " (авто)" else "").build()
            }
        }).build()

    private fun subtitles(v: Video): List<Pair<Sub, android.net.Uri?>> =
        if (Lib.isClient) v.meta.subs.orEmpty().map { it to runCatching { Remote.subUri(v, it.lang) }.getOrNull() }
        else v.found?.subs.orEmpty().map { (s, d) -> s to Lib.uriOf(v.storageId, d.id) }

    private fun resumePos(v: Video): Long {
        val w = v.watch
        val dur = v.meta.durationMs ?: Long.MAX_VALUE
        return if (w.status == Watch.STARTED && w.posMs > 5_000 && w.posMs < dur - 10_000) w.posMs.also { resumedAt.value = it }
               else 0L.also { resumedAt.value = null }
    }

    private fun latest(v: Video) = Lib.videos.value.firstOrNull { it.key == v.key && it.storageId == v.storageId } ?: v

    private fun becomeCurrent(v: Video) {
        current.value = v
        cov = Coverage.decode(v.watch.coverage, v.meta.durationMs ?: 0)
        lastPos = -1
    }

    private fun duration(v: Video) = player.duration.takeIf { it != C.TIME_UNSET && it > 0 } ?: v.meta.durationMs ?: 0

    private fun tick() {
        val v = current.value ?: return
        if (player.currentMediaItem?.mediaId == v.key) seen[v.key] = player.currentPosition
        if (!player.isPlaying) return
        val pos = player.currentPosition
        val c = cov ?: return
        // Only continuous playback marks segments: a jump means a seek, not watching.
        if (!sleeping && lastPos >= 0 && pos >= lastPos && pos - lastPos <= 3000 * player.playbackParameters.speed + 1000) {
            for (s in lastPos / SEGMENT_MS..pos / SEGMENT_MS) c.mark(s.toInt())
        }
        lastPos = pos
        if (++ticks % 5 == 0) save()
        if (System.currentTimeMillis() - lastFlush > 120_000) flush()
        checkFinish(v, pos, ended = false)
    }

    private fun save(v: Video? = current.value, pos: Long = player.currentPosition) {
        v ?: return
        val c = cov ?: return
        val now = System.currentTimeMillis()
        Lib.setWatch(v) {
            if (it.status == Watch.FINISHED || v.key in finished) it.copy(posMs = pos, coverage = c.encode(), lastWatchedAt = now)
            else it.copy(status = Watch.STARTED, posMs = pos, coverage = c.encode(), lastWatchedAt = now)
        }
    }

    private fun flush() { lastFlush = System.currentTimeMillis(); Lib.flushAll() }

    private fun checkFinish(v: Video, pos: Long, ended: Boolean) {
        if (sleeping || v.key in finished || v.finished) return
        val dur = duration(v).takeIf { it > 0 } ?: return
        val c = cov ?: return
        val s = Lib.settings.value
        if (c.count() * 100 < Coverage.segments(dur) * s.finishPercent) return
        if (!ended && pos < dur * 95 / 100) return
        finished += v.key
        val now = System.currentTimeMillis()
        Lib.setWatch(v) { it.copy(status = Watch.FINISHED, posMs = 0, coverage = c.encode(), finishedAt = now, lastWatchedAt = now) }
        Lib.setFlags(v) { it.copy(noAutoRedownload = true) }
        flush()
    }

    private val listener = object : Player.Listener {
        override fun onMediaItemTransition(mediaItem: MediaItem?, reason: Int) {
            val prev = current.value
            if (prev != null && reason == Player.MEDIA_ITEM_TRANSITION_REASON_AUTO) {
                // the previous video played to its end: count the last few seconds the ticker missed
                val dur = prev.meta.durationMs ?: 0
                if (!sleeping && lastPos >= 0 && dur - lastPos < 4000) for (s in lastPos / SEGMENT_MS..(dur - 1) / SEGMENT_MS) cov?.mark(s.toInt())
                checkFinish(prev, dur, ended = true)
                save(prev, if (prev.key in finished) 0 else dur)
            } else if (prev != null) save(prev, seen[prev.key] ?: prev.watch.posMs)   // the player is already at the next one
            val next = queue.value.firstOrNull { it.key == mediaItem?.mediaId } ?: return
            becomeCurrent(latest(next))
            error.value = null
            if (reason == Player.MEDIA_ITEM_TRANSITION_REASON_AUTO || reason == Player.MEDIA_ITEM_TRANSITION_REASON_SEEK) {
                val p = resumePos(current.value!!)
                if (p > 0) player.seekTo(p)
            }
        }

        override fun onPlayWhenReadyChanged(playWhenReady: Boolean, reason: Int) {
            if (!playWhenReady && reason == Player.PLAY_WHEN_READY_CHANGE_REASON_END_OF_MEDIA_ITEM && sleepEndsAt.value == Long.MAX_VALUE) stopSleep()
        }

        override fun onPositionDiscontinuity(old: Player.PositionInfo, new: Player.PositionInfo, reason: Int) {
            if (reason == Player.DISCONTINUITY_REASON_SEEK) lastPos = -1
        }

        override fun onIsPlayingChanged(isPlaying: Boolean) {
            if (!isPlaying && current.value != null) { save(); if (System.currentTimeMillis() - lastFlush > 10_000) flush() }
        }

        override fun onPlaybackStateChanged(state: Int) {
            if (state == Player.STATE_READY) retries = 0
            val v = current.value ?: return
            if (state == Player.STATE_ENDED) {
                val dur = duration(v)
                if (!sleeping && lastPos >= 0 && dur - lastPos < 4000) for (s in lastPos / SEGMENT_MS..(dur - 1) / SEGMENT_MS) cov?.mark(s.toInt())
                checkFinish(v, dur, ended = true)
                save(v, if (v.key in finished) 0 else dur)
                if (sleepEndsAt.value == Long.MAX_VALUE) stopSleep()
            }
        }

        override fun onPlayerError(e: PlaybackException) {
            val v = current.value
            // Software decoders are shared by the whole phone; a busy moment makes one fail to start.
            // Try again quietly before bothering anybody.
            val transient = e.errorCode == PlaybackException.ERROR_CODE_DECODER_INIT_FAILED || e.errorCode == PlaybackException.ERROR_CODE_DECODING_FAILED
            if (transient && retries < 2) {
                retries++
                main.launch { delay(800L * retries); player.prepare(); player.play() }
                return
            }
            error.value = when (e.errorCode) {
                PlaybackException.ERROR_CODE_DECODING_FORMAT_UNSUPPORTED, PlaybackException.ERROR_CODE_DECODING_FORMAT_EXCEEDS_CAPABILITIES,
                PlaybackException.ERROR_CODE_DECODER_QUERY_FAILED ->
                    "Это устройство не может воспроизвести формат ${v?.meta?.vcodec?.uppercase() ?: "этого видео"}. " +
                        "Скачайте ролик в совместимом формате (VP9 или H.264) через KouTube на компьютере."
                PlaybackException.ERROR_CODE_DECODER_INIT_FAILED, PlaybackException.ERROR_CODE_DECODING_FAILED ->
                    "Телефон не смог запустить декодер — так бывает, когда он занят. Нажмите «Повторить»."
                PlaybackException.ERROR_CODE_IO_NETWORK_CONNECTION_FAILED, PlaybackException.ERROR_CODE_IO_NETWORK_CONNECTION_TIMEOUT,
                PlaybackException.ERROR_CODE_IO_BAD_HTTP_STATUS ->
                    "Пропала связь с телефоном-сервером. Проверьте Wi-Fi — просмотр продолжится с того же места."
                PlaybackException.ERROR_CODE_IO_FILE_NOT_FOUND, PlaybackException.ERROR_CODE_IO_NO_PERMISSION,
                PlaybackException.ERROR_CODE_IO_UNSPECIFIED, PlaybackException.ERROR_CODE_IO_READ_POSITION_OUT_OF_RANGE ->
                    "Файл недоступен. Флешка извлечена или файл удалён."
                else -> "Не удалось воспроизвести: ${e.errorCodeName}"
            }
            if (e.errorCode in PlaybackException.ERROR_CODE_IO_UNSPECIFIED..PlaybackException.ERROR_CODE_IO_READ_POSITION_OUT_OF_RANGE) Lib.refresh(force = true)
        }
    }

    // ---------- sleep ----------

    /** [minutes] null = stop when the current video ends. */
    fun startSleep(minutes: Int?) {
        stopSleep()
        if (minutes == null) {
            sleepEndsAt.value = Long.MAX_VALUE
            player.pauseAtEndOfMediaItems = true
            return
        }
        val end = System.currentTimeMillis() + minutes * 60_000L
        sleepEndsAt.value = end
        sleepJob = main.launch {
            delay(end - System.currentTimeMillis() - 30_000)
            for (i in 30 downTo 1) { player.volume = i / 30f; delay(1000) }   // fade out over 30 s
            player.pause()
            player.volume = 1f
            sleepEndsAt.value = null
            dimmed.value = false
            flush()
        }
    }

    fun stopSleep() {
        sleepJob?.cancel()
        sleepJob = null
        player.volume = 1f
        player.pauseAtEndOfMediaItems = false
        sleepEndsAt.value = null
        dimmed.value = false
    }
}
