package com.koutube

import android.content.Context
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.net.Uri
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.withContext
import kotlinx.serialization.Serializable
import kotlinx.serialization.encodeToString
import java.net.HttpURLConnection
import java.net.URL

@Serializable data class Conn(val name: String, val host: String, val port: Int, val token: String)
data class Spotted(val name: String, val host: String, val port: Int)

/**
 * Client mode (a tablet): the library and the video live on the phone with the stick. Nothing is
 * copied here except covers and the list; progress goes back to the phone, which writes it to the
 * stick and decides about auto-delete with its own settings.
 */
object Remote {
    private val prefs get() = App.instance.getSharedPreferences("koutube-client", 0)
    val conn = MutableStateFlow(runCatching { json.decodeFromString<Conn>(prefs.getString("conn", "")!!) }.getOrNull())
    val spotted = MutableStateFlow<List<Spotted>>(emptyList())
    private var discovery: NsdManager.DiscoveryListener? = null
    private val pending = LinkedHashMap<String, RemoteEdit>()

    private fun c() = conn.value ?: error("нет подключения")

    /**
     * A VPN on the tablet sends home-network traffic into its tunnel, so the phone can't be reached. The phone is on the
     * same Wi-Fi: in client mode the whole app talks over the Wi-Fi itself (YouTube covers then come via the phone).
     */
    /** A VPN that can't be bypassed swallows home-network traffic: say so, it's the usual cause on a tablet. */
    private fun vpnHint(): String {
        val cm = App.instance.getSystemService(ConnectivityManager::class.java) ?: return ""
        val vpn = cm.getNetworkCapabilities(cm.activeNetwork)?.hasTransport(NetworkCapabilities.TRANSPORT_VPN) == true
        return if (vpn) " На этом устройстве включён VPN — он не пускает к телефону. Выключите VPN или добавьте KouTube в его исключения." else ""
    }

    fun useHomeNetwork() {
        val cm = App.instance.getSystemService(ConnectivityManager::class.java) ?: return
        @Suppress("DEPRECATION")
        val home = cm.allNetworks.firstOrNull { n ->
            cm.getNetworkCapabilities(n)?.let { (it.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) || it.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET))
                && !it.hasTransport(NetworkCapabilities.TRANSPORT_VPN) } == true
        }
        if (cm.boundNetworkForProcess != home) cm.bindProcessToNetwork(home)
    }
    private fun base(c: Conn = c()) = "http://${c.host}:${c.port}"

    fun videoUri(v: Video): Uri = Uri.parse("${base()}/api/video/${v.storageId}/${v.key}.${v.meta.path?.substringAfterLast('.') ?: "mp4"}?t=${c().token}")
    fun thumbUrl(v: Video) = "${base()}/api/thumb/${v.storageId}/${v.key}?t=${c().token}"
    fun commentsUrl(v: Video) = "${base()}/api/comments/${v.storageId}/${v.key}?t=${c().token}"
    fun avatarUrl(v: Video) = "${base()}/api/avatar/${v.storageId}/${v.key}?t=${c().token}"
    fun subUri(v: Video, lang: String): Uri = Uri.parse("${base()}/api/sub/${v.storageId}/${v.key}/$lang?t=${c().token}")

    private fun http(url: String, method: String = "GET", body: String? = null): Pair<Int, ByteArray> {
        val h = URL(url).openConnection() as HttpURLConnection
        h.connectTimeout = 5000; h.readTimeout = 20_000; h.requestMethod = method
        conn.value?.let { h.setRequestProperty("X-KouTube-Token", it.token) }
        if (body != null) { h.doOutput = true; h.setRequestProperty("Content-Type", "application/json"); h.outputStream.use { it.write(body.toByteArray()) } }
        return try { h.responseCode to (if (h.responseCode in 200..299) h.inputStream.use { it.readBytes() } else ByteArray(0)) } finally { h.disconnect() }
    }

    fun get(url: String) = http(url)

    /** Pairs with the phone using the 4-digit code it shows; null on success, else what went wrong. */
    suspend fun pair(host: String, port: Int, code: String): String? = withContext(Dispatchers.IO) {
        useHomeNetwork()
        runCatching {
            val (st, b) = http("http://$host:$port/pair", "POST", json.encodeToString(PairRequest(code.trim(), Lib.deviceId, Lib.deviceName)))
            when (st) {
                200 -> json.decodeFromString<PairReply>(String(b)).let { r ->
                    conn.value = Conn(r.serverName, host, port, r.token)
                    prefs.edit().putString("conn", json.encodeToString(conn.value)).apply()
                    null
                }
                403 -> "Неверный код — посмотрите его на телефоне-сервере"
                429 -> "Слишком много попыток — подождите минуту"
                else -> "Телефон ответил ошибкой $st"
            }
        }.getOrElse { "Не удалось связаться с $host:$port — телефон в той же сети Wi-Fi, раздача включена?" }
    }

    fun disconnect() { conn.value = null; prefs.edit().remove("conn").apply() }

    /** The phone's library, or an error text. 401 = the phone forgot this tablet. */
    suspend fun library(): Result<RemoteLibrary> = withContext(Dispatchers.IO) {
        runCatching {
            val (st, b) = http("${base()}/api/library")
            when (st) {
                200 -> json.decodeFromString<RemoteLibrary>(String(b))
                401 -> error("Телефон больше не узнаёт это устройство — подключитесь заново")
                else -> error("Телефон ответил ошибкой $st")
            }
        }.recoverCatching { if (it is IllegalStateException) throw it else error("Нет связи с телефоном «${conn.value?.name}». Проверьте Wi-Fi и что раздача включена." + vpnHint()) }
    }

    /** Edits are collected and sent together: progress every few seconds would be wasted radio time. */
    @Synchronized fun edit(e: RemoteEdit) {
        val k = e.storageId + "/" + e.key
        val old = pending[k]
        pending[k] = if (old == null) e else old.copy(watch = e.watch ?: old.watch, flags = e.flags ?: old.flags, delete = e.delete || old.delete)
    }

    suspend fun flush() = withContext(Dispatchers.IO) {
        val todo = synchronized(this@Remote) { pending.values.toList().also { pending.clear() } }
        for (e in todo) {
            val ok = runCatching { http("${base()}/api/edit", "POST", json.encodeToString(e)).first == 200 }.getOrDefault(false)
            if (!ok) edit(e)        // keep it for the next try
        }
    }

    // ---------- finding the phone on the network ----------

    fun discover(ctx: Context) {
        stopDiscovery(ctx)
        spotted.value = emptyList()
        val nsd = ctx.getSystemService(Context.NSD_SERVICE) as NsdManager
        val l = object : NsdManager.DiscoveryListener {
            override fun onDiscoveryStarted(t: String) {}
            override fun onDiscoveryStopped(t: String) {}
            override fun onStartDiscoveryFailed(t: String, e: Int) {}
            override fun onStopDiscoveryFailed(t: String, e: Int) {}
            override fun onServiceLost(i: NsdServiceInfo) { spotted.value = spotted.value.filter { it.name != i.serviceName } }
            override fun onServiceFound(i: NsdServiceInfo) {
                @Suppress("DEPRECATION")
                nsd.resolveService(i, object : NsdManager.ResolveListener {
                    override fun onResolveFailed(i: NsdServiceInfo, e: Int) {}
                    override fun onServiceResolved(r: NsdServiceInfo) {
                        val host = r.host?.hostAddress ?: return
                        spotted.value = spotted.value.filter { it.name != r.serviceName } + Spotted(r.serviceName, host, r.port)
                    }
                })
            }
        }
        runCatching { nsd.discoverServices(Server.TYPE, NsdManager.PROTOCOL_DNS_SD, l); discovery = l }
    }

    fun stopDiscovery(ctx: Context) {
        discovery?.let { runCatching { (ctx.getSystemService(Context.NSD_SERVICE) as NsdManager).stopServiceDiscovery(it) } }
        discovery = null
    }
}
