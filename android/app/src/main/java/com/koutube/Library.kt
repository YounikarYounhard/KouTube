package com.koutube

import android.content.Intent
import android.media.MediaCodecList
import android.media.MediaMetadataRetriever
import android.net.Uri
import android.os.Build
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withContext
import kotlinx.serialization.Serializable
import kotlinx.serialization.encodeToString
import java.io.File
import java.util.UUID

/** [plain] = a folder added in player mode: nothing is ever written into it, history lives only in the app. */
@Serializable data class StorageRef(val tree: String, val storageId: String, val name: String, val plain: Boolean = false)

@Serializable data class Settings(
    val theme: String = "system",          // system | light | dark
    val finishPercent: Int = 90,
    val sort: String = "added",            // added | title | channel | watched
    val role: String? = null,              // null = not chosen yet | server (videos here) | client (videos on another phone) | player (just a folder)
    val shareWifi: Boolean = false,        // server: give the library to tablets over Wi-Fi
)

@Serializable private data class ScanEntry(val size: Long, val mtime: Long, val key: String)

/** A video file found on a mounted storage, with the subtitle and cover files lying next to it. */
class Found(val doc: Doc, val path: String, val subs: List<Pair<Sub, Doc>>, val thumb: Doc?)

data class Video(val key: String, val storageId: String, val item: Item, val found: Found?, val remotePresent: Boolean = false) {
    val meta get() = item.meta ?: Meta()
    val watch get() = item.watch ?: Watch()
    val fileState get() = item.file ?: FileState()
    val flags get() = item.flags ?: Flags()
    val present get() = found != null || remotePresent
    val title get() = meta.title ?: found?.doc?.name ?: key
    val isShort get() = meta.kind == "short"
    val finished get() = watch.status == Watch.FINISHED
    val started get() = watch.status == Watch.STARTED && watch.posMs > 0
    val progress get() = meta.durationMs?.takeIf { it > 0 }?.let { (watch.posMs.toFloat() / it).coerceIn(0f, 1f) } ?: 0f
    /** Gone without the app deleting it — never treated as "watched" (FORMAT.md §5.2). */
    val missing get() = !present && fileState.state == FileState.PRESENT
}

data class StorageView(val id: String, val name: String, val online: Boolean, val count: Int, val error: String?, val folders: List<Folder>)

private class Mount(val ref: StorageRef) {
    val saf = Saf(Uri.parse(ref.tree))
    var storage = StorageFile(storageId = ref.storageId, name = ref.name)
    val mine = HashMap<String, Item>()
    var others: List<DeviceFile> = emptyList()
    val merged = HashMap<String, Item>()
    var docs: Map<String, Found> = emptyMap()
    var online = false
    var error: String? = null
    var dirty = false
    var flushJob: Job? = null
    var saveJob: Job? = null
    val dir get() = File(App.instance.filesDir, "s/${ref.storageId}").apply { mkdirs() }

    fun remerge(key: String? = null) {
        val all = others + DeviceFile(deviceId = Lib.deviceId, items = mine)
        if (key == null) { merged.clear(); merged.putAll(merge(all)) }
        else merge(all.map { f -> f.copy(items = f.items.filterKeys { it == key }) })[key]?.let { merged[key] = it }
    }
}

object Lib {
    private val ctx get() = App.instance
    internal val io = Dispatchers.IO.limitedParallelism(1)      // every Mount is touched only here
    private val probeIo = Dispatchers.IO.limitedParallelism(1)  // slow MediaMetadataRetriever work
    val scope = CoroutineScope(SupervisorJob() + io)
    private val prefs = ctx.getSharedPreferences("koutube", 0)

    val deviceId: String = prefs.getString("deviceId", null)
        ?: ("android-" + UUID.randomUUID().toString().take(8)).also { prefs.edit().putString("deviceId", it).apply() }
    val deviceName: String = Build.MODEL

    val settings = MutableStateFlow(runCatching { json.decodeFromString<Settings>(prefs.getString("settings", "")!!) }.getOrDefault(Settings()))
    val videos = MutableStateFlow<List<Video>>(emptyList())
    val storages = MutableStateFlow<List<StorageView>>(emptyList())
    val busy = MutableStateFlow(false)
    val notice = MutableStateFlow<String?>(null)

    private val mounts = LinkedHashMap<String, Mount>()
    val isClient get() = settings.value.role == "client"
    val isPlayer get() = settings.value.role == "player"
    private var remote: List<Video> = emptyList()
    private val remoteCache get() = File(ctx.filesDir, "remote.json")
    private val caps by lazy { readCaps() }
    private var lastRefresh = 0L

    private val refsFile get() = File(ctx.filesDir, "refs.json")

    init {
        scope.launch {
            runCatching { json.decodeFromString<List<StorageRef>>(refsFile.readText()) }.getOrDefault(emptyList())
                .forEach { mounts[it.storageId] = Mount(it).also(::loadLocal) }
            runCatching { json.decodeFromString<RemoteLibrary>(remoteCache.readText()) }.getOrNull()?.let(::setRemote)
            publish()
            refreshNow()
        }
    }

    /** The mode is asked on every start of the app (process): false until the user picks one. */
    val roleChosen = MutableStateFlow(false)

    /**
     * Picking a mode makes it work at once: a server shares over Wi-Fi straight away (no switch to flip off and on),
     * a player or a client does not share.
     */
    fun chooseRole(role: String) {
        val was = settings.value.role
        updateSettings { it.copy(role = role, shareWifi = role == "server") }
        if (role == "server") { if (!Server.running.value) Server.start(ctx) } else if (Server.running.value) Server.stop(ctx)
        roleChosen.value = true
        if (was != role) refresh(force = true)
    }

    fun updateSettings(f: (Settings) -> Settings) {
        settings.value = f(settings.value)
        prefs.edit().putString("settings", json.encodeToString(settings.value)).apply()
    }

    // ---------- storages ----------

    /** Rescan every storage; cheap no-op if one ran moments ago (called from onResume). */
    fun refresh(force: Boolean = false) = scope.launch {
        if (force || System.currentTimeMillis() - lastRefresh > 30_000) refreshNow()
    }

    private suspend fun refreshNow() {
        if (isClient) { Remote.useHomeNetwork(); return refreshRemote() }
        busy.value = true
        lastRefresh = System.currentTimeMillis()
        for (m in mounts.values.toList()) { loadDrive(m); publish() }
        busy.value = false
        probe()
    }

    data class Candidate(val path: String, val count: Int)
    class Inspect(val existing: StorageFile?, val candidates: List<Candidate>)

    /** What a freshly picked folder holds: an existing library, or folders with videos to choose from. */
    suspend fun inspect(tree: Uri): Inspect = withContext(io) {
        val saf = Saf(tree)
        val existing = runCatching {
            saf.child(saf.rootId, ".koutube")?.let { ml -> newestStorage(saf.readCandidates(ml.id, "storage.json")) }
        }.getOrNull()
        val top = saf.list(saf.rootId)
        val c = ArrayList<Candidate>()
        top.count { !it.isDir && isVideoName(it.name) }.takeIf { it > 0 }?.let { c += Candidate(".", it) }
        for (d in top.filter { it.isDir && !it.name.startsWith(".") }) {
            val n = runCatching { saf.list(d.id).count { !it.isDir && isVideoName(it.name) } }.getOrDefault(0)
            if (n > 0) c += Candidate(d.name, n)
        }
        Inspect(existing, c)
    }

    fun addStorage(tree: Uri, folders: List<Folder>) = scope.launch {
        runCatching {
            ctx.contentResolver.takePersistableUriPermission(tree,
                Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION)
            val saf = Saf(tree)
            if (isPlayer) {
                // player mode: the folder stays untouched — no .koutube, the description is kept in the app
                val m = mounts.values.firstOrNull { it.ref.tree == tree.toString() } ?: run {
                    val name = displayName(saf)
                    val sf = StorageFile(storageId = UUID.randomUUID().toString(), name = name, updatedAt = System.currentTimeMillis(), folders = listOf(Folder(".", "auto")))
                    Mount(StorageRef(tree.toString(), sf.storageId, name, plain = true)).also {
                        it.storage = sf; File(it.dir, "storage.json").writeText(json.encodeToString(sf)); mounts[sf.storageId] = it
                    }
                }
                saveRefs(); loadDrive(m); publish(); probe()
                return@runCatching
            }
            val ml = saf.dir(saf.rootId, ".koutube")
            val sf = newestStorage(saf.readCandidates(ml, "storage.json")) ?: StorageFile(
                storageId = UUID.randomUUID().toString(), name = displayName(saf), updatedAt = System.currentTimeMillis(),
                folders = folders,
                downloadTo = mapOf("video" to (folders.firstOrNull { it.role != "short" }?.path ?: "."),
                    "short" to (folders.firstOrNull { it.role == "short" }?.path ?: "Shorts")),
            ).also { saf.writeAtomic(ml, "storage.json", seal(json.encodeToString(it)).toByteArray()) }
            val ref = StorageRef(tree.toString(), sf.storageId, sf.name ?: "Хранилище")
            val m = mounts[sf.storageId]?.takeIf { it.ref.tree == ref.tree } ?: Mount(ref).also { mounts[sf.storageId] = it; loadLocal(it) }
            saveRefs()
            loadDrive(m); publish(); probe()
        }.onFailure { notice.value = "Не удалось подключить папку: ${it.message}" }
    }

    fun removeStorage(id: String) = scope.launch {
        val m = mounts.remove(id) ?: return@launch
        runCatching { ctx.contentResolver.releasePersistableUriPermission(Uri.parse(m.ref.tree),
            Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION) }
        saveRefs(); publish()
    }

    private fun displayName(saf: Saf) = runCatching {
        ctx.contentResolver.query(saf.uri(saf.rootId), arrayOf(android.provider.DocumentsContract.Document.COLUMN_DISPLAY_NAME), null, null, null)
            ?.use { if (it.moveToFirst()) it.getString(0) else null }
    }.getOrNull() ?: "Хранилище"

    private fun saveRefs() = refsFile.writeText(json.encodeToString(mounts.values.map { it.ref }))

    private fun newestStorage(bodies: List<String>) = bodies.mapNotNull { runCatching { json.decodeFromString<StorageFile>(it) }.getOrNull() }
        .filter { it.schema == SCHEMA }.maxByOrNull { it.updatedAt }

    private fun newestDevice(bodies: List<String>) = bodies.mapNotNull { runCatching { json.decodeFromString<DeviceFile>(it) }.getOrNull() }
        .filter { it.schema == SCHEMA }.maxByOrNull { it.writtenAt }

    // ---------- local copy (survives a pulled stick) ----------

    private fun loadLocal(m: Mount) {
        runCatching { json.decodeFromString<StorageFile>(File(m.dir, "storage.json").readText()) }.getOrNull()?.let { m.storage = it }
        runCatching { unseal(File(m.dir, "mine.json").readText())?.let { json.decodeFromString<DeviceFile>(it) } }.getOrNull()
            ?.let { m.mine.putAll(it.items) }
        m.others = File(m.dir, "others").listFiles()?.mapNotNull {
            runCatching { json.decodeFromString<DeviceFile>(it.readText()) }.getOrNull()
        } ?: emptyList()
        m.remerge()
    }

    private fun saveLocal(m: Mount) {
        val f = File(m.dir, "mine.json")
        val tmp = File(m.dir, "mine.json.tmp")
        tmp.writeText(seal(json.encodeToString(mineFile(m))))
        tmp.renameTo(f)
    }

    private fun mineFile(m: Mount) = DeviceFile(deviceId = deviceId, deviceName = deviceName, kind = "android",
        app = "KouTube ${BuildConfig.VERSION_NAME}", writtenAt = System.currentTimeMillis(), caps = caps, items = HashMap(m.mine))

    // ---------- the storage itself ----------

    private fun loadDrive(m: Mount) {
        try {
            if (m.ref.plain) { m.saf.list(m.saf.rootId); m.docs = scan(m); m.online = true; m.error = null; return }
            val ml = m.saf.dir(m.saf.rootId, ".koutube")
            newestStorage(m.saf.readCandidates(ml, "storage.json"))?.takeIf { it.updatedAt >= m.storage.updatedAt }?.let {
                m.storage = it; File(m.dir, "storage.json").writeText(json.encodeToString(it))
            }
            val dv = m.saf.dir(ml, "devices")
            val ids = m.saf.list(dv).map { it.name.removeSuffix(".tmp").removeSuffix(".bak") }
                .filter { it.endsWith(".json") }.map { it.removeSuffix(".json") }.toSet()
            val others = ArrayList<DeviceFile>()
            val cache = File(m.dir, "others").apply { mkdirs() }
            for (id in ids) {
                val f = newestDevice(m.saf.readCandidates(dv, "$id.json")) ?: continue
                if (id == deviceId) {
                    // our own file on the stick: restores history after a reinstall; local wins ties
                    merge(listOf(f, DeviceFile(deviceId = deviceId, items = m.mine))).let { m.mine.clear(); m.mine.putAll(it) }
                } else { others += f; File(cache, "$id.json").writeText(json.encodeToString(f)) }
            }
            m.others = others
            m.remerge()
            m.docs = scan(m)
            m.online = true; m.error = null
        } catch (e: Exception) {
            m.online = false; m.docs = emptyMap()
            m.error = if (e is SecurityException) "нет доступа — подключите заново" else "недоступно — флешка извлечена?"
        }
    }

    private fun scan(m: Mount): Map<String, Found> {
        val cacheFile = File(m.dir, "scan.json")
        val cache = runCatching { json.decodeFromString<Map<String, ScanEntry>>(cacheFile.readText()) }.getOrDefault(emptyMap())
        val next = HashMap<String, ScanEntry>()
        val found = HashMap<String, Found>()
        fun walk(dirId: String, path: String, role: String, depth: Int) {
            val docs = m.saf.list(dirId)
            val byName = docs.associateBy { it.name }
            for (d in docs) {
                val rel = if (path == ".") d.name else "$path/${d.name}"
                if (d.isDir) { if (depth < 2 && !d.name.startsWith(".")) walk(d.id, rel, role, depth + 1); continue }
                if (!isVideoName(d.name)) continue
                val c = cache[rel]
                val key = if (c != null && c.size == d.size && c.mtime == d.mtime) c.key else m.saf.fileKey(d)
                next[rel] = ScanEntry(d.size, d.mtime, key)
                if (key in found) continue
                val base = d.name.substringBeforeLast('.')
                val subs = docs.filter { it.name.startsWith("$base.") && (it.name.endsWith(".vtt") || it.name.endsWith(".srt")) }.map {
                    val lang = it.name.removePrefix("$base.").substringBeforeLast('.')
                    Sub(lang, if (path == ".") it.name else "$path/${it.name}") to it
                }
                val thumb = listOf("jpg", "jpeg", "png", "webp").firstNotNullOfOrNull { byName["$base.$it"] }
                found[key] = Found(d, rel, subs, thumb)
                val have = m.merged[key]?.meta
                if (have == null) {
                    val (channel, title) = parseName(d.name)
                    putMeta(m, key, Meta(title = title, channel = channel, kind = detectKind(role, null, null, null, null),
                        size = d.size, path = rel, subs = subs.map { it.first }.ifEmpty { null }, addedAt = d.mtime, source = "scan"))
                } else if (have.path != rel || (subs.isNotEmpty() && have.subs == null)) {
                    putMeta(m, key, have.copy(path = rel, subs = subs.map { it.first }.ifEmpty { have.subs }))
                }
            }
        }
        for (folder in m.storage.folders) m.saf.resolveDir(folder.path)?.let { walk(it, folder.path, folder.role, 0) }
        cacheFile.writeText(json.encodeToString(next))
        return found
    }

    /** Fills duration/size/orientation the file name cannot tell, one file at a time in the background. */
    private fun probe() = scope.launch {
        val todo = mounts.values.flatMap { m -> m.docs.filter { (k, _) -> m.merged[k]?.meta?.durationMs == null }.map { m to it } }
        for ((i, p) in todo.withIndex()) {
            val (m, e) = p
            val r = withContext(probeIo) { runCatching { readMeta(m.saf.uri(e.value.doc.id)) }.getOrNull() } ?: continue
            val have = m.merged[e.key]?.meta ?: continue
            val role = m.storage.folders.firstOrNull { e.value.path == it.path || e.value.path.startsWith(it.path + "/") || it.path == "." }?.role ?: "auto"
            putMeta(m, e.key, have.copy(durationMs = r.durationMs, width = r.width, height = r.height,
                title = r.title ?: have.title, channel = r.channel ?: have.channel,
                kind = detectKind(role, have.url, r.width, r.height, r.durationMs)))
            if (i % 10 == 9) publish()
        }
        if (todo.isNotEmpty()) publish()
    }

    private fun readMeta(uri: Uri): Meta = MediaMetadataRetriever().run {
        try {
            setDataSource(ctx, uri)
            val w = extractMetadata(MediaMetadataRetriever.METADATA_KEY_VIDEO_WIDTH)?.toIntOrNull()
            val h = extractMetadata(MediaMetadataRetriever.METADATA_KEY_VIDEO_HEIGHT)?.toIntOrNull()
            val rot = extractMetadata(MediaMetadataRetriever.METADATA_KEY_VIDEO_ROTATION)?.toIntOrNull() ?: 0
            val (ww, hh) = if (rot == 90 || rot == 270) h to w else w to h
            Meta(durationMs = extractMetadata(MediaMetadataRetriever.METADATA_KEY_DURATION)?.toLongOrNull(), width = ww, height = hh,
                title = extractMetadata(MediaMetadataRetriever.METADATA_KEY_TITLE)?.takeIf { it.isNotBlank() },
                channel = extractMetadata(MediaMetadataRetriever.METADATA_KEY_ARTIST)?.takeIf { it.isNotBlank() })
        } finally { release() }
    }

    private fun readCaps(): Caps {
        val out = LinkedHashMap<Pair<String, Boolean>, Decoder>()
        for (info in MediaCodecList(MediaCodecList.REGULAR_CODECS).codecInfos) {
            if (info.isEncoder) continue
            val hw = if (Build.VERSION.SDK_INT >= 29) info.isHardwareAccelerated
                     else !info.name.startsWith("OMX.google.") && !info.name.startsWith("c2.android.")
            for (t in info.supportedTypes.filter { it.startsWith("video/") }) {
                val vc = runCatching { info.getCapabilitiesForType(t).videoCapabilities }.getOrNull() ?: continue
                val d = Decoder(t, hw, vc.supportedWidths.upper, vc.supportedHeights.upper)
                val k = t to hw
                if ((out[k]?.maxW ?: 0) < d.maxW) out[k] = d
            }
        }
        return Caps(out.values.toList())
    }

    // ---------- edits (writer rule, FORMAT.md §6.4) ----------

    private fun putMeta(m: Mount, key: String, meta: Meta) =
        edit(m, key) { it.copy(meta = meta.copy(ts = nextTs(m.merged[key]?.meta?.ts))) }

    private fun edit(m: Mount, key: String, f: (Item) -> Item) {
        m.mine[key] = f(m.mine[key] ?: Item())
        m.remerge(key)
        m.dirty = true
        m.saveJob?.cancel()
        m.saveJob = scope.launch { delay(10_000); saveLocal(m) }
    }

    fun setWatch(v: Video, w: (Watch) -> Watch) = scope.launch {
        if (isClient) return@launch editRemote(v) { it.copy(watch = w(it.watch ?: Watch()).copy(ts = nextTs(it.watch?.ts))) }
        val m = mounts[v.storageId] ?: return@launch
        val cur = m.merged[v.key]?.watch ?: Watch()
        edit(m, v.key) { it.copy(watch = w(cur).copy(ts = nextTs(cur.ts))) }
        publish()
    }

    fun setFlags(v: Video, f: (Flags) -> Flags) = scope.launch {
        if (isClient) return@launch editRemote(v) { it.copy(flags = f(it.flags ?: Flags()).copy(ts = nextTs(it.flags?.ts))) }
        val m = mounts[v.storageId] ?: return@launch
        val cur = m.merged[v.key]?.flags ?: Flags()
        edit(m, v.key) { it.copy(flags = f(cur).copy(ts = nextTs(cur.ts))) }
        publish(); flushSoon(m)
    }

    fun markWatched(v: Video, watched: Boolean) {
        setWatch(v) { if (watched) it.copy(status = Watch.FINISHED, finishedAt = System.currentTimeMillis())
                      else Watch() }
        if (watched) setFlags(v) { it.copy(noAutoRedownload = true) }
        scope.launch { mounts[v.storageId]?.let(::flushSoon) }
    }

    fun rate(v: Video, r: Int) = setFlags(v) { it.copy(rating = r) }

    fun requestRedownload(v: Video) = setFlags(v) { it.copy(noAutoRedownload = false, redownload = true) }

    /** Write every changed storage now: called on pause, leaving the player, finishing a video. */
    fun flushAll() = scope.launch { if (isClient) Remote.flush() else mounts.values.forEach { flush(it) } }

    private fun flushSoon(m: Mount) {
        m.flushJob?.cancel()
        m.flushJob = scope.launch { delay(3_000); flush(m) }
    }

    private fun flush(m: Mount) {
        m.saveJob?.cancel()
        saveLocal(m)
        if (!m.dirty || !m.online || m.ref.plain) return
        try {
            val dv = m.saf.dir(m.saf.dir(m.saf.rootId, ".koutube"), "devices")
            m.saf.writeAtomic(dv, "$deviceId.json", seal(json.encodeToString(mineFile(m))).toByteArray())
            m.dirty = false
        } catch (e: Exception) {
            m.online = false; m.error = "недоступно — флешка извлечена?"; m.docs = emptyMap(); publish()
        }
    }

    // ---------- deleting (FORMAT.md §9) ----------

    fun deleteByUser(v: Video) = scope.launch {
        if (!isClient) return@launch deleteNow(v, FileState.BY_USER)
        Remote.edit(RemoteEdit(v.storageId, v.key, delete = true)); Remote.flush(); refreshRemote()
    }

    private suspend fun deleteNow(v: Video, reason: String) {
        val m = mounts[v.storageId] ?: return
        while (Server.isStreaming(v.key)) delay(5_000)      // a tablet is still reading it
        val f = m.docs[v.key] ?: return
        if (m.merged[v.key] == null) return
        val ok = runCatching { m.saf.fileKey(f.doc) == v.key }.getOrDefault(false)
        if (!ok) { notice.value = "Файл не удалён: он изменился или недоступен"; return }
        if (v.meta.thumb == null && !m.ref.plain) keepThumb(m, v)
        if (!runCatching { m.saf.delete(f.doc.id) }.getOrDefault(false)) { notice.value = "Не удалось удалить файл"; return }
        (f.subs.map { it.second } + listOfNotNull(f.thumb)).forEach { runCatching { m.saf.delete(it.id) } }
        val now = System.currentTimeMillis()
        val cur = m.merged[v.key]
        edit(m, v.key) { it.copy(file = FileState(nextTs(cur?.file?.ts), reason, now)) }
        edit(m, v.key) { it.copy(flags = (cur?.flags ?: Flags()).copy(ts = nextTs(cur?.flags?.ts), noAutoRedownload = true)) }
        m.docs = m.docs - v.key
        publish(); flush(m)
    }

    /** Copies our small cached cover into .koutube/thumbs so the history keeps a picture. */
    private suspend fun keepThumb(m: Mount, v: Video) = runCatching {
        val bytes = Thumbs.fileFor(v)?.readBytes() ?: return@runCatching
        val name = (v.meta.ytId ?: v.key) + ".jpg"
        val td = m.saf.dir(m.saf.dir(m.saf.rootId, ".koutube"), "thumbs")
        m.saf.write(td, name, bytes)
        putMeta(m, v.key, (m.merged[v.key]?.meta ?: Meta()).copy(thumb = "thumbs/$name"))
    }

    // ---------- reads for the UI and the player ----------

    fun uriOf(v: Video): Uri? = if (isClient) runCatching { Remote.videoUri(v) }.getOrNull() else v.found?.let { mounts[v.storageId]?.saf?.uri(it.doc.id) }
    fun uriOf(storageId: String, docId: String): Uri? = mounts[storageId]?.saf?.uri(docId)

    /** A kept cover in .koutube/thumbs (for deleted videos), if the storage is mounted. */
    fun storageThumb(v: Video): Uri? = v.meta.thumb?.let { storageFile(v.storageId, it) }

    /** A file inside .koutube (thumbs/…, avatars/…) of a mounted storage. */
    fun storageFile(storageId: String, rel: String): Uri? {
        if (isClient) return null
        val m = mounts[storageId]?.takeIf { it.online && !it.ref.plain } ?: return null

        return runCatching {
            val td = m.saf.child(m.saf.rootId, ".koutube")?.let { m.saf.child(it.id, rel.substringBeforeLast('/')) } ?: return null
            m.saf.child(td.id, rel.substringAfterLast('/'))?.let { m.saf.uri(it.id) }
        }.getOrNull()
    }

    /** The top comments the PC saved for this video (empty if none). */
    suspend fun comments(v: Video): List<Comment> = withContext(Dispatchers.IO) {
        val rel = v.meta.comments ?: return@withContext emptyList()
        runCatching {
            val text = if (isClient) Remote.get(Remote.commentsUrl(v)).let { (st, b) -> if (st == 200) String(b) else null }
                       else storageFile(v.storageId, rel)?.let { u -> ctx.contentResolver.openInputStream(u)!!.use { String(it.readBytes()) } }
            text?.let { json.decodeFromString<List<Comment>>(it) }
        }.getOrNull() ?: emptyList()
    }

    private fun publish() {
        if (isClient) { videos.value = remote; storages.value = emptyList(); return }
        val out = ArrayList<Video>()
        for (m in mounts.values) {
            for ((k, item) in m.merged) out += Video(k, m.ref.storageId, item, m.docs[k])
            for ((k, f) in m.docs) if (k !in m.merged) out += Video(k, m.ref.storageId, Item(), f)
        }
        videos.value = out
        storages.value = mounts.values.map { StorageView(it.ref.storageId, it.storage.name ?: it.ref.name, it.online, it.docs.size, it.error, it.storage.folders) }
    }

    // ---------- client mode: the library is on another phone ----------

    private fun setRemote(r: RemoteLibrary) {
        remote = r.items.map { Video(it.key, it.storageId, it.item, null, remotePresent = it.present) }
    }

    private suspend fun refreshRemote() {
        if (Remote.conn.value == null) { remote = emptyList(); publish(); return }
        busy.value = !offline
        Remote.flush()
        Remote.library().onSuccess { r ->
            offline = false
            setRemote(r); publish()
            remoteCache.writeText(json.encodeToString(r))
        }.onFailure {
            if (!offline) notice.value = it.message
            offline = true
            // the phone is off or out of reach: keep knocking, so its fresh progress replaces the cached copy
            // as soon as it's back — playing from the stale copy would overwrite newer progress
            if (retry?.isActive != true) retry = scope.launch { delay(5000); retry = null; refreshNow() }
        }
        busy.value = false
    }
    private var retry: Job? = null
    private var offline = false

    private fun editRemote(v: Video, f: (Item) -> Item) {
        val cur = remote.firstOrNull { it.key == v.key && it.storageId == v.storageId } ?: v
        val item = f(cur.item)
        remote = remote.map { if (it === cur) it.copy(item = item) else it }
        publish()
        Remote.edit(RemoteEdit(v.storageId, v.key, watch = item.watch.takeIf { it != cur.item.watch }, flags = item.flags.takeIf { it != cur.item.flags }))
    }

    // ---------- server side: what the tablet asks for ----------

    fun snapshot() = RemoteLibrary(deviceName, videos.value.map { RemoteItem(it.key, it.storageId, it.item, it.present) })

    fun fileFor(storageId: String, key: String): Triple<Uri, Long, String>? = runBlocking(io) {
        val m = mounts[storageId] ?: return@runBlocking null
        val f = m.docs[key] ?: return@runBlocking null
        Triple(m.saf.uri(f.doc.id), f.doc.size, f.doc.name)
    }

    fun subFor(storageId: String, key: String, lang: String): Uri? = runBlocking(io) {
        val m = mounts[storageId] ?: return@runBlocking null
        m.docs[key]?.subs?.firstOrNull { it.first.lang == lang }?.let { m.saf.uri(it.second.id) }
    }

    /** A tablet's progress, flags or delete request: newest wins, exactly like a device file would. */
    fun applyRemote(e: RemoteEdit) = scope.launch {
        val m = mounts[e.storageId] ?: return@launch
        val before = m.merged[e.key]
        e.watch?.let { w -> if (w.ts > (before?.watch?.ts ?: 0)) edit(m, e.key) { it.copy(watch = w) } }
        e.flags?.let { f -> if (f.ts > (before?.flags?.ts ?: 0)) edit(m, e.key) { it.copy(flags = f) } }
        publish()
        val v = Video(e.key, e.storageId, m.merged[e.key] ?: Item(), m.docs[e.key])
        if (e.delete) deleteNow(v, FileState.BY_USER)
        flushSoon(m)
    }
}
