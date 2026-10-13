package com.koutube

import android.app.PendingIntent
import android.app.PictureInPictureParams
import android.app.RemoteAction
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageManager
import android.graphics.drawable.Icon
import android.os.Build
import android.os.Bundle
import android.util.Rational
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.Surface
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.core.content.ContextCompat
import androidx.lifecycle.Lifecycle
import androidx.media3.common.MediaItem
import androidx.media3.common.Player
import androidx.media3.common.VideoSize

class MainActivity : ComponentActivity() {

    // ---------- picture-in-picture: leave the app while a video plays → a small window, like YouTube ----------

    private val hasPip get() = Build.VERSION.SDK_INT >= 26 && packageManager.hasSystemFeature(PackageManager.FEATURE_PICTURE_IN_PICTURE)

    private val pipButtons = object : BroadcastReceiver() {
        override fun onReceive(c: Context, i: Intent) {
            val p = Playback.player
            when (i.action) {
                PLAY -> if (p.isPlaying) p.pause() else p.play()
                PREV -> p.seekToPreviousMediaItem()
                NEXT -> p.seekToNextMediaItem()
            }
        }
    }

    private val pipFollow = object : Player.Listener {
        override fun onIsPlayingChanged(isPlaying: Boolean) = updatePip()
        override fun onVideoSizeChanged(videoSize: VideoSize) = updatePip()
        override fun onMediaItemTransition(mediaItem: MediaItem?, reason: Int) = updatePip()
    }

    private fun action(a: String, icon: Int, title: String) = RemoteAction(Icon.createWithResource(this, icon), title, title,
        PendingIntent.getBroadcast(this, a.hashCode(), Intent(a).setPackage(packageName), PendingIntent.FLAG_IMMUTABLE))

    private fun pipParams(): PictureInPictureParams {
        val vs = Playback.player.videoSize
        val r = if (vs.width > 0 && vs.height > 0) vs.width.toFloat() / vs.height else 16 / 9f
        val ratio = when { r > 2.39f -> Rational(239, 100); r < 1 / 2.39f -> Rational(100, 239); vs.width > 0 -> Rational(vs.width, vs.height); else -> Rational(16, 9) }
        val playing = Playback.player.isPlaying
        val b = PictureInPictureParams.Builder().setAspectRatio(ratio).setActions(listOf(
            action(PREV, android.R.drawable.ic_media_previous, "Предыдущее"),
            action(PLAY, if (playing) android.R.drawable.ic_media_pause else android.R.drawable.ic_media_play, if (playing) "Пауза" else "Смотреть"),
            action(NEXT, android.R.drawable.ic_media_next, "Следующее"),
        ))
        // Android 12+: slides into the window by itself — but only while something is actually playing
        if (Build.VERSION.SDK_INT >= 31) b.setAutoEnterEnabled(playing && Playback.current.value != null).setSeamlessResizeEnabled(true)
        return b.build()
    }

    private fun updatePip() { if (hasPip) runCatching { setPictureInPictureParams(pipParams()) } }

    override fun onUserLeaveHint() {
        super.onUserLeaveHint()
        // left the app (Home, another app): coming back asks the mode again — unless a video is open (back to it, or the small window)
        if (Playback.current.value == null) Lib.roleChosen.value = false
        // older Android has no auto-enter: ask for the window ourselves, and only for a playing video (paused = no window)
        if (hasPip && Build.VERSION.SDK_INT < 31 && Playback.current.value != null && Playback.player.isPlaying)
            runCatching { enterPictureInPictureMode(pipParams()) }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        if (Playback.current.value == null) Lib.roleChosen.value = false          // every opening of the app asks the mode
        enableEdgeToEdge()
        if (hasPip) {
            ContextCompat.registerReceiver(this, pipButtons, IntentFilter().apply { addAction(PLAY); addAction(PREV); addAction(NEXT) }, ContextCompat.RECEIVER_NOT_EXPORTED)
            Playback.player.addListener(pipFollow)
            addOnPictureInPictureModeChangedListener { info ->
                Playback.pip.value = info.isInPictureInPictureMode
                if (info.isInPictureInPictureMode) Playback.resumedAt.value = null   // its 6-second timer dies with the player screen
                // tapped "expand" in the small window → back straight into fullscreen
                if (!info.isInPictureInPictureMode && lifecycle.currentState.isAtLeast(Lifecycle.State.STARTED)) { Playback.minimized.value = false; Playback.expandFull.value = true }
            }
        }
        setContent {
            val s by Lib.settings.collectAsState()
            val dark = when (s.theme) { "light" -> false; "dark" -> true; else -> isSystemInDarkTheme() }
            KouTubeTheme(dark) { Surface { Root() } }
        }
    }

    override fun onResume() { super.onResume(); Lib.refresh() }

    // Leaving the app is a checkpoint: whatever changed goes to the stick now. Closing the small window
    // stops the video (like YouTube); with the sleep timer on, sound keeps going with the screen off.
    override fun onStop() {
        super.onStop()
        if (!Playback.sleeping && Playback.current.value != null) Playback.player.pause()
        Lib.flushAll()
    }

    override fun onDestroy() {
        if (hasPip) { runCatching { unregisterReceiver(pipButtons) }; Playback.player.removeListener(pipFollow) }
        super.onDestroy()
    }

    companion object {
        const val PLAY = "com.koutube.PIP_PLAY"
        const val PREV = "com.koutube.PIP_PREV"
        const val NEXT = "com.koutube.PIP_NEXT"
    }
}
