package com.koutube

import kotlin.random.Random

/** Stable within one visit; reshuffled explicitly, with channel variety and local watch signals. */
fun localFeed(all: List<Video>, seed: Long, shorts: Boolean = false): List<Video> {
    val taste = HashMap<String?, Double>()
    all.forEach { v ->
        val signal = when { v.flags.rating < 0 -> -3.0; v.flags.rating > 0 -> 2.0; v.finished -> 0.5; else -> 0.0 }
        taste[v.meta.channel] = (taste[v.meta.channel] ?: 0.0) + signal
    }
    val random = Random(seed)
    return all.filter { it.present && !it.finished && it.isShort == shorts && it.flags.rating >= 0 }
        .map { it to (random.nextDouble() * 2 + (if (it.started) 0.45 else 0.0) + (taste[it.meta.channel] ?: 0.0).coerceIn(-2.0, 2.0)) }
        .groupBy { it.first.meta.channel ?: it.first.key }
        .values.flatMap { group -> group.sortedByDescending { it.second }.mapIndexed { i, pair -> pair.first to pair.second - i * 0.8 } }
        .sortedByDescending { it.second }.map { it.first }
}

fun searchScore(v: Video, query: String): Int {
    val title = unFullwidth(v.title).lowercase().replace('ё', 'е')
    val q = unFullwidth(query).lowercase().replace('ё', 'е')
    return when { title == q -> 100; title.startsWith(q) -> 80; title.contains(q) -> 60
        v.meta.channel?.lowercase()?.contains(q) == true -> 40; else -> 20 }
}
