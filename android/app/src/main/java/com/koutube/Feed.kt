package com.koutube

import kotlin.random.Random

private val topicToken = Regex("[\\p{L}\\p{N}]+")
private val topicNoise = setOf("этот", "который", "сегодня", "новый", "видео", "ролик", "посмотрел", "лучший", "youtube", "shorts")
private fun topics(video: Video): Set<String> = topicToken.findAll(
    (video.title + " " + video.meta.tags.orEmpty().joinToString(" ")).lowercase().replace('ё', 'е')
).map { it.value }.filter { it.length >= 4 && it !in topicNoise && it.any(Char::isLetter) }
    .map { when {
        it.startsWith("minecraft") || it.startsWith("майнкрафт") -> "minecraft"
        it.length >= 7 -> it.take(6)
        else -> it
    } }.toSet()

fun recommended(cur: Video, all: List<Video>): List<Video> {
    val subject = topics(cur)
    return all.asSequence().filter { it.present && !it.finished && it.flags.rating >= 0 && it.isShort == cur.isShort && it.key != cur.key }
        .map { video ->
            val words = topics(video)
            val matches = words.count { it in subject }
            val match = matches * 12.0 / (1 + kotlin.math.sqrt(words.size.toDouble()))
            val channel = if (cur.meta.channel != null && cur.meta.channel == video.meta.channel) 1.8 else 0.0
            video to (match + channel + if (video.started) 0.4 else 0.0)
        }.sortedByDescending { it.second }.map { it.first }.distinctBy { it.key }.take(80).toList()
}

/** Stable within one visit; reshuffled explicitly, with channel variety and local watch signals. */
fun localFeed(all: List<Video>, seed: Long, shorts: Boolean = false): List<Video> {
    val taste = HashMap<String?, Double>()
    all.forEach { v ->
        val signal = when { v.flags.rating < 0 -> -3.0; v.flags.rating > 0 -> 2.0; v.finished -> 0.5; else -> 0.0 }
        taste[v.meta.channel] = (taste[v.meta.channel] ?: 0.0) + signal
    }
    val topicTaste = HashMap<String, Double>()
    all.asSequence().filter { it.flags.rating != 0 || it.finished || it.started }
        .sortedByDescending { it.watch.lastWatchedAt ?: 0 }.take(200).forEach { video ->
            val weight = when { video.flags.rating < 0 -> -1.2; video.flags.rating > 0 -> 0.9; video.finished -> 0.4; else -> 0.2 }
            topics(video).forEach { topicTaste.merge(it, weight, Double::plus) }
        }
    val random = Random(seed)
    return all.filter { it.present && !it.finished && it.isShort == shorts && it.flags.rating >= 0 }
        .map { it to (random.nextDouble() * 2 + topics(it).sumOf { w -> topicTaste[w] ?: 0.0 }.coerceIn(-2.5, 2.5) + (if (it.started) 0.45 else 0.0) + (taste[it.meta.channel] ?: 0.0).coerceIn(-2.0, 2.0)) }
        .groupBy { it.first.meta.channel ?: it.first.key }
        .values.flatMap { group -> group.sortedByDescending { it.second }.mapIndexed { i, pair -> pair.first to pair.second - i * 0.8 } }
        .sortedByDescending { it.second }.map { it.first }
}

fun searchScore(v: Video, query: String): Int = SearchDocument(v).rank(searchWords(query))
