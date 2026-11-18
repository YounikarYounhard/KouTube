package com.koutube

import android.app.Activity
import android.content.pm.ActivityInfo
import android.content.res.Configuration
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectHorizontalDragGestures
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.gestures.detectVerticalDragGestures
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.gestures.calculateZoom
import androidx.compose.ui.draw.clipToBounds
import androidx.compose.foundation.layout.wrapContentHeight
import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.FastOutSlowInEasing
import androidx.compose.animation.core.Spring
import androidx.compose.animation.core.spring
import androidx.compose.animation.core.tween
import kotlinx.coroutines.CoroutineStart
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.GridItemSpan
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items as gridItems
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.pager.VerticalPager
import androidx.compose.foundation.pager.rememberPagerState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.rounded.ArrowBack
import androidx.compose.material.icons.outlined.*
import androidx.compose.material.icons.rounded.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.geometry.Offset
import androidx.compose.animation.core.VectorConverter
import androidx.compose.foundation.gestures.detectDragGestures
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.IntOffset
import kotlin.math.roundToInt
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import androidx.lifecycle.compose.currentStateAsState
import androidx.media3.common.C
import androidx.media3.common.Player
import androidx.media3.common.TrackSelectionOverride
import androidx.media3.common.util.UnstableApi
import androidx.media3.ui.AspectRatioFrameLayout
import androidx.media3.ui.PlayerView
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

private fun brightness(a: Activity, v: Float) { a.window.attributes = a.window.attributes.apply { screenBrightness = v } }

// ---------- what the screen needs from the player, polled while it is visible ----------

data class Pos(val pos: Long = 0, val dur: Long = 0, val buf: Long = 0, val playing: Boolean = false, val buffering: Boolean = false, val speed: Float = 1f)

@Composable
fun rememberPos(): State<Pos> = produceState(Pos()) {
    while (true) {
        val p = Playback.player
        value = Pos(p.currentPosition, p.duration.coerceAtLeast(0), p.bufferedPosition, p.isPlaying,
            p.playbackState == Player.STATE_BUFFERING, p.playbackParameters.speed)
        delay(200)
    }
}

/** Just the picture (and subtitles) — every control is ours, drawn in Compose like YouTube's. */
@androidx.annotation.OptIn(UnstableApi::class)
@Composable
fun VideoSurface(modifier: Modifier) = AndroidView(
    factory = {
        (android.view.LayoutInflater.from(it).inflate(R.layout.player_texture, null) as PlayerView)
            .apply { resizeMode = AspectRatioFrameLayout.RESIZE_MODE_FIT; keepScreenOn = true; player = Playback.player }
    },
    update = { it.player = Playback.player },
    onRelease = { it.player = null },
    modifier = modifier,
)

private fun speedText(s: Float): String {
    val t = if (s * 10 % 1f == 0f) "%.1f".format(java.util.Locale.US, s) else "%.2f".format(java.util.Locale.US, s).trimEnd('0')
    return t.replace('.', ',')
}

// ---------- the screen ----------

@Composable
fun PlayerScreen(all: List<Video>, play: (List<Video>, Video) -> Unit, onClose: () -> Unit) {
    val v by Playback.current.collectAsState()
    val queue by Playback.queue.collectAsState()
    val activity = LocalContext.current as Activity
    val view = LocalView.current
    val cfg = LocalConfiguration.current
    val splitView = cfg.screenWidthDp >= 840 && cfg.orientation == Configuration.ORIENTATION_LANDSCAPE
    val sideWidth = (cfg.screenWidthDp * 0.36f).coerceIn(280f, 420f).dp
    val tablet = cfg.smallestScreenWidthDp >= 600
    var full by rememberSaveable { mutableStateOf(false) }
    val fullscreen = full
    var zoom by remember(v?.key) { mutableStateOf(1f) }
    var locked by remember { mutableStateOf(false) }
    var sheet by remember { mutableStateOf(false) }
    var more by remember { mutableStateOf(false) }
    val cur = v ?: return
    val portraitVideo = (cur.meta.height ?: 0) > (cur.meta.width ?: 1)

    Immersive(fullscreen)
    LaunchedEffect(fullscreen, tablet, portraitVideo) {
        if (!tablet) activity.requestedOrientation = if (fullscreen && !portraitVideo)
            ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE else ActivityInfo.SCREEN_ORIENTATION_SENSOR_PORTRAIT
    }
    val expand by Playback.expandFull.collectAsState()
    LaunchedEffect(expand) {
        if (!expand) return@LaunchedEffect
        Playback.expandFull.value = false
        full = true
    }
    DisposableEffect(Unit) {
        onDispose {
            activity.requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED
            WindowCompat.getInsetsController(activity.window, view).show(WindowInsetsCompat.Type.systemBars())
        }
    }
    val toggleFull: () -> Unit = { full = !full; zoom = 1f }
    val scope = rememberCoroutineScope()
    val density = LocalDensity.current
    val screenH = with(density) { cfg.screenHeightDp.dp.toPx() }
    val screenW = with(density) { cfg.screenWidthDp.dp.toPx() }
    val shrink = remember { Animatable(if (Playback.fromMini) 1f else 0f).also { Playback.fromMini = false } }
    val enter = remember { Animatable(if (shrink.value > 0f) 1f else 0f) }
    LaunchedEffect(Unit) {
        launch { enter.animateTo(1f, tween(220, easing = FastOutSlowInEasing)) }
        shrink.animateTo(0f, tween(300, easing = FastOutSlowInEasing))
    }
    val statusTop = with(density) { WindowInsets.statusBars.asPaddingValues().calculateTopPadding().toPx() }
    val navBottom = with(density) { WindowInsets.navigationBars.asPaddingValues().calculateBottomPadding().toPx() }
    val miniW = with(density) { (if (tablet) 360.dp else cfg.screenWidthDp.dp * 0.5f).toPx() }
    val playerH = statusTop + (if (splitView) screenW - with(density) { sideWidth.toPx() } else screenW) * 9f / 16f
    val miniBottom = screenH - navBottom - with(density) { (8.dp + 49.dp + 46.dp).toPx() }     // where the small window's picture ends
    val minimize: () -> Unit = {
        scope.launch { shrink.animateTo(1f, tween(240, easing = FastOutSlowInEasing)); Playback.minimized.value = true }
    }
    val toggleFullNow by rememberUpdatedState(toggleFull)
    val swipe = Modifier.pointerInput(fullscreen, locked, sheet) {
        if (locked || sheet || fullscreen) return@pointerInput
        var total = 0f
        var fromEdge = false
        detectVerticalDragGestures(onDragStart = { total = 0f; fromEdge = it.y < 32.dp.toPx() }, onDragEnd = {
            val threshold = 96.dp.toPx()
            if (!fromEdge && total > threshold) minimize()
            else {
                if (!fromEdge && total < -threshold) toggleFullNow()
                scope.launch { shrink.animateTo(0f, spring(stiffness = Spring.StiffnessMediumLow)) }
            }
        }, onDragCancel = { scope.launch { shrink.animateTo(0f) } }) { _, dy ->
            if (!fromEdge) {
                total += dy
                scope.launch(start = CoroutineStart.UNDISPATCHED) { shrink.snapTo((total / (screenH * 0.55f)).coerceIn(0f, 1f)) }
            }
        }
    }
    BackHandler {
        when {
            locked -> Unit
            sheet -> sheet = false
            more -> more = false
            fullscreen -> toggleFull()
            else -> minimize()
        }
    }

    val playerBox: @Composable (Modifier) -> Unit = { m ->
        val pinch = if (!locked && fullscreen) Modifier.pointerInput(cur.key) {
            awaitEachGesture {
                awaitFirstDown(requireUnconsumed = false)
                do {
                    val event = awaitPointerEvent()
                    if (event.changes.count { it.pressed && it.previousPressed } >= 2) {
                        zoom = (zoom * event.calculateZoom()).coerceIn(1f, 3f)
                        event.changes.forEach { it.consume() }
                    }
                } while (event.changes.any { it.pressed })
            }
        } else Modifier
        Box(m.background(Color.Black).clipToBounds().then(swipe).then(pinch)) {
            VideoSurface(Modifier.fillMaxSize().graphicsLayer { scaleX = zoom; scaleY = zoom })
            if (!locked) PlayerChrome(cur, fullscreen, toggleFull, minimize, onSettings = { sheet = true }, onMore = { more = !more })
            else LockOverlay { locked = false }
            if (more && fullscreen) MorePanel(cur, queue, all, play, Modifier.align(Alignment.CenterEnd)) { more = false }
            ResumeChip(Modifier.align(Alignment.BottomStart).padding(bottom = if (fullscreen) 104.dp else 44.dp))      // above the time pill
            ErrorOverlay(onClose)
            NextCountdown(Modifier.align(Alignment.BottomEnd).padding(end = 16.dp, bottom = 64.dp))
        }
    }

    Box(Modifier.fillMaxSize().graphicsLayer {
        // the page closes up to its player, then that player shrinks into the small window's corner
        val s = shrink.value
        val visible = screenH - (screenH - playerH) * s
        clip = s > 0f
        shape = androidx.compose.ui.graphics.RectangleShape.let { _ -> androidx.compose.foundation.shape.GenericShape { size, _ -> addRect(androidx.compose.ui.geometry.Rect(0f, 0f, size.width, visible)) } }
        transformOrigin = androidx.compose.ui.graphics.TransformOrigin(1f, visible / screenH)
        val k = 1f - (1f - miniW / screenW) * s
        scaleX = k; scaleY = k
        translationX = -with(density) { 8.dp.toPx() } * s
        translationY = (miniBottom - visible) * s + (1f - enter.value) * screenH * 0.05f
        alpha = enter.value
    }.background(MaterialTheme.colorScheme.background)) {
        when {
            fullscreen -> playerBox(Modifier.fillMaxSize())
            splitView -> Row(Modifier.fillMaxSize().statusBarsPadding()) {
                LazyColumn(Modifier.weight(1f)) {
                    item { playerBox(Modifier.fillMaxWidth().aspectRatio(16 / 9f)) }
                    item { VideoInfo(cur) }
                }
                UpNext(cur, queue, all, play, Modifier.width(sideWidth).fillMaxHeight(), compact = false)
            }
            else -> Column(Modifier.fillMaxSize().statusBarsPadding()) {
                playerBox(Modifier.fillMaxWidth().aspectRatio(16 / 9f))
                UpNext(cur, queue, all, play, Modifier.fillMaxSize(), compact = false, header = { VideoInfo(cur) })
            }
        }
        DimOverlay()
    }
    if (sheet) PlayerSettings(onDismiss = { sheet = false }, onLock = { sheet = false; locked = true })
}

/**
 * Fullscreen video and Shorts: the phone's own bars (clock, ◁ ○ □) hidden, a swipe from the edge shows them for a moment.
 * Re-applied whenever our window gets the focus back — a settings sheet, a menu, Home or the small window all bring
 * the bars back, and nothing else would hide them again.
 */
@Composable
fun Immersive(on: Boolean) {
    val activity = LocalContext.current as Activity
    val view = LocalView.current
    val resumed = androidx.lifecycle.compose.LocalLifecycleOwner.current.lifecycle.currentStateAsState().value.isAtLeast(androidx.lifecycle.Lifecycle.State.RESUMED)
    val focused = androidx.compose.ui.platform.LocalWindowInfo.current.isWindowFocused
    LaunchedEffect(on, resumed, focused) {
        val c = WindowCompat.getInsetsController(activity.window, view)
        if (on) { c.systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE; c.hide(WindowInsetsCompat.Type.systemBars()) }
        else c.show(WindowInsetsCompat.Type.systemBars())
    }
    DisposableEffect(Unit) { onDispose { WindowCompat.getInsetsController(activity.window, view).show(WindowInsetsCompat.Type.systemBars()) } }
}

// ---------- YouTube's controls ----------

@Composable
private fun PlayerChrome(v: Video, fullscreen: Boolean, onFullscreen: () -> Unit, onClose: () -> Unit, onSettings: () -> Unit, onMore: () -> Unit) {
    val p by rememberPos()
    val player = Playback.player
    val scope = rememberCoroutineScope()
    var visible by remember { mutableStateOf(true) }
    var poke by remember { mutableIntStateOf(0) }
    var holding by remember { mutableStateOf(false) }
    var skipTap by remember { mutableStateOf(false) }
    var seekSide by remember { mutableIntStateOf(0) }       // -1 back, +1 forward
    var seekSec by remember { mutableIntStateOf(0) }
    var scrub by remember { mutableStateOf<Long?>(null) }
    val hasSubs = player.currentTracks.groups.any { it.type == C.TRACK_TYPE_TEXT }
    val subsOn = hasSubs && !player.trackSelectionParameters.disabledTrackTypes.contains(C.TRACK_TYPE_TEXT)
    val fresh = Lib.videos.collectAsState().value.firstOrNull { it.key == v.key && it.storageId == v.storageId } ?: v

    LaunchedEffect(visible, poke, p.playing, scrub) { if (visible && p.playing && scrub == null) { delay(3000); visible = false } }
    LaunchedEffect(seekSide, seekSec) { if (seekSide != 0) { delay(700); seekSide = 0; seekSec = 0 } }

    Box(Modifier.fillMaxSize().pointerInput(Unit) {
        detectTapGestures(
            onPress = {
                // press and hold = 2x while held, like YouTube
                val job = scope.launch { delay(450); holding = true; Playback.hold2x(true) }
                tryAwaitRelease()
                job.cancel()
                if (holding) { Playback.hold2x(false); holding = false; skipTap = true }
            },
            onTap = { if (skipTap) skipTap = false else { visible = !visible; poke++ } },
            onDoubleTap = { o ->
                val fwd = o.x > size.width / 2
                if (fwd) player.seekForward() else player.seekBack()
                if (seekSide == (if (fwd) 1 else -1)) seekSec += 10 else { seekSide = if (fwd) 1 else -1; seekSec = 10 }
            },
        )
    }) {
        // double-tap feedback: a soft half-circle with "10 сек."
        if (seekSide != 0) Box(Modifier.fillMaxHeight().fillMaxWidth(0.4f).align(if (seekSide > 0) Alignment.CenterEnd else Alignment.CenterStart)
            .clip(if (seekSide > 0) RoundedCornerShape(topStartPercent = 50, bottomStartPercent = 50) else RoundedCornerShape(topEndPercent = 50, bottomEndPercent = 50))
            .background(Color.White.copy(alpha = 0.12f)), contentAlignment = Alignment.Center) {
            Column(horizontalAlignment = Alignment.CenterHorizontally) {
                Icon(if (seekSide > 0) Icons.Rounded.FastForward else Icons.Rounded.FastRewind, null, tint = Color.White, modifier = Modifier.size(32.dp))
                Text("$seekSec сек.", color = Color.White, fontSize = 13.sp, fontWeight = FontWeight.Medium)
            }
        }
        if (holding) Row(Modifier.align(Alignment.TopCenter).padding(top = 16.dp).background(Color.Black.copy(alpha = 0.6f), RoundedCornerShape(18.dp))
            .padding(horizontal = 14.dp, vertical = 6.dp), verticalAlignment = Alignment.CenterVertically) {
            Text("2x", color = Color.White, fontWeight = FontWeight.Bold)
            Icon(Icons.Rounded.FastForward, null, tint = Color.White, modifier = Modifier.size(20.dp))
        }
        if (visible && !holding) {
            Box(Modifier.matchParentSize().background(Color.Black.copy(alpha = 0.45f)))
            // top: ⌄ (title · @channel in fullscreen) …… CC ⚙
            Row(Modifier.align(Alignment.TopStart).fillMaxWidth().padding(horizontal = 4.dp, vertical = if (fullscreen) 8.dp else 2.dp),
                verticalAlignment = Alignment.CenterVertically) {
                IconButton({ if (fullscreen) onFullscreen() else onClose() }) { Icon(Icons.Rounded.KeyboardArrowDown, "Свернуть", tint = Color.White) }
                if (fullscreen) Column(Modifier.weight(1f)) {
                    Text(fresh.title, color = Color.White, fontWeight = FontWeight.Bold, fontSize = 17.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    fresh.meta.channel?.let { Text(it, color = Color.White.copy(alpha = 0.75f), fontSize = 13.sp, maxLines = 1) }
                } else Spacer(Modifier.weight(1f))
                if (hasSubs) IconButton({ Playback.setSubtitles(!subsOn); poke++ }) {
                    Icon(if (subsOn) Icons.Rounded.ClosedCaption else Icons.Outlined.ClosedCaptionOff, "Субтитры", tint = Color.White)
                }
                IconButton(onSettings) { Icon(Icons.Outlined.Settings, "Настройки", tint = Color.White) }
            }
            // centre: ⏮ ▶ ⏭ in dark circles
            Row(Modifier.align(Alignment.Center), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(36.dp)) {
                CircleButton(Icons.Rounded.SkipPrevious, "Предыдущее", 48, player.hasPreviousMediaItem()) { player.seekToPreviousMediaItem(); poke++ }
                Box(Modifier.size(64.dp).clip(CircleShape).background(Color.Black.copy(alpha = 0.4f)).clickable {
                    if (player.isPlaying) player.pause() else { if (player.playbackState == Player.STATE_ENDED) player.seekTo(0); player.play() }
                    poke++
                }, contentAlignment = Alignment.Center) {
                    if (p.buffering) CircularProgressIndicator(Modifier.size(36.dp), color = Color.White, strokeWidth = 3.dp)
                    else Icon(if (p.playing) Icons.Rounded.Pause else Icons.Rounded.PlayArrow, if (p.playing) "Пауза" else "Смотреть", tint = Color.White, modifier = Modifier.size(44.dp))
                }
                CircleButton(Icons.Rounded.SkipNext, "Следующее", 48, player.hasNextMediaItem()) { player.seekToNextMediaItem(); poke++ }
            }
            // bottom: time in a pill …… ⛶
            Row(Modifier.align(Alignment.BottomStart).fillMaxWidth().padding(start = 12.dp, end = 4.dp, bottom = if (fullscreen) 76.dp else 12.dp),
                verticalAlignment = Alignment.CenterVertically) {
                Row(Modifier.background(Color.Black.copy(alpha = 0.5f), RoundedCornerShape(14.dp)).padding(horizontal = 10.dp, vertical = 4.dp)) {
                    Text(fmtTime(scrub ?: p.pos), color = Color.White, fontSize = 13.sp, fontWeight = FontWeight.Medium)
                    Text(" / " + fmtTime(p.dur), color = Color.White.copy(alpha = 0.75f), fontSize = 13.sp)
                    if (p.speed != 1f) Text(" · ${speedText(p.speed)}x", color = Color.White.copy(alpha = 0.75f), fontSize = 13.sp)
                }
                Spacer(Modifier.weight(1f))
                IconButton(onFullscreen) { Icon(if (fullscreen) Icons.Rounded.FullscreenExit else Icons.Rounded.Fullscreen, "Во весь экран", tint = Color.White) }
            }
            // fullscreen: YouTube's row under the bar — 👍 👎 … and "Другие видео"
            if (fullscreen) Row(Modifier.align(Alignment.BottomStart).fillMaxWidth().padding(horizontal = 12.dp, vertical = 8.dp),
                verticalAlignment = Alignment.CenterVertically) {
                IconButton({ Lib.rate(fresh, if (fresh.flags.rating > 0) 0 else 1); poke++ }) {
                    Icon(if (fresh.flags.rating > 0) Icons.Rounded.ThumbUp else Icons.Outlined.ThumbUp, "Нравится", tint = Color.White)
                }
                IconButton({ Lib.rate(fresh, if (fresh.flags.rating < 0) 0 else -1); poke++ }) {
                    Icon(if (fresh.flags.rating < 0) Icons.Rounded.ThumbDown else Icons.Outlined.ThumbDown, "Не нравится", tint = Color.White)
                }
                VideoMenu(fresh, tint = Color.White)
                Spacer(Modifier.weight(1f))
                Row(Modifier.clip(RoundedCornerShape(20.dp)).background(Color.Black.copy(alpha = 0.5f)).clickable { onMore(); poke++ }
                    .padding(start = 14.dp, end = 6.dp, top = 6.dp, bottom = 6.dp), verticalAlignment = Alignment.CenterVertically) {
                    Text("Другие видео", color = Color.White, fontSize = 14.sp, fontWeight = FontWeight.Medium)
                    Icon(Icons.Rounded.VideoLibrary, null, tint = Color.White, modifier = Modifier.padding(start = 8.dp).size(22.dp))
                }
            }
        }
        // the red line comes and goes with the controls — a clean picture otherwise
        if ((visible && !holding) || scrub != null) SeekBar(p, true, Modifier.align(Alignment.BottomCenter).fillMaxWidth()
            .padding(horizontal = if (fullscreen) 16.dp else 0.dp).padding(bottom = if (fullscreen) 56.dp else 0.dp),
            onScrub = { scrub = it; visible = true; poke++ }, onSeek = { player.seekTo(it); poke++ })
    }
}

@Composable
private fun CircleButton(icon: ImageVector, label: String, size: Int, enabled: Boolean, onClick: () -> Unit) = Box(
    Modifier.size(size.dp).clip(CircleShape).background(Color.Black.copy(alpha = 0.4f)).clickable(enabled = enabled, onClick = onClick),
    contentAlignment = Alignment.Center) {
    Icon(icon, label, tint = Color.White.copy(alpha = if (enabled) 1f else 0.35f), modifier = Modifier.size((size * 0.6).dp))
}

/** Fullscreen "Другие видео": the up-next list slides over the right side of the picture. */
@Composable
private fun MorePanel(cur: Video, queue: List<Video>, all: List<Video>, play: (List<Video>, Video) -> Unit, modifier: Modifier, onClose: () -> Unit) {
    Box(modifier.fillMaxHeight().width(380.dp).background(Color(0xF00F0F0F))) {
        UpNext(cur, queue, all, { l, v -> onClose(); play(l, v) }, Modifier.fillMaxSize().statusBarsPadding())
        IconButton(onClose, Modifier.align(Alignment.TopEnd).statusBarsPadding()) { Icon(Icons.Rounded.Close, "Закрыть", tint = Color.White) }
    }
}

@Composable
private fun SeekBar(p: Pos, thumb: Boolean, modifier: Modifier, onScrub: (Long?) -> Unit, onSeek: (Long) -> Unit) {
    var drag by remember { mutableStateOf<Float?>(null) }
    val dur = p.dur
    Box(modifier.height(22.dp)
        .pointerInput(dur) { detectTapGestures { o -> if (dur > 0) onSeek((o.x / size.width * dur).toLong()) } }
        .pointerInput(dur) {
            detectHorizontalDragGestures(
                onDragStart = { o -> drag = (o.x / size.width).coerceIn(0f, 1f); onScrub((drag!! * dur).toLong()) },
                onDragEnd = { drag?.let { onSeek((it * dur).toLong()) }; drag = null; onScrub(null) },
                onDragCancel = { drag = null; onScrub(null) },
            ) { ch, _ -> drag = (ch.position.x / size.width).coerceIn(0f, 1f); onScrub((drag!! * dur).toLong()) }
        }) {
        Canvas(Modifier.fillMaxSize()) {
            val f = drag ?: if (dur > 0) (p.pos.toFloat() / dur).coerceIn(0f, 1f) else 0f
            val b = if (dur > 0) (p.buf.toFloat() / dur).coerceIn(0f, 1f) else 0f
            val h = (if (drag != null) 5 else 3).dp.toPx()
            val y = size.height - h / 2 - 1.dp.toPx()
            drawRect(Color.White.copy(alpha = 0.3f), Offset(0f, y - h / 2), Size(size.width, h))
            drawRect(Color.White.copy(alpha = 0.5f), Offset(0f, y - h / 2), Size(size.width * b, h))
            drawRect(YtRed, Offset(0f, y - h / 2), Size(size.width * f, h))
            if (thumb) drawCircle(YtRed, (if (drag != null) 8 else 6).dp.toPx(), Offset(size.width * f, y))
        }
    }
}

// ---------- the gear: speed, subtitles, audio, sleep, lock ----------

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun PlayerSettings(onDismiss: () -> Unit, onLock: () -> Unit) {
    var page by remember { mutableStateOf("main") }
    val player = Playback.player
    val p by rememberPos()
    val ends by Playback.sleepEndsAt.collectAsState()
    val cur by Playback.current.collectAsState()
    val tracks = player.currentTracks.groups
    val subs = tracks.filter { it.type == C.TRACK_TYPE_TEXT }
    val audios = tracks.filter { it.type == C.TRACK_TYPE_AUDIO }
    val subsOff = player.trackSelectionParameters.disabledTrackTypes.contains(C.TRACK_TYPE_TEXT)
    var repeat by remember { mutableStateOf(player.repeatMode == Player.REPEAT_MODE_ONE) }
    // YouTube's sheet: dark, rounded, centred when the phone is sideways
    Box(Modifier.fillMaxSize(), contentAlignment = Alignment.BottomCenter) {
        Box(Modifier.matchParentSize().background(Color.Black.copy(alpha = 0.55f))
            .pointerInput(onDismiss) { detectTapGestures { onDismiss() } })
        val enter = remember { Animatable(1f) }
        LaunchedEffect(Unit) { enter.animateTo(0f, tween(200, easing = FastOutSlowInEasing)) }
        Surface(Modifier.widthIn(max = 600.dp).fillMaxWidth().fillMaxHeight(0.88f).wrapContentHeight(Alignment.Bottom)
            .graphicsLayer { translationY = size.height * enter.value },
            shape = RoundedCornerShape(topStart = 22.dp, topEnd = 22.dp), color = Color(0xFF212121), contentColor = Color.White) {
        Column(Modifier.verticalScroll(rememberScrollState()).navigationBarsPadding().padding(top = 10.dp, bottom = 12.dp)) {
            when (page) {
                "main" -> {
                    val m = cur?.meta
                    if (m?.height != null) SheetRow(Icons.Rounded.Tune, "Качество",
                        "${minOf(m.width ?: 0, m.height)}p${m.fps?.takeIf { it >= 50 }?.let { Math.round(it).toString() } ?: ""}", arrow = false) {}
                    SheetRow(Icons.Outlined.SlowMotionVideo, "Скорость воспроизведения", speedText(p.speed)) { page = "speed" }
                    if (subs.isNotEmpty()) SheetRow(Icons.Outlined.ClosedCaption, "Субтитры",
                        if (subsOff) "Откл." else subs.firstOrNull { it.isSelected }?.let { it.mediaTrackGroup.getFormat(0).label ?: it.mediaTrackGroup.getFormat(0).language } ?: "Вкл.") { page = "subs" }
                    if (audios.isNotEmpty()) SheetRow(Icons.Outlined.RecordVoiceOver, "Звуковая дорожка",
                        audios.firstOrNull { it.isSelected }?.mediaTrackGroup?.getFormat(0)?.let { it.label ?: it.language?.let(::langName) } ?: "Оригинальная",
                        arrow = audios.size > 1) { if (audios.size > 1) page = "audio" }
                    SheetRow(Icons.Rounded.Refresh, "Восстановить синхронизацию", "", arrow = false) { Playback.repairSync(); onDismiss() }
                    SheetRow(Icons.Outlined.Settings, "Ещё", "") { page = "more" }
                    SheetRow(Icons.Outlined.Lock, "Блокировка экрана", "", arrow = false) { onLock() }
                }
                "more" -> {
                    SheetToggle(Icons.Rounded.RepeatOne, "Повтор воспроизведения", repeat) {
                        repeat = it; player.repeatMode = if (it) Player.REPEAT_MODE_ONE else Player.REPEAT_MODE_OFF
                    }
                    SheetRow(Icons.Outlined.Bedtime, "Автовыключение", when (ends) { null -> "Отключено"; Long.MAX_VALUE -> "После видео"; else -> "Включено" }) { page = "sleep" }
                }
                "speed" -> {
                    Text(speedText(p.speed) + "x", Modifier.fillMaxWidth().padding(top = 8.dp), fontSize = 28.sp, fontWeight = FontWeight.Bold,
                        textAlign = androidx.compose.ui.text.style.TextAlign.Center)
                    Slider(p.speed, { player.setPlaybackSpeed((Math.round(it * 20) / 20f).coerceIn(0.25f, 3f)) }, Modifier.padding(horizontal = 24.dp),
                        valueRange = 0.25f..3f, colors = SliderDefaults.colors(thumbColor = Color.White, activeTrackColor = Color.White, inactiveTrackColor = Color(0xFF555555)))
                    Row(Modifier.horizontalScroll(rememberScrollState()).padding(horizontal = 16.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        listOf(0.5f, 0.75f, 1f, 1.25f, 1.5f, 1.75f, 2f, 2.5f, 3f).forEach { s ->
                            val on = p.speed == s
                            Text(speedText(s), Modifier.clip(RoundedCornerShape(8.dp)).background(if (on) Color.White else Color(0xFF3F3F3F))
                                .clickable { player.setPlaybackSpeed(s) }.padding(horizontal = 14.dp, vertical = 8.dp),
                                color = if (on) Color.Black else Color.White, fontWeight = FontWeight.Medium)
                        }
                    }
                }
                "subs" -> {
                    SheetChoice("Отключены", subsOff) { Playback.setSubtitles(false); onDismiss() }
                    subs.forEach { g ->
                        val f = g.mediaTrackGroup.getFormat(0)
                        SheetChoice(f.label ?: f.language?.let(::langName) ?: "Субтитры", !subsOff && g.isSelected) {
                            player.trackSelectionParameters = player.trackSelectionParameters.buildUpon()
                                .setTrackTypeDisabled(C.TRACK_TYPE_TEXT, false).setOverrideForType(TrackSelectionOverride(g.mediaTrackGroup, 0)).build()
                            onDismiss()
                        }
                    }
                }
                "audio" -> audios.forEach { g ->
                    val f = g.mediaTrackGroup.getFormat(0)
                    SheetChoice(f.label ?: f.language?.let(::langName) ?: "Дорожка", g.isSelected) {
                        player.trackSelectionParameters = player.trackSelectionParameters.buildUpon().setOverrideForType(TrackSelectionOverride(g.mediaTrackGroup, 0)).build()
                        onDismiss()
                    }
                }
                "sleep" -> {
                    Text("Пока таймер идёт, видео не засчитываются как просмотренные и не удаляются.", Modifier.padding(horizontal = 24.dp, vertical = 8.dp),
                        color = Color.White.copy(alpha = 0.7f), fontSize = 13.sp)
                    SheetChoice("Отключено", ends == null) { Playback.stopSleep(); onDismiss() }
                    listOf(15, 30, 45, 60, 90).forEach { m -> SheetChoice("$m минут", false) { Playback.startSleep(m); onDismiss() } }
                    SheetChoice("После окончания видео", ends == Long.MAX_VALUE) { Playback.startSleep(null); onDismiss() }
                    if (ends != null) SheetChoice("Затемнить экран", false) { Playback.dimmed.value = true; onDismiss() }
                }
            }
        }
    }
}
}

private fun langName(code: String) = when (code.take(2)) { "ru" -> "Русский"; "en" -> "Английский"; "uk" -> "Украинский"; "de" -> "Немецкий"; else -> code }

@Composable
private fun SheetToggle(icon: ImageVector, title: String, on: Boolean, onChange: (Boolean) -> Unit) = Row(
    Modifier.fillMaxWidth().clickable { onChange(!on) }.padding(horizontal = 24.dp, vertical = 10.dp), verticalAlignment = Alignment.CenterVertically) {
    Icon(icon, null)
    Text(title, Modifier.padding(start = 20.dp).weight(1f), fontSize = 16.sp)
    Switch(on, onChange, colors = SwitchDefaults.colors(checkedTrackColor = YtBlue, checkedThumbColor = Color.White, checkedBorderColor = YtBlue))
}

@Composable
private fun SheetRow(icon: ImageVector, title: String, value: String, arrow: Boolean = true, onClick: () -> Unit) = Row(
    Modifier.fillMaxWidth().clickable(onClick = onClick).padding(horizontal = 24.dp, vertical = 15.dp), verticalAlignment = Alignment.CenterVertically) {
    Icon(icon, null)
    Text(title, Modifier.padding(start = 20.dp).weight(1f), fontSize = 16.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
    Text(value, color = Color.White.copy(alpha = 0.7f), fontSize = 14.sp, maxLines = 1)
    if (arrow) Icon(Icons.Rounded.ChevronRight, null, tint = Color.White.copy(alpha = 0.7f))
}

@Composable
private fun SheetChoice(title: String, selected: Boolean, onClick: () -> Unit) = Row(
    Modifier.fillMaxWidth().clickable(onClick = onClick).padding(horizontal = 24.dp, vertical = 14.dp), verticalAlignment = Alignment.CenterVertically) {
    Box(Modifier.width(44.dp)) { if (selected) Icon(Icons.Rounded.Check, null) }
    Text(title, fontSize = 15.sp)
}

// ---------- under the video ----------

@Composable
private fun VideoInfo(v: Video) {
    val fresh = Lib.videos.collectAsState().value.firstOrNull { it.key == v.key && it.storageId == v.storageId } ?: v
    Column(Modifier.padding(start = 12.dp, end = 12.dp, top = 12.dp)) {
        Text(fresh.title, fontSize = 18.sp, lineHeight = 24.sp, fontWeight = FontWeight.Bold, maxLines = 3, overflow = TextOverflow.Ellipsis)
        // YouTube's line under the title: when it came out, in words ("9 месяцев назад")
        val published = fresh.meta.published?.let { runCatching { java.text.SimpleDateFormat("yyyy-MM-dd", java.util.Locale.US).parse(it)?.time }.getOrNull() }
        Text(listOfNotNull(fresh.meta.viewCount?.let { count(it) + " просмотров" }, ago(published).takeIf { it.isNotEmpty() }, ago(fresh.meta.addedAt).takeIf { it.isNotEmpty() }?.let { "добавлено $it" })
            .joinToString(" · "), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(top = 4.dp))
        val ofChannel = Lib.videos.collectAsState().value.count { it.present && it.meta.channel != null && it.meta.channel == fresh.meta.channel }
        Row(Modifier.padding(vertical = 12.dp), verticalAlignment = Alignment.CenterVertically) {
            Avatar(fresh, 40)
            Column(Modifier.padding(start = 12.dp)) {
                Text(fresh.meta.channel ?: "Без канала", fontWeight = FontWeight.Bold, fontSize = 16.sp)
                if (ofChannel > 1) Text("$ofChannel ${plural(ofChannel.toLong(), "видео", "видео", "видео")} в библиотеке", style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        }
        // YouTube's row of pills
        Row(Modifier.horizontalScroll(rememberScrollState()).padding(bottom = 12.dp), horizontalArrangement = Arrangement.spacedBy(8.dp),
            verticalAlignment = Alignment.CenterVertically) {
            Row(Modifier.clip(RoundedCornerShape(18.dp)).background(MaterialTheme.colorScheme.surfaceVariant).height(36.dp),
                verticalAlignment = Alignment.CenterVertically) {
                Box(Modifier.fillMaxHeight().clickable { Lib.rate(fresh, if (fresh.flags.rating > 0) 0 else 1) }.padding(horizontal = 14.dp), contentAlignment = Alignment.Center) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Icon(if (fresh.flags.rating > 0) Icons.Rounded.ThumbUp else Icons.Outlined.ThumbUp, "Нравится", Modifier.size(20.dp))
                        fresh.meta.likeCount?.let { Text(count(it), Modifier.padding(start = 6.dp), fontSize = 13.sp) }
                    }
                }
                Box(Modifier.width(1.dp).height(20.dp).background(MaterialTheme.colorScheme.outline))
                Box(Modifier.fillMaxHeight().clickable { Lib.rate(fresh, if (fresh.flags.rating < 0) 0 else -1) }.padding(horizontal = 14.dp), contentAlignment = Alignment.Center) {
                    Icon(if (fresh.flags.rating < 0) Icons.Rounded.ThumbDown else Icons.Outlined.ThumbDown, "Не нравится", Modifier.size(20.dp))
                }
            }
            val ends by Playback.sleepEndsAt.collectAsState()
            var sleepMenu by remember { mutableStateOf(false) }
            Box {
                Pill(Icons.Rounded.Timer, if (ends != null) "Таймер сна ✓" else "Таймер сна") { sleepMenu = true }
                DropdownMenu(sleepMenu, { sleepMenu = false }) {
                    listOf(15, 30, 45, 60, 90).forEach { m -> DropdownMenuItem({ Text("$m минут") }, { sleepMenu = false; Playback.startSleep(m) }) }
                    DropdownMenuItem({ Text("До конца видео") }, { sleepMenu = false; Playback.startSleep(null) })
                    if (ends != null) {
                        DropdownMenuItem({ Text("Затемнить экран") }, { sleepMenu = false; Playback.dimmed.value = true })
                        DropdownMenuItem({ Text("Выключить таймер") }, { sleepMenu = false; Playback.stopSleep() })
                    }
                }
            }
            VideoMenu(fresh)
        }
        CommentsCard(fresh)
    }
}

private fun count(n: Long) = when {
    n >= 1_000_000 -> "%.1f млн".format(n / 1e6).replace(".0", "")
    n >= 1000 -> "%.1f тыс.".format(n / 1e3).replace(".0", "")
    else -> n.toString()
}

@Composable
private fun CommentsCard(v: Video) {
    val list by produceState<List<Comment>?>(null, v.key, v.meta.comments) { value = Lib.comments(v) }
    var open by remember { mutableStateOf(false) }
    val l = list ?: return
    if (l.isEmpty()) return
    Column(Modifier.padding(bottom = 12.dp).fillMaxWidth().clip(RoundedCornerShape(12.dp)).background(MaterialTheme.colorScheme.surfaceVariant)
        .clickable { open = true }.padding(12.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text("Комментарии", fontWeight = FontWeight.Bold, fontSize = 15.sp)
            Text("  " + count(v.meta.commentCount ?: l.size.toLong()), color = MaterialTheme.colorScheme.onSurfaceVariant, fontSize = 14.sp)
        }
        Row(Modifier.padding(top = 8.dp), verticalAlignment = Alignment.CenterVertically) {
            Avatar(l[0].a.removePrefix("@"), 24)
            Text(l[0].t, Modifier.padding(start = 10.dp), maxLines = 2, overflow = TextOverflow.Ellipsis, fontSize = 13.sp)
        }
    }
    if (open) CommentsSheet(l, v.meta.commentCount) { open = false }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun CommentsSheet(list: List<Comment>, total: Long?, onDismiss: () -> Unit) {
    ModalBottomSheet(onDismissRequest = onDismiss, containerColor = MaterialTheme.colorScheme.surface, sheetMaxWidth = 720.dp) {
        CommentsContent(list, total)
    }
}

@Composable
private fun CommentsContent(list: List<Comment>?, total: Long?) {
    val threads = remember(list) { list.orEmpty().filter { it.parent != null && it.parent != "root" }.groupBy { it.parent } }
    val roots = remember(list) { list.orEmpty().filter { it.parent == null || it.parent == "root" } }
    Text("Комментарии" + (total ?: list?.size?.toLong())?.let { " · " + count(it) }.orEmpty(),
        Modifier.padding(16.dp), fontWeight = FontWeight.Bold, fontSize = 18.sp)
    HorizontalDivider(color = MaterialTheme.colorScheme.outline)
    if (list.isNullOrEmpty()) {
        Text(if (list == null) "Загрузка…" else "Для этого видео комментарии не сохранены.",
            Modifier.padding(16.dp), color = MaterialTheme.colorScheme.onSurfaceVariant)
    } else LazyColumn(Modifier.fillMaxWidth().heightIn(max = 640.dp), contentPadding = WindowInsets.navigationBars.asPaddingValues()) {
        items(roots) { c ->
            var open by remember(c.id) { mutableStateOf(false) }
            val replies = c.id?.let(threads::get).orEmpty()
            CommentRow(c)
            if (replies.isNotEmpty()) TextButton({ open = !open }, Modifier.padding(start = 52.dp)) {
                Text(if (open) "Скрыть ответы" else "Ответы (" + replies.size + ")")
            }
            if (open) replies.forEach { CommentRow(it, inset = 36) }
        }
    }
}

@Composable
private fun CommentRow(c: Comment, inset: Int = 0) {
    Row(Modifier.fillMaxWidth().padding(start = (16 + inset).dp, end = 16.dp, top = 10.dp, bottom = 10.dp)) {
        Avatar(c.a.removePrefix("@"), 32)
        Column(Modifier.padding(start = 12.dp)) {
            if (c.p) Text("Закреплено", fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(c.a, fontSize = 12.sp, fontWeight = if (c.u) FontWeight.Bold else FontWeight.Normal,
                    color = MaterialTheme.colorScheme.onSurfaceVariant)
                c.ts?.let { Text("  ·  " + ago(it * 1000), fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant) }
            }
            Text(c.t, fontSize = 14.sp, lineHeight = 19.sp, modifier = Modifier.padding(top = 2.dp))
            if (c.l > 0) Row(Modifier.padding(top = 6.dp), verticalAlignment = Alignment.CenterVertically) {
                Icon(Icons.Outlined.ThumbUp, null, Modifier.size(15.dp), tint = MaterialTheme.colorScheme.onSurfaceVariant)
                Text("  " + count(c.l), fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        }
    }
}

@Composable
private fun Pill(icon: ImageVector, text: String, onClick: () -> Unit) = Row(
    Modifier.clip(RoundedCornerShape(18.dp)).background(MaterialTheme.colorScheme.surfaceVariant).clickable(onClick = onClick).height(36.dp).padding(horizontal = 14.dp),
    verticalAlignment = Alignment.CenterVertically) {
    Icon(icon, null, Modifier.size(18.dp))
    Text(text, Modifier.padding(start = 6.dp), fontSize = 14.sp, fontWeight = FontWeight.Medium)
}

/** [compact] = small rows (tablet side column, fullscreen panel); otherwise full-width cards like the home feed. */
@Composable
private fun UpNext(cur: Video, queue: List<Video>, all: List<Video>, play: (List<Video>, Video) -> Unit, modifier: Modifier,
                   compact: Boolean = true, header: (@Composable () -> Unit)? = null) {
    val playlist by Playback.playlistMode.collectAsState()
    val recs by produceState(emptyList<Video>(), cur.key, all, playlist, queue) {
        value = if (playlist) queue.dropWhile { it.key != cur.key }.drop(1)
            else kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.Default) { recommended(cur, all) }
    }
    val open: (Video) -> Unit = { video ->
        if (playlist) Playback.play(queue, video, fromPlaylist = true) else play(recs, video)
    }
    val bottom = WindowInsets.navigationBars.asPaddingValues().calculateBottomPadding()
    if (compact) LazyColumn(modifier.background(MaterialTheme.colorScheme.background), contentPadding = PaddingValues(top = 8.dp, bottom = bottom)) {
        header?.let { h -> item { h() } }
        items(recs, key = { "r" + it.storageId + it.key }) { r -> NextRow(r) { open(r) } }
    }
    // cards like the home feed: one column on a phone, two under the video on a tablet, one in the tablet's side column
    else {
        val wide = wideFeed()
        LazyVerticalGrid(GridCells.Adaptive(330.dp), modifier.background(MaterialTheme.colorScheme.background),
            contentPadding = PaddingValues(start = if (wide) 16.dp else 0.dp, end = if (wide) 16.dp else 0.dp, top = if (header == null) 12.dp else 0.dp, bottom = bottom),
            horizontalArrangement = Arrangement.spacedBy(if (wide) 16.dp else 0.dp)) {
            header?.let { h -> item(span = { GridItemSpan(maxLineSpan) }) { h() } }
            gridItems(recs, key = { "r" + it.storageId + it.key }) { r -> VideoCard(r, { open(r) }) }
        }
    }
}

@Composable
private fun NextCountdown(modifier: Modifier) {
    val remaining by Playback.nextIn.collectAsState()
    if (remaining <= 0) return
    Surface(modifier, shape = RoundedCornerShape(16.dp), color = Color(0xF52A2434), contentColor = Color.White) {
        Row(Modifier.padding(horizontal = 12.dp), verticalAlignment = Alignment.CenterVertically) {
            Text("Следующее через $remaining с")
            TextButton(Playback::cancelNext) { Text("Отмена", color = Color.White) }
        }
    }
}

@Composable
private fun NextRow(v: Video, onClick: () -> Unit) {
    Row(Modifier.fillMaxWidth().clickable(onClick = onClick).padding(horizontal = 12.dp, vertical = 6.dp)) {
        Box(Modifier.width(160.dp).aspectRatio(16 / 9f).clip(RoundedCornerShape(8.dp))) {
            Thumb(v, Modifier.matchParentSize())
            v.meta.durationMs?.let {
                Text(fmtTime(it), Modifier.align(Alignment.BottomEnd).padding(4.dp).background(Color.Black.copy(alpha = 0.8f), RoundedCornerShape(4.dp))
                    .padding(horizontal = 4.dp), color = Color.White, fontSize = 11.sp, fontWeight = FontWeight.Medium)
            }
            val f = if (v.finished) 1f else v.progress
            if (f > 0f) Box(Modifier.align(Alignment.BottomStart).fillMaxWidth(f).height(3.dp).background(YtRed))
        }
        Column(Modifier.padding(start = 10.dp).weight(1f)) {
            Text(v.title, maxLines = 2, overflow = TextOverflow.Ellipsis, fontSize = 14.sp, fontWeight = FontWeight.Medium, lineHeight = 18.sp)
            Text(v.meta.channel ?: "", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1,
                modifier = Modifier.padding(top = 2.dp))
        }
    }
}

// ---------- miniplayer: YouTube's floating window — the video keeps going in a corner while you browse ----------

@Composable
fun FloatingPlayer(areaW: Dp, areaH: Dp, aboveTabs: Boolean, onClose: () -> Unit) {
    Playback.current.collectAsState().value ?: return
    val p by rememberPos()
    val d = LocalDensity.current
    val scope = rememberCoroutineScope()
    val tablet = LocalConfiguration.current.smallestScreenWidthDp >= 600
    val w = if (tablet) 360.dp else areaW * 0.5f
    val h = w * 9f / 16f + 2.dp + 44.dp
    val bars = WindowInsets.systemBars.asPaddingValues()
    val margin = 8.dp
    val topY = bars.calculateTopPadding() + 60.dp                                  // under the top bar
    val bottomY = areaH - h - margin - bars.calculateBottomPadding() - (if (aboveTabs) 49.dp else 0.dp)
    // corners: 0 bottom-right, 1 bottom-left, 2 top-right, 3 top-left
    fun spot(c: Int) = with(d) { Offset((if (c % 2 == 0) areaW - w - margin else margin).toPx(), (if (c < 2) bottomY else topY).toPx()) }
    var corner by remember { mutableIntStateOf(0) }
    val pos = remember { Animatable(spot(0), Offset.VectorConverter) }
    val appear = remember { Animatable(0f) }
    LaunchedEffect(Unit) { appear.animateTo(1f, tween(140)) }
    LaunchedEffect(corner, areaW, areaH, aboveTabs) { pos.animateTo(spot(corner)) }
    val expand = { Playback.fromMini = true; Playback.minimized.value = false }

    Column(Modifier.offset { IntOffset(pos.value.x.roundToInt(), pos.value.y.roundToInt()) }.width(w)
        .graphicsLayer { alpha = appear.value; scaleX = 0.85f + 0.15f * appear.value; scaleY = scaleX }
        .shadow(10.dp, RoundedCornerShape(12.dp)).clip(RoundedCornerShape(12.dp)).background(Color(0xFF212121))
        .pointerInput(areaW, areaH, aboveTabs) {
            // drag it anywhere; let go and it settles in the nearest corner. Dragged off the bottom = closed.
            detectDragGestures(onDragEnd = {
                val c = pos.value
                if (c.y > (bottomY + h * 0.35f).toPx()) { onClose(); return@detectDragGestures }      // pulled down past its lowest spot
                val right = c.x + w.toPx() / 2 > areaW.toPx() / 2
                val lower = c.y + h.toPx() / 2 > areaH.toPx() / 2
                val next = (if (lower) 0 else 2) + (if (right) 0 else 1)
                if (next == corner) scope.launch { pos.animateTo(spot(corner)) } else corner = next
            }) { ch, drag -> ch.consume(); scope.launch { pos.snapTo(pos.value + drag) } }
        }) {
        Box(Modifier.fillMaxWidth().aspectRatio(16 / 9f).background(Color.Black).clickable(onClick = expand)) {
            VideoSurface(Modifier.fillMaxSize())
            IconButton(onClose, Modifier.align(Alignment.TopEnd).size(40.dp)) {
                Icon(Icons.Rounded.Close, "Закрыть", Modifier.background(Color.Black.copy(alpha = 0.45f), CircleShape).padding(3.dp), tint = Color.White)
            }
        }
        val f = if (p.dur > 0) (p.pos.toFloat() / p.dur).coerceIn(0f, 1f) else 0f
        Box(Modifier.fillMaxWidth().height(2.dp).background(Color.White.copy(alpha = 0.25f))) {
            Box(Modifier.fillMaxWidth(f).fillMaxHeight().background(YtRed))
        }
        Row(Modifier.fillMaxWidth().height(44.dp), horizontalArrangement = Arrangement.SpaceEvenly, verticalAlignment = Alignment.CenterVertically) {
            IconButton({ Playback.player.seekBack() }) { Icon(Icons.Rounded.Replay10, "Назад на 10 секунд", tint = Color.White) }
            IconButton({ if (Playback.player.isPlaying) Playback.player.pause() else Playback.player.play() }) {
                if (p.buffering) CircularProgressIndicator(Modifier.size(22.dp), color = Color.White, strokeWidth = 2.dp)
                else Icon(if (p.playing) Icons.Rounded.Pause else Icons.Rounded.PlayArrow, if (p.playing) "Пауза" else "Смотреть", tint = Color.White)
            }
            IconButton({ Playback.player.seekForward() }) { Icon(Icons.Rounded.Forward10, "Вперёд на 10 секунд", tint = Color.White) }
        }
    }
}

// ---------- overlays ----------

/** Black screen at minimum brightness while the timer runs; a tap brings the picture back. */
@Composable
private fun DimOverlay() {
    val dimmed by Playback.dimmed.collectAsState()
    val activity = LocalContext.current as Activity
    LaunchedEffect(dimmed) { brightness(activity, if (dimmed) 0.01f else -1f) }
    DisposableEffect(Unit) { onDispose { brightness(activity, -1f) } }
    if (dimmed) Box(Modifier.fillMaxSize().background(Color.Black).pointerInput(Unit) { detectTapGestures { Playback.dimmed.value = false } })
}

@Composable
private fun LockOverlay(onUnlock: () -> Unit) {
    var show by remember { mutableStateOf(true) }
    LaunchedEffect(show) { if (show) { delay(3000); show = false } }
    Box(Modifier.fillMaxSize().pointerInput(Unit) { detectTapGestures { show = true } }) {
        if (show) FilledTonalButton(onUnlock, Modifier.align(Alignment.Center)) {
            Icon(Icons.Rounded.LockOpen, null); Spacer(Modifier.width(8.dp)); Text("Разблокировать")
        }
    }
}

@Composable
private fun ResumeChip(modifier: Modifier) {
    val at by Playback.resumedAt.collectAsState()
    LaunchedEffect(at) { if (at != null) { delay(6000); Playback.resumedAt.value = null } }
    at?.let {
        Row(modifier.padding(12.dp).background(Color.Black.copy(alpha = 0.75f), RoundedCornerShape(20.dp)).padding(start = 14.dp),
            verticalAlignment = Alignment.CenterVertically) {
            Text("Продолжено с ${fmtTime(it)}", color = Color.White, style = MaterialTheme.typography.bodySmall)
            TextButton(Playback::restart) { Text("С начала", color = YtBlue, fontWeight = FontWeight.Medium) }
        }
    }
}

@Composable
private fun ErrorOverlay(onClose: () -> Unit) {
    val err by Playback.error.collectAsState()
    err?.let { e ->
        Column(Modifier.fillMaxSize().background(Color.Black.copy(alpha = 0.85f)).padding(24.dp),
            verticalArrangement = Arrangement.Center, horizontalAlignment = Alignment.CenterHorizontally) {
            Icon(Icons.Rounded.ErrorOutline, null, tint = Color.White, modifier = Modifier.size(40.dp))
            Spacer(Modifier.height(12.dp))
            Text(e, color = Color.White, style = MaterialTheme.typography.bodyMedium)
            Spacer(Modifier.height(16.dp))
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Button({ Playback.error.value = null; Playback.player.prepare(); Playback.player.play() }) { Text("Повторить") }
                if (Playback.player.hasNextMediaItem()) OutlinedButton({ Playback.error.value = null; Playback.player.seekToNextMediaItem(); Playback.player.prepare(); Playback.player.play() }) {
                    Text("Следующее", color = Color.White)
                }
                OutlinedButton(onClose) { Text("Закрыть", color = Color.White) }
            }
        }
    }
}

/** Kept for the Shorts screen: the timer menu behind a timer icon. */
@Composable
fun SleepButton() {
    val ends by Playback.sleepEndsAt.collectAsState()
    var open by remember { mutableStateOf(false) }
    Box {
        IconButton({ open = true }) { Icon(Icons.Rounded.Timer, "Таймер сна", tint = if (ends != null) YtRed else Color.White) }
        DropdownMenu(open, { open = false }) {
            listOf(15, 30, 45, 60, 90).forEach { m -> DropdownMenuItem({ Text("$m минут") }, { open = false; Playback.startSleep(m) }) }
            DropdownMenuItem({ Text("До конца видео") }, { open = false; Playback.startSleep(null) })
            if (ends != null) {
                DropdownMenuItem({ Text("Затемнить экран") }, { open = false; Playback.dimmed.value = true })
                DropdownMenuItem({ Text("Выключить таймер") }, { open = false; Playback.stopSleep() })
            }
        }
    }
}

// ---------- Shorts ----------

@Composable
fun ShortsPlayer(onClose: () -> Unit) {
    val queue by Playback.queue.collectAsState()
    val cur by Playback.current.collectAsState()
    val all by Lib.videos.collectAsState()
    if (queue.isEmpty()) return
    val idx = queue.indexOfFirst { it.key == cur?.key }.coerceAtLeast(0)
    val pager = rememberPagerState(idx) { queue.size }
    val p by rememberPos()
    val firstFrame by Playback.rendered.collectAsState()
    val scope = rememberCoroutineScope()
    var holding by remember { mutableStateOf(false) }
    var skipTap by remember { mutableStateOf(false) }
    var talk by remember { mutableStateOf<Video?>(null) }                                         // whose comments are open
    LaunchedEffect(idx) { if (pager.settledPage != idx) pager.scrollToPage(idx) }          // player → pager
    LaunchedEffect(pager.settledPage) {                                                           // pager → player
        val pg = pager.settledPage
        if (Playback.player.currentMediaItemIndex != pg) { Playback.player.seekToDefaultPosition(pg); Playback.player.play() }
    }
    BackHandler { if (talk != null) talk = null else onClose() }
    Immersive(true)
    // a Short repeats until you swipe, like YouTube
    DisposableEffect(Unit) {
        Playback.player.repeatMode = Player.REPEAT_MODE_ONE
        Playback.player.pauseAtEndOfMediaItems = false
        onDispose {
            Playback.player.repeatMode = Player.REPEAT_MODE_OFF
            Playback.player.pauseAtEndOfMediaItems = true
        }
    }

    Box(Modifier.fillMaxSize().background(Color.Black)) {
        VerticalPager(pager, Modifier.fillMaxSize(), userScrollEnabled = talk == null) { page ->
            val s = all.firstOrNull { it.key == queue[page].key && it.storageId == queue[page].storageId } ?: queue[page]
            Box(Modifier.fillMaxSize()) {
                val active = page == pager.settledPage && cur?.key == s.key
                val ready = active && firstFrame == s.key
                if (!ready) Thumb(s, Modifier.fillMaxSize())
                if (active) Box(Modifier.fillMaxSize().pointerInput(Unit) {
                    detectTapGestures(
                        onPress = {
                            val job = scope.launch { delay(450); holding = true; Playback.hold2x(true) }
                            tryAwaitRelease(); job.cancel()
                            if (holding) { Playback.hold2x(false); holding = false; skipTap = true }
                        },
                        onTap = { if (skipTap) skipTap = false else if (Playback.player.isPlaying) Playback.player.pause() else Playback.player.play() },
                    )
                }) { VideoSurface(Modifier.fillMaxSize().graphicsLayer { alpha = if (ready) 1f else 0f }) }
                if (page == pager.settledPage && !p.playing && !p.buffering && !holding)
                    Icon(Icons.Rounded.PlayArrow, null, Modifier.align(Alignment.Center).size(72.dp), tint = Color.White.copy(alpha = 0.8f))
                if (page == pager.settledPage && holding) Row(Modifier.align(Alignment.TopCenter).statusBarsPadding().padding(top = 16.dp)
                    .background(Color.Black.copy(alpha = 0.6f), RoundedCornerShape(18.dp)).padding(horizontal = 14.dp, vertical = 6.dp),
                    verticalAlignment = Alignment.CenterVertically) {
                    Text("2x", color = Color.White, fontWeight = FontWeight.Bold)
                    Icon(Icons.Rounded.FastForward, null, tint = Color.White, modifier = Modifier.size(20.dp))
                }
                Column(Modifier.align(Alignment.BottomStart).fillMaxWidth(0.78f).widthIn(max = 460.dp).padding(start = 16.dp, bottom = 20.dp)) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Avatar(s, 32)
                        Text(s.meta.channel ?: "", Modifier.padding(start = 8.dp), color = Color.White, fontWeight = FontWeight.SemiBold, style = OverVideo)
                    }
                    Text(s.title, color = Color.White, maxLines = 2, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.bodyMedium.merge(OverVideo),
                        modifier = Modifier.padding(top = 8.dp))
                }
                // YouTube's column on the right
                Column(Modifier.align(Alignment.BottomEnd).padding(end = 8.dp, bottom = 20.dp),
                    horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(14.dp)) {
                    ShortAction(if (s.flags.rating > 0) Icons.Rounded.ThumbUp else Icons.Outlined.ThumbUp, s.meta.likeCount?.let(::count) ?: "Нравится") { Lib.rate(s, if (s.flags.rating > 0) 0 else 1) }
                    ShortAction(if (s.flags.rating < 0) Icons.Rounded.ThumbDown else Icons.Outlined.ThumbDown, "Не нравится") { Lib.rate(s, if (s.flags.rating < 0) 0 else -1) }
                    ShortAction(Icons.Outlined.ChatBubbleOutline, s.meta.commentCount?.let(::count) ?: "Комментарии") { talk = s }
                    SleepButton()
                    VideoMenu(s, tint = Color.White)
                }
                if (page == pager.settledPage) ShortProgress(p, Modifier.align(Alignment.BottomStart))
            }
        }
        ErrorOverlay(onClose)
        DimOverlay()
        talk?.let { video ->
            val list by produceState<List<Comment>?>(null, video.key) { value = Lib.comments(video) }
            Box(Modifier.matchParentSize().background(Color.Black.copy(alpha = 0.48f))
                .pointerInput(Unit) { detectTapGestures { talk = null } })
            Surface(Modifier.align(Alignment.BottomCenter).widthIn(max = 720.dp).fillMaxWidth().fillMaxHeight(0.7f),
                shape = RoundedCornerShape(topStart = 22.dp, topEnd = 22.dp)) {
                Column { CommentsContent(list, video.meta.commentCount) }
            }
        }
    }
}

/** White text over a picture of any brightness: a soft shadow, like YouTube's. */
private val OverVideo = androidx.compose.ui.text.TextStyle(shadow = androidx.compose.ui.graphics.Shadow(Color.Black.copy(alpha = 0.7f), blurRadius = 6f))

@Composable
private fun ShortAction(icon: ImageVector, label: String, onClick: () -> Unit) = Column(horizontalAlignment = Alignment.CenterHorizontally,
    modifier = Modifier.clip(RoundedCornerShape(12.dp)).clickable(onClick = onClick).padding(4.dp)) {
    Box(Modifier.size(48.dp).clip(CircleShape).background(Color.Black.copy(alpha = 0.3f)), contentAlignment = Alignment.Center) {
        Icon(icon, label, tint = Color.White)
    }
    Text(label, color = Color.White, fontSize = 12.sp, fontWeight = FontWeight.Medium, style = OverVideo, modifier = Modifier.padding(top = 2.dp))
}

@Composable
private fun ShortProgress(p: Pos, modifier: Modifier) {
    val f = if (p.dur > 0) (p.pos.toFloat() / p.dur).coerceIn(0f, 1f) else 0f
    Box(modifier.fillMaxWidth().height(3.dp).background(Color.White.copy(alpha = 0.3f))) {
        Box(Modifier.fillMaxWidth(f).fillMaxHeight().background(YtRed))
    }
}
