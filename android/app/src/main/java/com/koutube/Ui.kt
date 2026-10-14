package com.koutube

import android.net.Uri
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.GridItemSpan
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.rounded.ArrowBack
import androidx.compose.material.icons.filled.*
import androidx.compose.material.icons.outlined.*
import androidx.compose.material.icons.rounded.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import kotlinx.coroutines.launch

// ---------- theme ----------

// YouTube's own palette: near-black or white, grey chips, red only for the logo and progress.
val YtRed = Color(0xFFFF0000)
val YtBlue = Color(0xFF3EA6FF)
private val Red = YtRed

@Composable
fun KouTubeTheme(dark: Boolean, content: @Composable () -> Unit) = MaterialTheme(
    colorScheme = if (dark) darkColorScheme(
        primary = Color.White, onPrimary = Color(0xFF0F0F0F), background = Color(0xFF0F0F0F), surface = Color(0xFF0F0F0F),
        onBackground = Color(0xFFF1F1F1), onSurface = Color(0xFFF1F1F1), surfaceVariant = Color(0xFF272727), onSurfaceVariant = Color(0xFFAAAAAA),
        secondaryContainer = Color(0xFF272727), onSecondaryContainer = Color(0xFFF1F1F1), surfaceContainer = Color(0xFF0F0F0F),
        surfaceContainerHigh = Color(0xFF212121), surfaceContainerHighest = Color(0xFF272727), outline = Color(0xFF3F3F3F))
    else lightColorScheme(
        primary = Color(0xFF0F0F0F), onPrimary = Color.White, background = Color.White, surface = Color.White,
        onBackground = Color(0xFF0F0F0F), onSurface = Color(0xFF0F0F0F), surfaceVariant = Color(0xFFF2F2F2), onSurfaceVariant = Color(0xFF606060),
        secondaryContainer = Color(0xFFF2F2F2), onSecondaryContainer = Color(0xFF0F0F0F), surfaceContainer = Color.White,
        surfaceContainerHigh = Color.White, surfaceContainerHighest = Color(0xFFF2F2F2), outline = Color(0xFFE5E5E5)),
    content = content,
)

/** The logo in the top bar: a red rounded rectangle with a white triangle, then the name. */
@Composable
fun Logo() = Row(verticalAlignment = Alignment.CenterVertically) {
    Box(Modifier.size(30.dp, 21.dp).background(YtRed, RoundedCornerShape(6.dp)), contentAlignment = Alignment.Center) {
        Icon(Icons.Rounded.PlayArrow, null, Modifier.size(17.dp), tint = Color.White)
    }
    Text("KouTube", Modifier.padding(start = 4.dp), fontWeight = FontWeight.Bold, fontSize = 20.sp, letterSpacing = (-0.8).sp)
}

/** The channel picture if the PC fetched one, otherwise YouTube's letter in a coloured circle. */
@Composable
fun Avatar(v: Video, size: Int = 36) {
    val img by produceState<ImageBitmap?>(null, v.meta.avatar, v.meta.channel) { value = Thumbs.avatar(v) }
    img?.let { Image(it, v.meta.channel, Modifier.size(size.dp).clip(CircleShape), contentScale = ContentScale.Crop) } ?: Avatar(v.meta.channel, size)
}

@Composable
fun Avatar(channel: String?, size: Int = 36) {
    val colors = listOf(0xFF5C6BC0, 0xFF26A69A, 0xFFEF6C00, 0xFF8E24AA, 0xFF43A047, 0xFFD81B60, 0xFF1E88E5, 0xFF6D4C41)
    val c = Color(colors[((channel ?: "").hashCode() and 0x7fffffff) % colors.size])
    Box(Modifier.size(size.dp).background(c, CircleShape), contentAlignment = Alignment.Center) {
        Text((channel?.trim()?.firstOrNull() ?: '?').uppercase(), color = Color.White, fontWeight = FontWeight.Medium, fontSize = (size * 0.45).sp)
    }
}

@Composable
fun ytSwitch() = SwitchDefaults.colors(checkedTrackColor = YtBlue, checkedThumbColor = Color.White, checkedBorderColor = YtBlue)

// ---------- formatting ----------

fun fmtTime(ms: Long): String {
    val s = ms / 1000
    return if (s >= 3600) "%d:%02d:%02d".format(s / 3600, s / 60 % 60, s % 60) else "%d:%02d".format(s / 60, s % 60)
}

fun plural(n: Long, one: String, few: String, many: String) =
    if (n % 10 == 1L && n % 100 != 11L) one else if (n % 10 in 2..4 && n % 100 !in 12..14) few else many

fun ago(t: Long?): String {
    t ?: return ""
    val d = (System.currentTimeMillis() - t) / 86_400_000
    return when {
        d <= 0 -> "сегодня"
        d == 1L -> "вчера"
        d < 30 -> "$d ${plural(d, "день", "дня", "дней")} назад"
        d < 365 -> "${d / 30} ${plural(d / 30, "месяц", "месяца", "месяцев")} назад"
        else -> "${d / 365} ${plural(d / 365, "год", "года", "лет")} назад"
    }
}

private fun sorted(list: List<Video>, sort: String) = when (sort) {
    "title" -> list.sortedBy { it.title.lowercase() }
    "channel" -> list.sortedWith(compareBy<Video> { it.meta.channel?.lowercase() ?: "￿" }.thenByDescending { it.meta.addedAt })
    "watched" -> list.sortedByDescending { it.watch.lastWatchedAt ?: 0 }
    else -> list.sortedByDescending { it.meta.addedAt ?: 0 }
}

/** "Похожие" from the local collection: same channel first, then shared title words. */
fun similar(v: Video, all: List<Video>, n: Int = 12): List<Video> {
    val words = v.title.lowercase().split(Regex("\\W+")).filter { it.length >= 4 }.toSet()
    return all.asSequence().filter { it.present && it.key != v.key && !it.finished && it.isShort == v.isShort }
        .map { o -> o to ((if (o.meta.channel == v.meta.channel && v.meta.channel != null) 3 else 0) +
            o.title.lowercase().split(Regex("\\W+")).count { it in words }) }
        .sortedByDescending { it.second }.take(n).map { it.first }.toList()
}

// ---------- root ----------

@Composable
fun Root() {
    val playing by Playback.current.collectAsState()
    val pip by Playback.pip.collectAsState()
    val minimized by Playback.minimized.collectAsState()
    // the small window shows only the picture
    if (pip && playing != null) return Box(Modifier.fillMaxSize().background(Color.Black)) { VideoSurface(Modifier.fillMaxSize()) }
    val all by Lib.videos.collectAsState()
    var tab by rememberSaveable { mutableIntStateOf(0) }
    var search by rememberSaveable { mutableStateOf<String?>(null) }
    val snack = remember { SnackbarHostState() }
    val notice by Lib.notice.collectAsState()

    LaunchedEffect(notice) { notice?.let { snack.showSnackbar(it); Lib.notice.value = null } }

    // a Short always opens in the Shorts tab, like YouTube: that one first, then the rest in our "recommended" order
    val play: (List<Video>, Video) -> Unit = { list, v ->
        if (v.isShort) { search = null; tab = 1; Playback.play(shortsFeed(all, v), v) } else Playback.play(list, v)
    }
    var adding by remember { mutableStateOf<Uri?>(null) }
    val pick = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocumentTree()) { uri ->
        // player mode takes the whole folder at once — no questions
        if (uri != null) { if (Lib.isPlayer) Lib.addStorage(uri, emptyList()) else adding = uri }
    }
    adding?.let { AddStorageDialog(it) { adding = null } }
    val s by Lib.settings.collectAsState()
    val conn by Remote.conn.collectAsState()
    val storageList by Lib.storages.collectAsState()

    // every start of the app asks what this device does today
    val chosen by Lib.roleChosen.collectAsState()
    if (!chosen)
        return RoleScreen(s.role, onServer = { Lib.chooseRole("server"); if (storageList.isEmpty()) pick.launch(null) },
            onClient = { Lib.chooseRole("client") },
            onPlayer = { Lib.chooseRole("player"); if (storageList.none()) pick.launch(null) })
    if (s.role == "client" && conn == null) return ConnectScreen(onBack = { Lib.roleChosen.value = false })

    val short = playing?.isShort == true
    val mini = playing != null && minimized && !short
    // leaving the Shorts tab ends the Short, like YouTube
    val goTab: (Int) -> Unit = { i -> if (tab == 1 && i != 1 && short) Playback.close(); search = null; tab = i }
    LaunchedEffect(short, tab) { if (short && tab != 1) Playback.close() }

    BoxWithConstraints(Modifier.fillMaxSize()) {
        Scaffold(
            snackbarHost = { SnackbarHost(snack) },
            contentWindowInsets = WindowInsets(0),            // each screen's top bar takes the status bar itself
            bottomBar = { if (search == null) TabBar(tab, dark = tab == 1 && short, onTab = goTab) },
        ) { pad ->
            Box(Modifier.padding(pad).fillMaxSize()) {
                when {
                    search != null -> SearchScreen(search!!, { search = it }, { search = null }, all, play)
                    tab == 0 -> HomeScreen(all, play, onSearch = { search = "" }, onAdd = { pick.launch(null) })
                    tab == 1 -> ShortsTab(all, onClose = { goTab(0) })
                    tab == 2 -> HistoryScreen(all, play)
                    else -> SettingsScreen(onAdd = { pick.launch(null) })
                }
            }
        }
        if (mini) FloatingPlayer(maxWidth, maxHeight, aboveTabs = search == null, onClose = Playback::close)
        if (playing != null && !mini && !short) PlayerScreen(all, play, onClose = Playback::close)
    }
    BackHandler(search != null) { search = null }
}

/**
 * YouTube's bottom bar: a thin strip — small icons, tiny labels, filled icon for the open tab. Black over a Short.
 * On a tablet the tabs sit together in the middle.
 */
@Composable
private fun TabBar(tab: Int, dark: Boolean, onTab: (Int) -> Unit) {
    val fg = if (dark) Color.White else MaterialTheme.colorScheme.onSurface
    val wide = wideFeed()
    Column(Modifier.background(if (dark) Color.Black else MaterialTheme.colorScheme.background).navigationBarsPadding()) {
        HorizontalDivider(color = if (dark) Color.White.copy(alpha = 0.12f) else MaterialTheme.colorScheme.outline, thickness = 0.5.dp)
        Row(Modifier.fillMaxWidth().height(48.dp), horizontalArrangement = Arrangement.Center) {
            listOf(Triple(Icons.Filled.Home, Icons.Outlined.Home, "Главная"), Triple(Icons.Filled.Slideshow, Icons.Outlined.Slideshow, "Shorts"),
                Triple(Icons.Filled.History, Icons.Outlined.History, "История"), Triple(Icons.Filled.Settings, Icons.Outlined.Settings, "Настройки"))
                .forEachIndexed { i, (on, off, label) ->
                    Column(Modifier.then(if (wide) Modifier.width(96.dp) else Modifier.weight(1f)).fillMaxHeight()
                        .clickable(remember { androidx.compose.foundation.interaction.MutableInteractionSource() },
                            androidx.compose.material3.ripple(bounded = false, radius = 30.dp)) { onTab(i) },
                        horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.Center) {
                        Icon(if (tab == i) on else off, null, Modifier.size(24.dp), tint = fg)
                        Text(label, fontSize = 10.sp, lineHeight = 12.sp, color = fg, fontWeight = if (tab == i) FontWeight.Medium else FontWeight.Normal)
                    }
                }
        }
    }
}

// ---------- shared pieces ----------

@Composable
fun Thumb(v: Video, modifier: Modifier = Modifier) {
    val better by Thumbs.version.collectAsState()
    val img by produceState<ImageBitmap?>(null, v.key, v.present, better) { value = Thumbs.get(v) }
    Box(modifier.background(MaterialTheme.colorScheme.surfaceVariant)) {
        img?.let { Image(it, null, Modifier.matchParentSize(), contentScale = ContentScale.Crop, alpha = if (v.finished || !v.present) 0.5f else 1f) }
            ?: Icon(Icons.Rounded.Movie, null, Modifier.align(Alignment.Center).size(36.dp), tint = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.4f))
    }
}

@Composable
private fun Badge(text: String, modifier: Modifier) = Text(text, modifier.padding(6.dp).background(Color.Black.copy(alpha = 0.8f), RoundedCornerShape(4.dp))
    .padding(horizontal = 4.dp, vertical = 1.dp), color = Color.White, fontSize = 12.sp, fontWeight = FontWeight.Medium)

@Composable
fun VideoCard(v: Video, onClick: () -> Unit, compact: Boolean = false) {
    // YouTube's phone feed: edge-to-edge picture, then avatar · title · channel, ⋮ on the right
    // several columns (tablet, phone sideways): rounded pictures with gaps, like YouTube there
    val wide = wideFeed()
    Column(Modifier.then(if (compact) Modifier.width(240.dp) else Modifier.fillMaxWidth()).clickable(onClick = onClick).padding(bottom = if (wide) 24.dp else 16.dp)) {
        Box(Modifier.fillMaxWidth().aspectRatio(16 / 9f).clip(RoundedCornerShape(if (compact) 8.dp else if (wide) 12.dp else 0.dp))) {
            Thumb(v, Modifier.matchParentSize())
            v.meta.durationMs?.let { Badge(fmtTime(it), Modifier.align(Alignment.BottomEnd)) }
            when {
                !v.present && v.fileState.state == FileState.AFTER_WATCH -> Badge("Удалено после просмотра", Modifier.align(Alignment.TopStart))
                !v.present && v.fileState.state == FileState.BY_USER -> Badge("Удалено", Modifier.align(Alignment.TopStart))
                v.missing -> Badge("Файла нет на носителе", Modifier.align(Alignment.TopStart))
            }
            // watched = full red bar, like YouTube; started = partial
            val f = if (v.finished) 1f else v.progress
            if (f > 0f) Box(Modifier.align(Alignment.BottomStart).fillMaxWidth().height(4.dp).background(Color(0x99717171))) {
                Box(Modifier.fillMaxWidth(f).fillMaxHeight().background(Red))
            }
        }
        Row(Modifier.padding(start = if (compact || wide) 0.dp else 12.dp, top = 10.dp)) {
            if (!compact) { Avatar(v); Spacer(Modifier.width(12.dp)) }
            Column(Modifier.weight(1f)) {
                Text(v.title, maxLines = 2, overflow = TextOverflow.Ellipsis, fontSize = if (compact) 14.sp else 15.sp, lineHeight = if (compact) 18.sp else 20.sp,
                    fontWeight = FontWeight.Medium, color = MaterialTheme.colorScheme.onSurface)
                Text(listOfNotNull(v.meta.channel, ago(v.meta.addedAt).takeIf { it.isNotEmpty() }).joinToString(" · "),
                    style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
            VideoMenu(v)
        }
    }
}

@Composable
fun VideoMenu(v: Video, tint: Color = MaterialTheme.colorScheme.onSurfaceVariant) {
    var open by remember { mutableStateOf(false) }
    var confirm by remember { mutableStateOf(false) }
    Box {
        IconButton(onClick = { open = true }) { Icon(Icons.Rounded.MoreVert, "Ещё", tint = tint) }
        DropdownMenu(open, { open = false }) {
            if (v.present && !v.finished) DropdownMenuItem({ Text("Отметить просмотренным") }, { open = false; Lib.markWatched(v, true) },
                leadingIcon = { Icon(Icons.Rounded.CheckCircle, null) })
            if (v.finished || v.started) DropdownMenuItem({ Text("Отметить непросмотренным") }, { open = false; Lib.markWatched(v, false) },
                leadingIcon = { Icon(Icons.Rounded.Replay, null) })
            if (v.present) DropdownMenuItem({ Text("Удалить видео") }, { open = false; confirm = true }, leadingIcon = { Icon(Icons.Rounded.Delete, null) })
            if (!v.present) DropdownMenuItem({ Text(if (v.flags.redownload) "Будет скачано снова" else "Скачать снова") },
                { open = false; Lib.requestRedownload(v); Lib.notice.value = "KouTube на ПК скачает видео при следующем подключении к компьютеру" },
                enabled = !v.flags.redownload, leadingIcon = { Icon(Icons.Rounded.Download, null) })
        }
    }
    if (confirm) AlertDialog(onDismissRequest = { confirm = false },
        title = { Text("Удалить видео?") },
        text = { Text("Файл «${v.title}» будет удалён с носителя. Запись останется в истории, и само видео не скачается снова автоматически.") },
        confirmButton = { TextButton({ confirm = false; Lib.deleteByUser(v) }) { Text("Удалить") } },
        dismissButton = { TextButton({ confirm = false }) { Text("Отмена") } })
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun TopBar(title: String, onSearch: (() -> Unit)?, actions: @Composable RowScope.() -> Unit = {}) {
    val busy by Lib.busy.collectAsState()
    Column {
        TopAppBar(title = { if (title == "KouTube") Logo() else Text(title, fontWeight = FontWeight.Bold) }, expandedHeight = 52.dp, actions = {
            actions()
            onSearch?.let { IconButton(it) { Icon(Icons.Outlined.Search, "Поиск") } }
        }, colors = TopAppBarDefaults.topAppBarColors(containerColor = MaterialTheme.colorScheme.background))
        if (busy) LinearProgressIndicator(Modifier.fillMaxWidth().height(2.dp), color = YtRed)
    }
}

/** What this device is doing now — tap to choose again. */
@Composable
private fun ModeChip() {
    val s by Lib.settings.collectAsState()
    val (icon, name) = when (s.role) {
        "client" -> Icons.Outlined.Tablet to "Клиент"
        "player" -> Icons.Outlined.FolderOpen to "Плеер"
        else -> Icons.Outlined.PhoneAndroid to "Сервер"
    }
    Row(Modifier.padding(end = 4.dp).clip(RoundedCornerShape(16.dp)).background(MaterialTheme.colorScheme.surfaceVariant)
        .clickable { Lib.roleChosen.value = false }.padding(horizontal = 10.dp, vertical = 5.dp), verticalAlignment = Alignment.CenterVertically) {
        Icon(icon, null, Modifier.size(16.dp))
        Text(name, Modifier.padding(start = 5.dp), fontSize = 13.sp, fontWeight = FontWeight.Medium)
    }
}

@Composable
private fun SortMenu() {
    var open by remember { mutableStateOf(false) }
    val s by Lib.settings.collectAsState()
    Box {
        IconButton({ open = true }) { Icon(Icons.Rounded.Sort, "Сортировка") }
        DropdownMenu(open, { open = false }) {
            listOf("added" to "По дате добавления", "title" to "По названию", "channel" to "По каналу", "watched" to "По истории просмотра").forEach { (k, t) ->
                DropdownMenuItem({ Text(t) }, { open = false; Lib.updateSettings { it.copy(sort = k) } },
                    trailingIcon = { if (s.sort == k) Icon(Icons.Rounded.Check, null) })
            }
        }
    }
}

@Composable
private fun Empty(title: String, text: String, action: Pair<String, () -> Unit>? = null) {
    Column(Modifier.fillMaxSize().padding(32.dp), verticalArrangement = Arrangement.Center, horizontalAlignment = Alignment.CenterHorizontally) {
        Icon(Icons.Outlined.VideoLibrary, null, Modifier.size(64.dp), tint = MaterialTheme.colorScheme.onSurfaceVariant)
        Spacer(Modifier.height(16.dp))
        Text(title, style = MaterialTheme.typography.titleLarge)
        Spacer(Modifier.height(8.dp))
        Text(text, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        action?.let { (t, f) -> Spacer(Modifier.height(20.dp)); Button(f) { Text(t) } }
    }
}

// ---------- screens ----------

@Composable
fun HomeScreen(all: List<Video>, play: (List<Video>, Video) -> Unit, onSearch: () -> Unit, onAdd: () -> Unit) {
    var filter by rememberSaveable { mutableStateOf("home") }
    val s by Lib.settings.collectAsState()
    val storages by Lib.storages.collectAsState()
    // watched to the end: out of the feed, it lives on in «История»
    val videos = all.filter { it.present && !it.isShort && !it.finished }
    val cont = videos.filter { it.started }.sortedByDescending { it.watch.lastWatchedAt ?: 0 }
    // every video is somewhere in the feed, watched ones at the bottom. A "continue" shelf only pays off in a big
    // library; in a small one it would just repeat the cards below, so started videos simply lead the feed
    val shelf = cont.isNotEmpty() && videos.size > 8
    val fresh = sorted(videos.filter { !it.started && !it.finished }, s.sort)
    val byState = (if (shelf) fresh + sorted(cont, s.sort) else cont + fresh) + sorted(videos.filter { it.finished }, s.sort)
    val list = when (filter) {
        "all" -> sorted(videos, s.sort)
        "continue" -> cont
        "new" -> sorted(videos.filter { it.watch.status == Watch.NEW }, s.sort)
        else -> byState
    }
    val shortKeys = all.filter { it.present && it.isShort }.map { it.key }.toSet()
    val shorts = remember(shortKeys) { shortsFeed(all, null) }        // shuffled once, not on every redraw
    val wide = wideFeed()
    Column {
        TopBar("KouTube", onSearch) { ModeChip(); SortMenu() }
        if (storages.isEmpty() && !Lib.isClient) {
            Empty("Добро пожаловать", "Подключите флешку через OTG-переходник или выберите папку с видео в памяти телефона, например Download/YTDLnis.",
                "Выбрать флешку или папку" to onAdd)
            return
        }
        LazyVerticalGrid(GridCells.Adaptive(330.dp), contentPadding = PaddingValues(start = if (wide) 16.dp else 0.dp, end = if (wide) 16.dp else 0.dp, bottom = 16.dp),
            horizontalArrangement = Arrangement.spacedBy(if (wide) 16.dp else 0.dp)) {
            item(span = { GridItemSpan(maxLineSpan) }) {
                Row(Modifier.horizontalScroll(rememberScrollState()).padding(horizontal = if (wide) 0.dp else 12.dp, vertical = 4.dp).padding(bottom = 8.dp),
                    horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    listOf("home" to "Главная", "all" to "Все видео", "continue" to "Продолжить", "new" to "Не просмотрено").forEach { (k, t) ->
                        Chip(t, filter == k) { filter = k }
                    }
                }
            }
            // the phone with the videos: sharing to the tablet right here, not buried in the settings
            if (filter == "home" && s.role == "server") item(span = { GridItemSpan(maxLineSpan) }) { Column { ShareSection(withDevices = false) } }
            if (filter == "home" && shelf) item(span = { GridItemSpan(maxLineSpan) }) {
                Column {
                    Text("Продолжить просмотр", Modifier.padding(start = 16.dp, top = 8.dp, bottom = 8.dp), style = MaterialTheme.typography.titleMedium)
                    LazyRow(contentPadding = PaddingValues(horizontal = 12.dp), horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                        items(cont, key = { it.storageId + it.key }) { v -> VideoCard(v, { play(cont, v) }, compact = true) }
                    }
                }
            }
            if (list.isEmpty()) item(span = { GridItemSpan(maxLineSpan) }) {
                Text(when {
                    videos.isEmpty() && all.any { it.present && !it.isShort } -> "Всё досмотрено — оно в «Истории»."
                    videos.isEmpty() -> "Видео пока нет. Проверьте папки в настройках."
                    filter == "new" -> "Всё уже начато или просмотрено."
                    else -> "Здесь пусто."
                }, Modifier.padding(24.dp), color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            // YouTube puts a Shorts shelf a couple of videos into the feed
            val withShorts = filter == "home" && shorts.isNotEmpty()
            val split = if (withShorts) minOf(2, list.size) else list.size
            items(list.take(split), key = { it.storageId + it.key }) { v -> VideoCard(v, { play(list, v) }) }
            if (withShorts) item(span = { GridItemSpan(maxLineSpan) }) { ShortsShelf(shorts, wide, play) }
            items(list.drop(split), key = { it.storageId + it.key }) { v -> VideoCard(v, { play(list, v) }) }
        }
    }
}

@Composable
private fun Chip(text: String, selected: Boolean, onClick: () -> Unit) = FilterChip(selected, onClick, { Text(text, fontWeight = FontWeight.Medium) },
    shape = RoundedCornerShape(8.dp), border = null,
    colors = FilterChipDefaults.filterChipColors(containerColor = MaterialTheme.colorScheme.surfaceVariant,
        labelColor = MaterialTheme.colorScheme.onSurface, selectedContainerColor = MaterialTheme.colorScheme.onSurface,
        selectedLabelColor = MaterialTheme.colorScheme.surface))

/** YouTube's Shorts block in the feed: two columns of tall cards on a phone, one row on a tablet. */
@Composable
private fun ShortsShelf(shorts: List<Video>, wide: Boolean, play: (List<Video>, Video) -> Unit) {
    val cols = if (wide) 5 else 2
    Column(Modifier.padding(horizontal = if (wide) 0.dp else 12.dp).padding(bottom = 20.dp)) {
        Row(Modifier.padding(bottom = 10.dp), verticalAlignment = Alignment.CenterVertically) {
            Icon(Icons.Filled.Slideshow, null, tint = YtRed, modifier = Modifier.size(28.dp))
            Text("Shorts", Modifier.padding(start = 10.dp), fontWeight = FontWeight.Bold, fontSize = 20.sp)
        }
        shorts.take(if (wide) cols else 4).chunked(cols).forEach { row ->
            Row(Modifier.fillMaxWidth().padding(bottom = 8.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                row.forEach { v -> ShortCard(v, Modifier.weight(1f)) { play(shorts, v) } }
                repeat(cols - row.size) { Spacer(Modifier.weight(1f)) }
            }
        }
    }
}

@Composable
private fun ShortCard(v: Video, modifier: Modifier, small: Boolean = false, onClick: () -> Unit) {
    Box(modifier.aspectRatio(if (small) 0.62f else 0.6f).clip(RoundedCornerShape(if (small) 8.dp else 10.dp)).clickable(onClick = onClick)) {
        Thumb(v, Modifier.matchParentSize())
        Box(Modifier.matchParentSize().background(Brush.verticalGradient(0.55f to Color.Transparent, 1f to Color.Black.copy(alpha = 0.75f))))
        Text(v.title, Modifier.align(Alignment.BottomStart).padding(if (small) 8.dp else 10.dp), color = Color.White, maxLines = 2, overflow = TextOverflow.Ellipsis,
            fontSize = if (small) 13.sp else 15.sp, fontWeight = FontWeight.SemiBold, lineHeight = if (small) 16.sp else 19.sp)
    }
}

/** Two or more columns fit: a tablet, or a phone held sideways. */
@Composable
fun wideFeed() = LocalConfiguration.current.screenWidthDp >= 600

/**
 * Our "recommended" order for Shorts: unseen first; channels you liked or watched to the end a little higher,
 * disliked ones lower; shuffled, so every visit starts somewhere new.
 */
fun shortsFeed(all: List<Video>, first: Video?): List<Video> {
    val taste = HashMap<String, Double>()
    for (v in all) {
        val c = v.meta.channel ?: continue
        taste[c] = (taste[c] ?: 0.0) + when { v.flags.rating > 0 -> 1.0; v.flags.rating < 0 -> -1.5; v.finished -> 0.3; else -> 0.0 }
    }
    val rnd = java.util.Random()
    return listOfNotNull(first) + all.filter { it.present && it.isShort && it.key != first?.key }
        .map { v -> v to (rnd.nextDouble() + (if (v.finished) 0.0 else 1.5) - (if (v.flags.rating < 0) 3.0 else 0.0) +
            0.4 * (taste[v.meta.channel] ?: 0.0).coerceIn(-2.0, 1.5)) }
        .sortedByDescending { it.second }.map { it.first }
}

/** The Shorts tab is the player itself, like YouTube: open it and a Short is already playing; swipe for the next. */
@Composable
fun ShortsTab(all: List<Video>, onClose: () -> Unit) {
    val cur by Playback.current.collectAsState()
    if (all.none { it.present && it.isShort }) {
        Column { TopBar("Shorts", null); Empty("Shorts пока нет", "Вертикальные короткие видео появятся здесь сами — из папки Shorts или по ориентации кадра.") }
        return
    }
    if (cur?.isShort != true) {
        LaunchedEffect(Unit) { val feed = shortsFeed(all, null); Playback.play(feed, feed.first()) }
        Box(Modifier.fillMaxSize().background(Color.Black))
        return
    }
    ShortsPlayer(onClose)
}

// ---------- history, like YouTube's: by day, videos as rows, Shorts as a strip ----------

private fun seenAt(v: Video) = v.watch.lastWatchedAt ?: v.watch.finishedAt ?: v.fileState.at ?: v.meta.addedAt ?: 0L

private fun dayLabel(t: Long): String {
    fun day(ms: Long) = java.util.Calendar.getInstance().apply {
        timeInMillis = ms; set(java.util.Calendar.HOUR_OF_DAY, 0); set(java.util.Calendar.MINUTE, 0); set(java.util.Calendar.SECOND, 0); set(java.util.Calendar.MILLISECOND, 0)
    }
    val d = day(t); val today = day(System.currentTimeMillis())
    val days = Math.round((today.timeInMillis - d.timeInMillis) / 86_400_000.0)
    val ru = java.util.Locale("ru")
    return when {
        days <= 0 -> "Сегодня"
        days == 1L -> "Вчера"
        days < 7 -> java.text.SimpleDateFormat("EEEE", ru).format(java.util.Date(t)).replaceFirstChar { it.uppercase() }
        d.get(java.util.Calendar.YEAR) == today.get(java.util.Calendar.YEAR) -> java.text.SimpleDateFormat("d MMMM", ru).format(java.util.Date(t))
        else -> java.text.SimpleDateFormat("d MMMM yyyy", ru).format(java.util.Date(t))
    }
}

private fun statusOf(v: Video) = when {
    !v.present && v.fileState.state == FileState.AFTER_WATCH -> "Удалено после просмотра"
    !v.present && v.fileState.state == FileState.BY_USER -> "Удалено"
    !v.present -> "Файла нет на носителе"
    v.finished -> "Просмотрено"
    v.started -> "Остановились на ${fmtTime(v.watch.posMs)}"
    else -> ""
}

@Composable
fun HistoryScreen(all: List<Video>, play: (List<Video>, Video) -> Unit) {
    var q by rememberSaveable { mutableStateOf("") }
    var kind by rememberSaveable { mutableStateOf("all") }
    val needle = unFullwidth(q.trim()).lowercase()
    val seen = all.filter { it.watch.lastWatchedAt != null || it.finished || !it.present }
    val list = seen.filter { when (kind) { "video" -> !it.isShort; "short" -> it.isShort; else -> true } }
        .filter { v -> needle.isEmpty() || listOfNotNull(v.title, v.meta.channel).any { unFullwidth(it).lowercase().contains(needle) } }
        .sortedByDescending(::seenAt)
    val days = list.groupBy { dayLabel(seenAt(it)) }
    val videos = list.filter { it.present && !it.isShort }
    LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 16.dp)) {
        item {
            Text("История", Modifier.statusBarsPadding().padding(start = 16.dp, top = 16.dp, bottom = 12.dp), fontSize = 30.sp, fontWeight = FontWeight.Bold)
            TextField(q, { q = it }, Modifier.fillMaxWidth().padding(horizontal = 16.dp), singleLine = true,
                placeholder = { Text("Искать в истории просмотра") }, leadingIcon = { Icon(Icons.Outlined.Search, null) },
                trailingIcon = { if (q.isNotEmpty()) IconButton({ q = "" }) { Icon(Icons.Rounded.Close, "Очистить") } },
                shape = RoundedCornerShape(24.dp), colors = TextFieldDefaults.colors(focusedIndicatorColor = Color.Transparent, unfocusedIndicatorColor = Color.Transparent,
                    focusedContainerColor = MaterialTheme.colorScheme.surfaceVariant, unfocusedContainerColor = MaterialTheme.colorScheme.surfaceVariant))
            Row(Modifier.horizontalScroll(rememberScrollState()).padding(horizontal = 16.dp, vertical = 12.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                listOf("all" to "Все", "video" to "Видео", "short" to "Shorts").forEach { (k, t) -> Chip(t, kind == k) { kind = k } }
            }
        }
        if (list.isEmpty()) item {
            Text(if (seen.isEmpty()) "Здесь будут просмотренные видео — и те, что уже удалены с носителя." else "Ничего не нашлось.",
                Modifier.padding(24.dp), color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        days.forEach { (day, vids) ->
            item(key = "d$day") { Text(day, Modifier.padding(start = 16.dp, top = 12.dp, bottom = 10.dp), fontSize = 19.sp, fontWeight = FontWeight.Bold) }
            val sh = vids.filter { it.isShort }
            if (sh.isNotEmpty()) item(key = "s$day") {
                LazyRow(Modifier.padding(bottom = 10.dp), contentPadding = PaddingValues(horizontal = 16.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    items(sh, key = { "hs" + it.storageId + it.key }) { v -> ShortCard(v, Modifier.width(118.dp), small = true) { if (v.present) play(listOf(v), v) } }
                }
            }
            items(vids.filter { !it.isShort }, key = { "hv" + it.storageId + it.key }) { v -> HistoryRow(v) { if (v.present) play(videos, v) } }
        }
    }
}

@Composable
private fun HistoryRow(v: Video, onClick: () -> Unit) {
    Row(Modifier.fillMaxWidth().clickable(onClick = onClick).padding(start = 16.dp, top = 6.dp, bottom = 6.dp)) {
        Box(Modifier.width(168.dp).aspectRatio(16 / 9f).clip(RoundedCornerShape(8.dp))) {
            Thumb(v, Modifier.matchParentSize())
            v.meta.durationMs?.let { Badge(fmtTime(it), Modifier.align(Alignment.BottomEnd)) }
            val f = if (v.finished) 1f else v.progress
            if (f > 0f) Box(Modifier.align(Alignment.BottomStart).fillMaxWidth().height(3.dp).background(Color(0x99717171))) {
                Box(Modifier.fillMaxWidth(f).fillMaxHeight().background(Red))
            }
        }
        Column(Modifier.padding(start = 12.dp, top = 2.dp).weight(1f)) {
            Text(v.title, maxLines = 3, overflow = TextOverflow.Ellipsis, fontSize = 15.sp, lineHeight = 20.sp, fontWeight = FontWeight.Medium)
            Text(v.meta.channel ?: "", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1,
                modifier = Modifier.padding(top = 4.dp))
            statusOf(v).takeIf { it.isNotEmpty() }?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant) }
        }
        VideoMenu(v)
    }
}

/** The start of a word that survives its endings: "путешествие" → "путешест", which also finds "путешествия", "путешествую". */
private fun stem(w: String) = if (w.length <= 4) w else w.take(maxOf(4, Math.round(w.length * 0.72f)))

private fun words(s: String) = unFullwidth(s).lowercase().split(Regex("[^\\p{L}\\p{Nd}]+")).filter { it.isNotEmpty() }

/**
 * Search like a person means it: every word of the query (in any form) appears in the title, channel, category
 * or the video's YouTube tags — "путешествие" finds "Путешествия по Японии" and a video tagged #путешествия.
 */
fun matches(v: Video, query: String): Boolean {
    val hay = (listOfNotNull(v.title, v.meta.channel, v.meta.category) + v.meta.tags.orEmpty()).flatMap(::words)
    val whole = unFullwidth(listOfNotNull(v.title, v.meta.channel).joinToString(" ")).lowercase()
    return whole.contains(query) || words(query).all { q -> stem(q).let { st -> hay.any { it.startsWith(st) } } }
}

@Composable
fun SearchScreen(q: String, onQuery: (String) -> Unit, onBack: () -> Unit, all: List<Video>, play: (List<Video>, Video) -> Unit) {
    val needle = unFullwidth(q.trim()).lowercase()
    val found = if (needle.isEmpty()) emptyList() else all.filter { matches(it, needle) }
        .sortedWith(compareByDescending<Video> { it.present }.thenByDescending { it.meta.addedAt ?: 0 })
    val videos = found.filter { !it.isShort }
    val shorts = found.filter { it.isShort && it.present }
    // before typing: the channels in the library, like YouTube's suggestions — it works without internet
    val channels = all.filter { it.present }.groupBy { it.meta.channel }.filterKeys { it != null }.entries.sortedByDescending { it.value.size }
    val focus = remember { androidx.compose.ui.focus.FocusRequester() }
    LaunchedEffect(Unit) { focus.requestFocus() }
    val wide = wideFeed()
    Column(Modifier.statusBarsPadding().navigationBarsPadding().imePadding()) {
        Row(Modifier.fillMaxWidth().padding(end = 12.dp, top = 6.dp, bottom = 6.dp), verticalAlignment = Alignment.CenterVertically) {
            IconButton(onBack) { Icon(Icons.AutoMirrored.Rounded.ArrowBack, "Назад") }
            TextField(q, onQuery, Modifier.weight(1f).height(52.dp).focusRequester(focus), singleLine = true,
                placeholder = { Text("Поиск по библиотеке") },
                trailingIcon = { if (q.isNotEmpty()) IconButton({ onQuery("") }) { Icon(Icons.Rounded.Close, "Очистить") } },
                shape = RoundedCornerShape(26.dp), colors = TextFieldDefaults.colors(focusedIndicatorColor = Color.Transparent, unfocusedIndicatorColor = Color.Transparent,
                    focusedContainerColor = MaterialTheme.colorScheme.surfaceVariant, unfocusedContainerColor = MaterialTheme.colorScheme.surfaceVariant))
        }
        if (needle.isEmpty()) {
            LazyColumn {
                items(channels, key = { "c" + it.key }) { (ch, vids) ->
                    Row(Modifier.fillMaxWidth().clickable { onQuery(ch!!) }.padding(horizontal = 16.dp, vertical = 10.dp), verticalAlignment = Alignment.CenterVertically) {
                        Avatar(vids.first(), 36)
                        Column(Modifier.padding(start = 16.dp).weight(1f)) {
                            Text(ch!!, fontSize = 16.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
                            Text("${vids.size} ${plural(vids.size.toLong(), "видео", "видео", "видео")}", style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                        Icon(Icons.Rounded.NorthWest, null, tint = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                }
            }
            return
        }
        if (found.isEmpty()) { Text("Ничего не нашлось", Modifier.padding(24.dp), color = MaterialTheme.colorScheme.onSurfaceVariant); return }
        LazyVerticalGrid(GridCells.Adaptive(330.dp), contentPadding = PaddingValues(start = if (wide) 16.dp else 0.dp, end = if (wide) 16.dp else 0.dp, bottom = 16.dp),
            horizontalArrangement = Arrangement.spacedBy(if (wide) 16.dp else 0.dp)) {
            if (shorts.isNotEmpty()) item(span = { GridItemSpan(maxLineSpan) }) { ShortsShelf(shorts, wide, play) }
            items(videos, key = { it.storageId + it.key }) { v -> VideoCard(v, { if (v.present) play(videos.filter { it.present }, v) }) }
        }
    }
}

// ---------- settings ----------

@Composable
fun SettingsScreen(onAdd: () -> Unit) {
    val s by Lib.settings.collectAsState()
    val storages by Lib.storages.collectAsState()
    Column {
        TopBar("Настройки", null)
        Column(Modifier.verticalScroll(rememberScrollState()).padding(bottom = 24.dp)) {
            if (Lib.isClient) { Section("Сервер"); ClientSection() } else {
            Section("Где лежат видео")
            storages.forEach { st ->
                ListItem(
                    headlineContent = { Text(st.name) },
                    supportingContent = {
                        Text((if (st.online) "${st.count} видео" else st.error ?: "недоступно") + " · папки: " +
                            st.folders.joinToString { (if (it.path == ".") "вся папка" else it.path) + roleName(it.role) })
                    },
                    leadingContent = { Icon(if (st.online) Icons.Rounded.Usb else Icons.Rounded.UsbOff, null) },
                    trailingContent = { IconButton({ Lib.removeStorage(st.id) }) { Icon(Icons.Rounded.Close, "Отключить") } },
                )
            }
            Row(Modifier.padding(horizontal = 16.dp, vertical = 8.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Button(onAdd) { Icon(Icons.Rounded.Add, null); Spacer(Modifier.width(6.dp)); Text("Флешка или папка") }
                OutlinedButton({ Lib.refresh(force = true) }) { Text("Обновить") }
            }
            Text("Флешка — через OTG-переходник: в окне выбора откройте её и нажмите «Использовать эту папку». " +
                "Можно добавить несколько флешек и папок — всё покажется одной библиотекой.",
                Modifier.padding(horizontal = 16.dp), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)

            Section("Просмотренные видео")
            Column(Modifier.padding(horizontal = 16.dp, vertical = 8.dp)) {
                Text("Здесь ничего не удаляется само: досмотренное помечается и остаётся в истории." + if (Lib.isPlayer) "" else
                    " Когда вы подключите флешку к компьютеру, KouTube там предложит убрать просмотренное и закачать новое.",
                    style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(bottom = 12.dp))
                Text("Досмотрено, если реально проиграно ${s.finishPercent}% видео")
                Slider(s.finishPercent.toFloat(), { v -> Lib.updateSettings { it.copy(finishPercent = v.toInt()) } }, valueRange = 70f..100f, steps = 5,
                    colors = SliderDefaults.colors(thumbColor = YtRed, activeTrackColor = YtRed))
                Text("Перемотка в конец не считается. Видео, которые шли в режиме сна, не засчитываются.",
                    style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }

            if (!Lib.isPlayer) { Section("Раздача по Wi-Fi"); ShareSection() }
            }

            Section("Режим")
            var modes by remember { mutableStateOf(false) }
            val roles = listOf("server" to "Сервер — видео на этом устройстве", "client" to "Клиент — смотрю с другого телефона", "player" to "Просто плеер — папка с видео")
            ListItem(headlineContent = { Text(roles.firstOrNull { it.first == (s.role ?: "server") }?.second ?: "") },
                trailingContent = { TextButton({ modes = true }) { Text("Сменить", color = YtBlue) } })
            if (modes) AlertDialog(onDismissRequest = { modes = false }, title = { Text("Режим") },
                text = {
                    Column {
                        roles.forEach { (k, t) ->
                            Row(Modifier.fillMaxWidth().clickable {
                                modes = false
                                if (s.role != k) {
                                    if (!Lib.isClient) Server.stop(App.instance)
                                    Lib.updateSettings { it.copy(role = k, shareWifi = false) }
                                    Lib.refresh(force = true)
                                }
                            }.padding(vertical = 4.dp), verticalAlignment = Alignment.CenterVertically) {
                                RadioButton((s.role ?: "server") == k, null); Text(t, Modifier.padding(start = 8.dp))
                            }
                        }
                    }
                }, confirmButton = { TextButton({ modes = false }) { Text("Закрыть") } })

            Section("Оформление")
            listOf("system" to "Как в системе", "dark" to "Тёмная", "light" to "Светлая").forEach { (k, t) ->
                Row(Modifier.fillMaxWidth().clickable { Lib.updateSettings { it.copy(theme = k) } }.padding(horizontal = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                    RadioButton(s.theme == k, { Lib.updateSettings { it.copy(theme = k) } }); Text(t)
                }
            }

            Section("О приложении")
            Text("KouTube ${BuildConfig.VERSION_NAME} · ${Lib.deviceName} · ${Lib.deviceId}", Modifier.padding(horizontal = 16.dp),
                style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
}

private fun roleName(r: String) = when (r) { "short" -> " (Shorts)"; "video" -> " (видео)"; else -> "" }

@Composable private fun Section(t: String) = Text(t, Modifier.padding(start = 16.dp, top = 24.dp, bottom = 4.dp),
    fontSize = 16.sp, fontWeight = FontWeight.Bold, color = MaterialTheme.colorScheme.onSurface)

@Composable
private fun Toggle(title: String, sub: String?, value: Boolean, enabled: Boolean = true, onChange: (Boolean) -> Unit) = ListItem(
    headlineContent = { Text(title) }, supportingContent = sub?.let { { Text(it) } },
    trailingContent = { Switch(value, onChange, enabled = enabled, colors = ytSwitch()) },
    modifier = Modifier.clickable(enabled) { onChange(!value) },
)

@Composable
private fun AddStorageDialog(tree: Uri, onDone: () -> Unit) {
    val scope = rememberCoroutineScope()
    var info by remember { mutableStateOf<Lib.Inspect?>(null) }
    var failed by remember { mutableStateOf<String?>(null) }
    val chosen = remember { mutableStateMapOf<String, String>() }   // path → role
    LaunchedEffect(tree) {
        runCatching { Lib.inspect(tree) }.onSuccess { i -> info = i; i.candidates.forEach { chosen[it.path] = if (it.path.contains("short", true)) "short" else "auto" } }
            .onFailure { failed = it.message }
    }
    val i = info
    AlertDialog(onDismissRequest = onDone,
        title = { Text(if (i?.existing != null) "Найдена библиотека" else "Какие папки смотреть?") },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState())) {
                when {
                    failed != null -> Text("Не удалось открыть: $failed")
                    i == null -> LinearProgressIndicator(Modifier.fillMaxWidth())
                    i.existing != null -> Text("«${i.existing.name}» уже настроена: " + i.existing.folders.joinToString { it.path } + ". Подключить её?")
                    i.candidates.isEmpty() -> Text("Видео здесь пока нет. Подключить папку целиком? Новые видео будут появляться сами.")
                    else -> i.candidates.forEach { c ->
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Checkbox(c.path in chosen, { on -> if (on) chosen[c.path] = "auto" else chosen.remove(c.path) })
                            Column(Modifier.weight(1f)) {
                                Text(if (c.path == ".") "Эта папка" else c.path)
                                Text("${c.count} видео", style = MaterialTheme.typography.bodySmall)
                            }
                            if (c.path in chosen) TextButton({ chosen[c.path] = if (chosen[c.path] == "short") "auto" else "short" }) {
                                Text(if (chosen[c.path] == "short") "Shorts" else "Авто")
                            }
                        }
                    }
                }
            }
        },
        confirmButton = {
            TextButton(enabled = i != null && failed == null && (i.existing != null || i.candidates.isEmpty() || chosen.isNotEmpty()), onClick = {
                val folders = i!!.existing?.folders ?: chosen.map { (p, r) -> Folder(p, r) }.ifEmpty { listOf(Folder(".", "auto")) }
                scope.launch { Lib.addStorage(tree, folders) }
                onDone()
            }) { Text("Подключить") }
        },
        dismissButton = { TextButton(onDone) { Text("Отмена") } })
}
