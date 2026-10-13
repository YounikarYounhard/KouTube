package com.koutube

import android.Manifest
import android.os.Build
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.rounded.ArrowBack
import androidx.compose.material.icons.outlined.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import kotlinx.coroutines.launch

/** First start: is this the phone with the videos (server) or a screen that watches them (client)? */
@Composable
fun RoleScreen(current: String?, onServer: () -> Unit, onClient: () -> Unit, onPlayer: () -> Unit) {
    Column(Modifier.fillMaxSize().background(MaterialTheme.colorScheme.background).statusBarsPadding().verticalScroll(rememberScrollState()).padding(20.dp)) {
        Logo()
        Spacer(Modifier.height(36.dp))
        Text("Как будет работать это устройство?", fontSize = 22.sp, fontWeight = FontWeight.Bold)
        Spacer(Modifier.height(20.dp))
        RoleCard(Icons.Outlined.PhoneAndroid, "Сервер — видео здесь", current == "server",
            "Флешка через OTG-переходник или папка в памяти телефона. Смотрите сами и можете раздавать видео планшету по Wi-Fi.", onServer)
        Spacer(Modifier.height(12.dp))
        RoleCard(Icons.Outlined.Tablet, "Клиент — смотреть с сервера", current == "client",
            "Для планшета или второго телефона. Видео идёт по Wi-Fi с телефона, где флешка, — сюда ничего не копируется.", onClient)
        Spacer(Modifier.height(12.dp))
        RoleCard(Icons.Outlined.FolderOpen, "Просто плеер", current == "player",
            "Выберите папку с видео — и смотрите. Без компьютера, флешки-библиотеки и Wi-Fi; в папку ничего не записывается.", onPlayer)
        Spacer(Modifier.height(16.dp))
        Text("Этот вопрос — при каждом запуске. «Сервер» сразу включает раздачу по Wi-Fi.", color = MaterialTheme.colorScheme.onSurfaceVariant, fontSize = 13.sp)
    }
}

@Composable
private fun RoleCard(icon: ImageVector, title: String, last: Boolean, text: String, onClick: () -> Unit) {
    // the mode used last time is outlined in red, so the usual choice is one tap
    Row(Modifier.fillMaxWidth().clip(RoundedCornerShape(12.dp)).border(if (last) 2.dp else 1.dp, if (last) YtRed else MaterialTheme.colorScheme.outline, RoundedCornerShape(12.dp))
        .clickable(onClick = onClick).padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
        Icon(icon, null, Modifier.size(36.dp))
        Column(Modifier.padding(start = 16.dp).weight(1f)) {
            Text(title, fontWeight = FontWeight.Bold, fontSize = 17.sp)
            if (last) Text("в прошлый раз", color = YtRed, fontSize = 12.sp, fontWeight = FontWeight.Medium)
            Spacer(Modifier.height(4.dp))
            Text(text, color = MaterialTheme.colorScheme.onSurfaceVariant, fontSize = 14.sp, lineHeight = 19.sp)
        }
    }
}

/** Client: find the phone (NSD), or type its address, then the 4-digit code it shows. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ConnectScreen(onBack: () -> Unit) {
    val ctx = LocalContext.current
    val spotted by Remote.spotted.collectAsState()
    var host by remember { mutableStateOf("") }
    var code by remember { mutableStateOf("") }
    var busy by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    val scope = rememberCoroutineScope()
    DisposableEffect(Unit) { Remote.discover(ctx); onDispose { Remote.stopDiscovery(ctx) } }
    BackHandler(onBack = onBack)
    Column(Modifier.fillMaxSize().background(MaterialTheme.colorScheme.background).statusBarsPadding().imePadding()) {
        TopAppBar(title = { Text("Подключение к серверу", fontWeight = FontWeight.Bold) },
            navigationIcon = { IconButton(onBack) { Icon(Icons.AutoMirrored.Rounded.ArrowBack, "Назад") } },
            colors = TopAppBarDefaults.topAppBarColors(containerColor = MaterialTheme.colorScheme.background))
        Column(Modifier.verticalScroll(rememberScrollState()).padding(horizontal = 16.dp)) {
            Text("На телефоне с видео откройте KouTube → Настройки → «Раздавать видео по Wi-Fi». Там будет код из 4 цифр. " +
                "Оба устройства должны быть в одной сети Wi-Fi — или планшет подключён к точке доступа телефона.",
                color = MaterialTheme.colorScheme.onSurfaceVariant, fontSize = 14.sp, lineHeight = 19.sp)
            Spacer(Modifier.height(20.dp))
            Text("Найдено в сети", fontWeight = FontWeight.Bold)
            if (spotted.isEmpty()) Row(Modifier.padding(vertical = 12.dp), verticalAlignment = Alignment.CenterVertically) {
                CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp, color = YtRed)
                Text("  Ищу телефоны… если долго не находится — введите адрес ниже", color = MaterialTheme.colorScheme.onSurfaceVariant, fontSize = 13.sp)
            }
            spotted.forEach { s ->
                ListItem(headlineContent = { Text(s.name.removePrefix("KouTube · ")) }, supportingContent = { Text("${s.host}:${s.port}") },
                    leadingContent = { Icon(Icons.Outlined.PhoneAndroid, null) },
                    trailingContent = { if (host == "${s.host}:${s.port}") Icon(Icons.Outlined.CheckCircle, null, tint = YtBlue) },
                    modifier = Modifier.clickable { host = "${s.host}:${s.port}" })
            }
            Spacer(Modifier.height(16.dp))
            OutlinedTextField(host, { host = it.trim() }, Modifier.fillMaxWidth(), label = { Text("Адрес сервера") },
                placeholder = { Text("например 192.168.43.1") }, singleLine = true, keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri))
            Spacer(Modifier.height(8.dp))
            OutlinedTextField(code, { code = it.filter(Char::isDigit).take(4) }, Modifier.fillMaxWidth(), label = { Text("Код с телефона") },
                singleLine = true, keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.NumberPassword))
            error?.let { Text(it, color = YtRed, modifier = Modifier.padding(top = 8.dp), fontSize = 14.sp) }
            Spacer(Modifier.height(16.dp))
            Button(enabled = !busy && host.isNotBlank() && code.length == 4, onClick = {
                busy = true; error = null
                val h = host.substringBefore(':')
                val p = host.substringAfter(':', "").toIntOrNull() ?: Server.PORT
                scope.launch {
                    error = Remote.pair(h, p, code)
                    busy = false
                    if (error == null) Lib.refresh(force = true)
                }
            }, modifier = Modifier.fillMaxWidth()) { Text(if (busy) "Подключаюсь…" else "Подключиться") }
        }
    }
}

/** Settings → sharing over Wi-Fi (server role). */
@Composable
fun ShareSection() {
    val ctx = LocalContext.current
    val s by Lib.settings.collectAsState()
    val running by Server.running.collectAsState()
    val code by Server.code.collectAsState()
    val devices by Server.devices.collectAsState()
    val ask = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { Server.start(ctx) }
    ListItem(
        headlineContent = { Text("Раздавать видео по Wi-Fi") },
        supportingContent = { Text("Планшет в той же сети сможет смотреть библиотеку этого телефона") },
        trailingContent = {
            Switch(s.shareWifi, { on ->
                Lib.updateSettings { it.copy(shareWifi = on) }
                when {
                    !on -> Server.stop(ctx)
                    Build.VERSION.SDK_INT >= 33 -> ask.launch(Manifest.permission.POST_NOTIFICATIONS)   // the Stop button lives in a notification
                    else -> Server.start(ctx)
                }
            }, colors = ytSwitch())
        },
    )
    if (s.shareWifi && running) Column(Modifier.padding(horizontal = 16.dp)) {
        Text("Код для подключения", color = MaterialTheme.colorScheme.onSurfaceVariant, fontSize = 13.sp)
        Text(code, fontSize = 40.sp, fontWeight = FontWeight.Bold, letterSpacing = 8.sp)
        Text("Адрес: " + Server.addresses().joinToString("  или  ").ifEmpty { "нет сети — включите Wi-Fi или точку доступа" },
            fontSize = 14.sp)
        Text("Без зрителей раздача выключится сама через 30 минут.", color = MaterialTheme.colorScheme.onSurfaceVariant, fontSize = 13.sp,
            modifier = Modifier.padding(top = 4.dp))
    }
    devices.forEach { (token, name) ->
        ListItem(headlineContent = { Text(name) }, supportingContent = { Text("подключённое устройство") },
            leadingContent = { Icon(Icons.Outlined.Tablet, null) },
            trailingContent = { TextButton({ Server.forget(token) }) { Text("Забыть", color = YtBlue) } })
    }
}

/** Settings → the server this tablet watches from (client role). */
@Composable
fun ClientSection() {
    val conn by Remote.conn.collectAsState()
    val c = conn ?: return
    ListItem(headlineContent = { Text(c.name) }, supportingContent = { Text("${c.host}:${c.port}") },
        leadingContent = { Icon(Icons.Outlined.PhoneAndroid, null) },
        trailingContent = { TextButton({ Remote.disconnect(); Lib.refresh(force = true) }) { Text("Отключиться", color = YtBlue) } })
    Text("Автоудаление и папки настраиваются на телефоне-сервере.", Modifier.padding(horizontal = 16.dp),
        color = MaterialTheme.colorScheme.onSurfaceVariant, fontSize = 13.sp)
}
