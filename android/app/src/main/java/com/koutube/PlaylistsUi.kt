package com.koutube

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.History
import androidx.compose.material.icons.outlined.Settings
import androidx.compose.material.icons.rounded.Add
import androidx.compose.material.icons.rounded.Delete
import androidx.compose.material.icons.rounded.Download
import androidx.compose.material.icons.rounded.PlayArrow
import androidx.compose.material.icons.rounded.RemoveCircleOutline
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp

private data class Playlist(val id: String, val name: String, val videos: List<Video>)

private fun collectPlaylists(all: List<Video>, names: Map<String, String>): List<Playlist> {
    val entries = all.flatMap { v -> v.item.playlists.orEmpty().mapNotNull { (id, member) ->
        member.name?.let { Triple(id, member, v) }
    } }.groupBy { it.first }
    return (names.keys + entries.keys).map { id ->
        val list = entries[id].orEmpty()
        Playlist(id, list.maxByOrNull { it.second.ts }?.second?.name ?: names.getValue(id),
            list.sortedBy { it.second.order }.map { it.third })
    }.sortedBy { it.name.lowercase() }
}

@Composable
internal fun PersonalLibrary(all: List<Video>, play: (List<Video>, Video) -> Unit, onSettings: () -> Unit) {
    var page by rememberSaveable { mutableStateOf<String?>(null) }
    var creating by remember { mutableStateOf(false) }
    var deleting by remember { mutableStateOf(false) }
    val names by Lib.playlistNames.collectAsState()
    val playlists = remember(all, names) { collectPlaylists(all, names) }
    val playlist = playlists.firstOrNull { it.id == page }
    BackHandler(page != null) { page = null }
    if (page == "history") {
        HistoryScreen(all, play) { page = null }
        return
    }
    if (playlist != null) {
        Column(Modifier.fillMaxSize()) {
            TopBar(playlist.name, null, onBack = { page = null }) {
                IconButton({ deleting = true }) { Icon(Icons.Rounded.Delete, "Удалить плейлист") }
            }
            val playable = playlist.videos.filter { it.present }
            Button({ playable.firstOrNull()?.let { Playback.play(playable, it, fromPlaylist = true) } },
                enabled = playable.isNotEmpty(), modifier = Modifier.padding(horizontal = 16.dp, vertical = 8.dp)) {
                Icon(Icons.Rounded.PlayArrow, null)
                Text("Воспроизвести всё")
            }
            LazyColumn(contentPadding = PaddingValues(bottom = 24.dp)) {
                if (playlist.videos.isEmpty()) item {
                    Text("Добавьте видео через его меню ⋮.", Modifier.padding(16.dp))
                }
                items(playlist.videos, key = { it.storageId + "/" + it.key }) { v ->
                    HistoryRow(v, actions = {
                        if (!v.present && v.meta.ytId != null) IconButton({ Lib.requestRedownload(v) }, enabled = !v.flags.redownload) {
                            Icon(Icons.Rounded.Download, if (v.flags.redownload) "Докачивание в очереди" else "Докачать")
                        }
                        IconButton({ Lib.setPlaylist(listOf(v), playlist.id, null) }) {
                            Icon(Icons.Rounded.RemoveCircleOutline, "Убрать из плейлиста")
                        }
                    }) { if (v.present) Playback.play(playable, v, fromPlaylist = true) }
                }
            }
        }
        if (deleting) AlertDialog(onDismissRequest = { deleting = false }, title = { Text("Удалить плейлист?") },
            text = { Text("Файлы останутся на носителе. Защита от очистки сохранится у видео, которые входят в другие списки.") },
            confirmButton = { TextButton({ deleting = false; page = null; Lib.forgetPlaylist(playlist.id, playlist.videos) }) { Text("Удалить плейлист") } },
            dismissButton = { TextButton({ deleting = false }) { Text("Отмена") } })
        return
    }
    Column(Modifier.fillMaxSize()) {
        TopBar("Библиотека", null) { IconButton(onSettings) { Icon(Icons.Outlined.Settings, "Настройки") } }
        LazyColumn(contentPadding = PaddingValues(bottom = 24.dp)) {
            item {
                ListItem(headlineContent = { Text("История") }, leadingContent = { Icon(Icons.Outlined.History, null) },
                    modifier = Modifier.clickable { page = "history" })
                HorizontalDivider(Modifier.padding(horizontal = 16.dp))
                Row(Modifier.fillMaxWidth().padding(start = 16.dp, end = 8.dp, top = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                    Text("Плейлисты", Modifier.weight(1f), style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.SemiBold)
                    TextButton({ creating = true }) { Icon(Icons.Rounded.Add, null); Text("Создать") }
                }
            }
            if (playlists.isEmpty()) item { Text("Здесь будут ваши плейлисты.", Modifier.padding(16.dp), color = MaterialTheme.colorScheme.onSurfaceVariant) }
            items(playlists, key = { it.id }) { list ->
                ListItem(headlineContent = { Text(list.name, maxLines = 1, overflow = TextOverflow.Ellipsis) },
                    supportingContent = { Text("${list.videos.size} видео") },
                    leadingContent = { list.videos.firstOrNull()?.let { Thumb(it, Modifier.width(88.dp).aspectRatio(16f / 9)) } },
                    modifier = Modifier.clickable { page = list.id })
            }
        }
    }
    if (creating) PlaylistNameDialog({ creating = false }) { title -> creating = false; page = Lib.newPlaylist(title) }
}

@Composable
internal fun SaveToPlaylist(video: Video, onDismiss: () -> Unit) {
    val all by Lib.videos.collectAsState()
    val names by Lib.playlistNames.collectAsState()
    val playlists = remember(all, names) { collectPlaylists(all, names) }
    val latest = all.firstOrNull { it.key == video.key && it.storageId == video.storageId } ?: video
    var creating by remember { mutableStateOf(false) }
    if (creating) PlaylistNameDialog({ creating = false }) { title ->
        Lib.setPlaylist(listOf(latest), Lib.newPlaylist(title), title)
        onDismiss()
    } else AlertDialog(onDismissRequest = onDismiss, title = { Text("Сохранить в плейлист") }, text = {
        Column(Modifier.heightIn(max = 360.dp).verticalScroll(rememberScrollState())) {
            playlists.forEach { list ->
                val checked = latest.item.playlists?.get(list.id)?.name != null
                Row(Modifier.fillMaxWidth().heightIn(min = 48.dp).clickable {
                    Lib.setPlaylist(listOf(latest), list.id, list.name.takeUnless { checked })
                }, verticalAlignment = Alignment.CenterVertically) {
                    Checkbox(checked, onCheckedChange = null)
                    Text(list.name, Modifier.padding(start = 12.dp))
                }
            }
            TextButton({ creating = true }) { Icon(Icons.Rounded.Add, null); Text("Новый плейлист") }
        }
    }, confirmButton = { TextButton(onDismiss) { Text("Готово") } })
}

@Composable
private fun PlaylistNameDialog(onDismiss: () -> Unit, create: (String) -> Unit) {
    var name by rememberSaveable { mutableStateOf("") }
    AlertDialog(onDismissRequest = onDismiss, title = { Text("Новый плейлист") },
        text = { OutlinedTextField(name, { name = it.take(120) }, singleLine = true, label = { Text("Название") }) },
        confirmButton = { TextButton({ create(name.trim()) }, enabled = name.isNotBlank()) { Text("Создать") } },
        dismissButton = { TextButton(onDismiss) { Text("Отмена") } })
}
