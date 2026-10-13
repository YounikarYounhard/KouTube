package com.koutube

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.net.wifi.WifiManager
import android.os.Build
import android.os.IBinder
import android.os.PowerManager
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.runBlocking
import kotlinx.serialization.Serializable
import kotlinx.serialization.encodeToString
import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.io.FileInputStream
import java.io.InputStream
import java.io.OutputStream
import java.net.Inet4Address
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.NetworkInterface
import java.net.ServerSocket
import java.net.Socket
import java.net.URLDecoder
import java.nio.ByteBuffer
import java.security.SecureRandom
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicInteger
import kotlin.concurrent.thread

@Serializable data class RemoteItem(val key: String, val storageId: String, val item: Item, val present: Boolean, val ext: String = "")
@Serializable data class RemoteLibrary(val serverName: String, val items: List<RemoteItem>)
@Serializable data class PairRequest(val code: String, val deviceId: String, val name: String)
@Serializable data class PairReply(val token: String, val serverName: String)
@Serializable data class RemoteEdit(val storageId: String, val key: String, val watch: Watch? = null, val flags: Flags? = null, val delete: Boolean = false)

/**
 * The phone with the stick as a server for a tablet on the same Wi-Fi (or the phone's hotspot).
 * Plain HTTP on the local network only: a pairing code shown on this phone gives the tablet a
 * token, and nothing is served without it. Video goes out with Range support, so the tablet seeks
 * without downloading the file and keeps no copy. Runs only while switched on, in a foreground
 * service with a Stop button, and switches itself off after 30 idle minutes.
 */
object Server {
    const val PORT = 8790
    const val TYPE = "_koutube._tcp."
    val running = MutableStateFlow(false)
    val code = MutableStateFlow("")
    val devices = MutableStateFlow<Map<String, String>>(emptyMap())      // token → device name
    val port = MutableStateFlow(PORT)

    private val prefs get() = App.instance.getSharedPreferences("koutube-server", 0)
    private var socket: ServerSocket? = null
    private var nsd: NsdManager.RegistrationListener? = null
    private val streams = ConcurrentHashMap<String, AtomicInteger>()     // video key → open streams
    private val open = AtomicInteger(0)
    @Volatile private var lastRequest = 0L
    private var wrongCodes = 0
    private var wrongSince = 0L
    private var wake: PowerManager.WakeLock? = null
    private var wifi: WifiManager.WifiLock? = null

    init {
        devices.value = runCatching { json.decodeFromString<Map<String, String>>(prefs.getString("tokens", "{}")!!) }.getOrDefault(emptyMap())
    }

    fun start(ctx: Context) {
        val i = Intent(ctx, ServerService::class.java)
        if (Build.VERSION.SDK_INT >= 26) ctx.startForegroundService(i) else ctx.startService(i)
    }

    fun stop(ctx: Context) { ctx.stopService(Intent(ctx, ServerService::class.java)) }

    fun forget(token: String) {
        devices.value = devices.value - token
        prefs.edit().putString("tokens", json.encodeToString(devices.value)).apply()
    }

    fun isStreaming(key: String) = (streams[key]?.get() ?: 0) > 0

    /** Where tablets can reach us: Wi-Fi and hotspot addresses (the hotspot one changes between Android versions). */
    fun addresses(): List<String> = runCatching {
        NetworkInterface.getNetworkInterfaces().toList().filter { it.isUp && !it.isLoopback }.flatMap { n ->
            n.inetAddresses.toList().filterIsInstance<Inet4Address>().filter { it.isSiteLocalAddress }.map { "${it.hostAddress}:${port.value}" }
        }
    }.getOrDefault(emptyList())

    internal fun listen(service: Service) {
        code.value = "%04d".format(SecureRandom().nextInt(10_000))
        val ss = ServerSocket()
        ss.reuseAddress = true
        runCatching { ss.bind(InetSocketAddress(PORT)) }.onFailure { ss.bind(InetSocketAddress(0)) }   // 8790 taken: any free port, NSD tells the tablet
        socket = ss
        port.value = ss.localPort
        lastRequest = System.currentTimeMillis()
        running.value = true
        thread(name = "koutube-server") {
            while (!ss.isClosed) {
                val s = runCatching { ss.accept() }.getOrNull() ?: break
                thread(name = "koutube-conn") { runCatching { handle(s) }; runCatching { s.close() } }
            }
        }
        thread(name = "koutube-idle") {
            while (!ss.isClosed) {
                Thread.sleep(60_000)
                if (open.get() == 0 && System.currentTimeMillis() - lastRequest > 30 * 60_000) { service.stopSelf(); break }
            }
        }
        register(service)
    }

    internal fun shutdown(ctx: Context) {
        runCatching { socket?.close() }
        socket = null
        nsd?.let { l -> runCatching { (ctx.getSystemService(Context.NSD_SERVICE) as NsdManager).unregisterService(l) } }
        nsd = null
        releaseLocks()
        running.value = false
    }

    private fun register(ctx: Context) {
        val info = NsdServiceInfo().apply { serviceName = "KouTube · ${Lib.deviceName}"; serviceType = TYPE; port = this@Server.port.value }
        val l = object : NsdManager.RegistrationListener {
            override fun onServiceRegistered(i: NsdServiceInfo) {}
            override fun onRegistrationFailed(i: NsdServiceInfo, e: Int) {}
            override fun onServiceUnregistered(i: NsdServiceInfo) {}
            override fun onUnregistrationFailed(i: NsdServiceInfo, e: Int) {}
        }
        runCatching { (ctx.getSystemService(Context.NSD_SERVICE) as NsdManager).registerService(info, NsdManager.PROTOCOL_DNS_SD, l); nsd = l }
    }

    // Keep CPU and Wi-Fi awake only while a video is actually going out.
    private fun streamStarted(key: String) {
        streams.getOrPut(key) { AtomicInteger() }.incrementAndGet()
        if (open.incrementAndGet() == 1) synchronized(this) {
            val ctx = App.instance
            wake = (ctx.getSystemService(Context.POWER_SERVICE) as PowerManager).newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "koutube:stream").apply { acquire(3 * 3600_000L) }
            @Suppress("DEPRECATION")
            wifi = (ctx.applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager).createWifiLock(WifiManager.WIFI_MODE_FULL_HIGH_PERF, "koutube:stream").apply { acquire() }
        }
    }

    private fun streamEnded(key: String) {
        streams[key]?.decrementAndGet()
        if (open.decrementAndGet() == 0) releaseLocks()
    }

    private fun releaseLocks() = synchronized(this) {
        wake?.takeIf { it.isHeld }?.release(); wake = null
        wifi?.takeIf { it.isHeld }?.release(); wifi = null
    }

    // ---------- HTTP ----------

    private class Req(val method: String, val path: String, val query: Map<String, String>, val headers: Map<String, String>, val body: ByteArray)

    private fun readLine(i: InputStream): String? {
        val b = StringBuilder()
        while (true) {
            val c = i.read()
            if (c < 0) return if (b.isEmpty()) null else b.toString()
            if (c == '\n'.code) return b.toString().trimEnd('\r')
            if (b.length > 8192) return null
            b.append(c.toChar())
        }
    }

    private fun read(i: InputStream): Req? {
        val line = readLine(i) ?: return null
        val parts = line.split(' ')
        if (parts.size < 2) return null
        val h = HashMap<String, String>()
        while (true) {
            val l = readLine(i) ?: return null
            if (l.isEmpty()) break
            val c = l.indexOf(':')
            if (c > 0) h[l.substring(0, c).trim().lowercase()] = l.substring(c + 1).trim()
        }
        val n = h["content-length"]?.toIntOrNull()?.coerceIn(0, 1 shl 20) ?: 0
        val body = ByteArray(n)
        var got = 0
        while (got < n) { val r = i.read(body, got, n - got); if (r < 0) break; got += r }
        val target = parts[1]
        val q = target.substringAfter('?', "").split('&').filter { it.contains('=') }
            .associate { URLDecoder.decode(it.substringBefore('='), "UTF-8") to URLDecoder.decode(it.substringAfter('='), "UTF-8") }
        return Req(parts[0], URLDecoder.decode(target.substringBefore('?'), "UTF-8"), q, h, body)
    }

    private fun head(o: OutputStream, code: Int, type: String, length: Long, extra: String = "") {
        val status = when (code) { 200 -> "OK"; 206 -> "Partial Content"; 401 -> "Unauthorized"; 403 -> "Forbidden"; 404 -> "Not Found"; 416 -> "Range Not Satisfiable"; 429 -> "Too Many Requests"; else -> "Error" }
        o.write(("HTTP/1.1 $code $status\r\nContent-Type: $type\r\nContent-Length: $length\r\nConnection: close\r\n$extra\r\n").toByteArray())
    }

    private fun text(o: OutputStream, code: Int, body: String, type: String = "application/json; charset=utf-8") {
        val b = body.toByteArray()
        head(o, code, type, b.size.toLong()); o.write(b); o.flush()
    }

    private fun isLocal(a: InetAddress) = a.isSiteLocalAddress || a.isLoopbackAddress || a.isLinkLocalAddress

    private fun handle(s: Socket) {
        s.soTimeout = 30_000
        val i = BufferedInputStream(s.getInputStream())
        val o = BufferedOutputStream(s.getOutputStream(), 64 * 1024)
        val r = read(i) ?: return
        if (!isLocal(s.inetAddress)) return text(o, 403, "{}")       // never the internet, whatever the router does
        lastRequest = System.currentTimeMillis()
        if (r.method == "POST" && r.path == "/pair") return pair(o, r)
        val token = r.headers["x-koutube-token"] ?: r.query["t"]
        if (token == null || token !in devices.value) return text(o, 401, "{}")
        val p = r.path.split('/').filter { it.isNotEmpty() }        // api, what, storageId, key, …
        when {
            r.method == "GET" && r.path == "/api/library" -> text(o, 200, json.encodeToString(Lib.snapshot()))
            r.method == "GET" && p.size >= 4 && p[1] == "video" -> video(o, r, p[2], p[3].substringBefore('.'))
            r.method == "GET" && p.size >= 4 && p[1] == "thumb" -> thumb(o, p[2], p[3])
            r.method == "GET" && p.size >= 4 && p[1] == "avatar" -> avatar(o, p[2], p[3])
            r.method == "GET" && p.size >= 4 && p[1] == "comments" -> comments(o, p[2], p[3])
            r.method == "GET" && p.size >= 5 && p[1] == "sub" -> sub(o, p[2], p[3], p[4])
            r.method == "POST" && r.path == "/api/edit" -> {
                val e = json.decodeFromString<RemoteEdit>(String(r.body))
                Lib.applyRemote(e)
                text(o, 200, "{}")
            }
            else -> text(o, 404, "{}")
        }
    }

    private fun pair(o: OutputStream, r: Req) {
        val now = System.currentTimeMillis()
        if (now - wrongSince > 60_000) { wrongCodes = 0; wrongSince = now }
        if (wrongCodes >= 5) return text(o, 429, "{}")                  // no guessing 10 000 codes
        val p = runCatching { json.decodeFromString<PairRequest>(String(r.body)) }.getOrNull() ?: return text(o, 403, "{}")
        if (p.code != code.value) { wrongCodes++; return text(o, 403, "{}") }
        val t = ByteArray(16).also { SecureRandom().nextBytes(it) }.hex()
        devices.value = devices.value + (t to p.name.take(60))
        prefs.edit().putString("tokens", json.encodeToString(devices.value)).apply()
        text(o, 200, json.encodeToString(PairReply(t, Lib.deviceName)))
    }

    private fun video(o: OutputStream, r: Req, storageId: String, key: String) {
        val (uri, size, name) = Lib.fileFor(storageId, key) ?: return text(o, 404, "{}")
        val type = when (name.substringAfterLast('.').lowercase()) { "webm" -> "video/webm"; "mkv" -> "video/x-matroska"; else -> "video/mp4" }
        var start = 0L
        var end = size - 1
        val range = r.headers["range"]
        if (range != null && range.startsWith("bytes=")) {
            val (a, b) = range.removePrefix("bytes=").substringBefore(',').split('-').let { it[0] to it.getOrElse(1) { "" } }
            if (a.isEmpty()) start = maxOf(0, size - (b.toLongOrNull() ?: 0)) else { start = a.toLongOrNull() ?: 0; b.toLongOrNull()?.let { end = minOf(it, size - 1) } }
            if (start >= size) return head(o, 416, type, 0, "Content-Range: bytes */$size\r\n").also { o.flush() }
        }
        val len = end - start + 1
        head(o, if (range != null) 206 else 200, type, len,
            "Accept-Ranges: bytes\r\n" + (if (range != null) "Content-Range: bytes $start-$end/$size\r\n" else ""))
        if (r.method == "HEAD") return o.flush()
        streamStarted(key)
        try {
            App.instance.contentResolver.openFileDescriptor(uri, "r")!!.use { pfd ->
                FileInputStream(pfd.fileDescriptor).channel.use { ch ->
                    ch.position(start)
                    val buf = ByteBuffer.allocate(64 * 1024)
                    var left = len
                    while (left > 0) {
                        buf.clear(); if (left < buf.capacity()) buf.limit(left.toInt())
                        val n = ch.read(buf); if (n <= 0) break
                        o.write(buf.array(), 0, n); left -= n
                    }
                }
            }
            o.flush()
        } finally { streamEnded(key) }
    }

    private fun thumb(o: OutputStream, storageId: String, key: String) {
        val v = Lib.videos.value.firstOrNull { it.storageId == storageId && it.key == key } ?: return text(o, 404, "{}")
        runBlocking { Thumbs.get(v) }
        val f = Thumbs.fileFor(v) ?: return text(o, 404, "{}")
        val b = f.readBytes()
        head(o, 200, "image/jpeg", b.size.toLong(), "Cache-Control: max-age=86400\r\n"); o.write(b); o.flush()
    }

    private fun avatar(o: OutputStream, storageId: String, key: String) {
        val v = Lib.videos.value.firstOrNull { it.storageId == storageId && it.key == key } ?: return text(o, 404, "{}")
        val uri = v.meta.avatar?.let { Lib.storageFile(storageId, it) } ?: return text(o, 404, "{}")
        val b = App.instance.contentResolver.openInputStream(uri)!!.use { it.readBytes() }
        head(o, 200, "image/jpeg", b.size.toLong(), "Cache-Control: max-age=86400\r\n"); o.write(b); o.flush()
    }

    private fun comments(o: OutputStream, storageId: String, key: String) {
        val v = Lib.videos.value.firstOrNull { it.storageId == storageId && it.key == key } ?: return text(o, 404, "{}")
        val uri = v.meta.comments?.let { Lib.storageFile(storageId, it) } ?: return text(o, 404, "{}")
        text(o, 200, App.instance.contentResolver.openInputStream(uri)!!.use { String(it.readBytes()) })
    }

    private fun sub(o: OutputStream, storageId: String, key: String, lang: String) {
        val uri = Lib.subFor(storageId, key, lang) ?: return text(o, 404, "{}")
        val b = App.instance.contentResolver.openInputStream(uri)!!.use { it.readBytes() }
        head(o, 200, "text/vtt; charset=utf-8", b.size.toLong()); o.write(b); o.flush()
    }
}

class ServerService : Service() {
    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        val nm = getSystemService(NotificationManager::class.java)
        if (Build.VERSION.SDK_INT >= 26) nm.createNotificationChannel(NotificationChannel("server", "Раздача по Wi-Fi", NotificationManager.IMPORTANCE_LOW))
        Server.listen(this)
        val stop = PendingIntent.getService(this, 1, Intent(this, ServerService::class.java).setAction("stop"), PendingIntent.FLAG_IMMUTABLE)
        val open = PendingIntent.getActivity(this, 2, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE)
        val n = (if (Build.VERSION.SDK_INT >= 26) Notification.Builder(this, "server") else @Suppress("DEPRECATION") Notification.Builder(this))
            .setSmallIcon(android.R.drawable.stat_sys_upload)
            .setContentTitle("KouTube раздаёт видео по Wi-Fi")
            .setContentText("Код подключения: ${Server.code.value} · ${Server.addresses().firstOrNull() ?: ""}")
            .setContentIntent(open)
            .addAction(Notification.Action.Builder(null, "Остановить", stop).build())
            .setOngoing(true).build()
        if (Build.VERSION.SDK_INT >= 29) startForeground(1, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE) else startForeground(1, n)
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == "stop") { Lib.updateSettings { it.copy(shareWifi = false) }; stopSelf() }
        return START_NOT_STICKY
    }

    override fun onDestroy() { Server.shutdown(this); super.onDestroy() }
}
