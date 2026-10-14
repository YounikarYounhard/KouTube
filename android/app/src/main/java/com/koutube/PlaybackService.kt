package com.koutube

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Intent
import android.content.pm.ServiceInfo
import android.graphics.drawable.Icon
import android.media.MediaMetadata
import android.media.session.MediaSession
import android.media.session.PlaybackState
import android.os.Build
import androidx.media3.common.Player

/**
 * The player in the notification and on the lock screen: title, ⏮ ⏯ ⏭. While something plays the
 * service is in the foreground, so the sound goes on in another app or with the screen off. Plain
 * Android MediaSession — no extra library.
 */
class PlaybackService : Service() {
    private lateinit var session: MediaSession
    private val follow = object : Player.Listener {
        override fun onEvents(player: Player, events: Player.Events) = update()
    }

    override fun onBind(intent: Intent?) = null

    override fun onCreate() {
        super.onCreate()
        if (Build.VERSION.SDK_INT >= 26) getSystemService(NotificationManager::class.java)
            .createNotificationChannel(NotificationChannel(CHANNEL, "Плеер", NotificationManager.IMPORTANCE_LOW))
        session = MediaSession(this, "KouTube").apply {
            setCallback(object : MediaSession.Callback() {
                override fun onPlay() = Playback.player.play()
                override fun onPause() = Playback.player.pause()
                override fun onSkipToNext() = Playback.player.seekToNext()
                override fun onSkipToPrevious() = Playback.player.seekToPrevious()
                override fun onSeekTo(pos: Long) = Playback.player.seekTo(pos)
                override fun onStop() = Playback.close()
            })
            setSessionActivity(open())
            isActive = true
        }
        Playback.player.addListener(follow)
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val p = Playback.player
        when (intent?.action) {
            PLAY -> if (p.isPlaying) p.pause() else p.play()
            PREV -> p.seekToPrevious()
            NEXT -> p.seekToNext()
        }
        update()
        return START_NOT_STICKY
    }

    private fun open() = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java),
        PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)

    private fun button(icon: Int, title: String, action: String) = Notification.Action.Builder(Icon.createWithResource(this, icon), title,
        PendingIntent.getService(this, action.hashCode(), Intent(this, PlaybackService::class.java).setAction(action), PendingIntent.FLAG_IMMUTABLE)).build()

    private fun update() {
        val v = Playback.current.value ?: return stopSelf()
        val p = Playback.player
        session.setMetadata(MediaMetadata.Builder().putString(MediaMetadata.METADATA_KEY_TITLE, v.title)
            .putString(MediaMetadata.METADATA_KEY_ARTIST, v.meta.channel).putLong(MediaMetadata.METADATA_KEY_DURATION, p.duration.coerceAtLeast(0)).build())
        session.setPlaybackState(PlaybackState.Builder()
            .setActions(PlaybackState.ACTION_PLAY or PlaybackState.ACTION_PAUSE or PlaybackState.ACTION_PLAY_PAUSE or PlaybackState.ACTION_STOP or
                PlaybackState.ACTION_SKIP_TO_NEXT or PlaybackState.ACTION_SKIP_TO_PREVIOUS or PlaybackState.ACTION_SEEK_TO)
            .setState(if (p.isPlaying) PlaybackState.STATE_PLAYING else PlaybackState.STATE_PAUSED, p.currentPosition, if (p.isPlaying) p.playbackParameters.speed else 0f)
            .build())
        val n = (if (Build.VERSION.SDK_INT >= 26) Notification.Builder(this, CHANNEL) else @Suppress("DEPRECATION") Notification.Builder(this))
            .setSmallIcon(android.R.drawable.ic_media_play)
            .setContentTitle(v.title).setContentText(v.meta.channel).setContentIntent(open())
            .setVisibility(Notification.VISIBILITY_PUBLIC).setShowWhen(false)
            .addAction(button(android.R.drawable.ic_media_previous, "Назад", PREV))
            .addAction(if (p.isPlaying) button(android.R.drawable.ic_media_pause, "Пауза", PLAY) else button(android.R.drawable.ic_media_play, "Играть", PLAY))
            .addAction(button(android.R.drawable.ic_media_next, "Дальше", NEXT))
            .setStyle(Notification.MediaStyle().setMediaSession(session.sessionToken).setShowActionsInCompactView(0, 1, 2))
            .build()
        // playing: foreground, Android leaves it alone; paused: an ordinary notification you can swipe away
        val fg = p.isPlaying && runCatching {
            if (Build.VERSION.SDK_INT >= 29) startForeground(ID, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PLAYBACK) else startForeground(ID, n)
        }.isSuccess
        if (!fg) {
            if (Build.VERSION.SDK_INT >= 24) stopForeground(STOP_FOREGROUND_DETACH) else @Suppress("DEPRECATION") stopForeground(false)
            getSystemService(NotificationManager::class.java).notify(ID, n)
        }
    }

    override fun onDestroy() {
        Playback.player.removeListener(follow)
        session.release()
        getSystemService(NotificationManager::class.java).cancel(ID)
        super.onDestroy()
    }

    companion object {
        const val CHANNEL = "player"
        const val ID = 2                    // 1 is the Wi-Fi sharing notification
        const val PLAY = "com.koutube.PLAYER_PLAY"
        const val PREV = "com.koutube.PLAYER_PREV"
        const val NEXT = "com.koutube.PLAYER_NEXT"
    }
}
