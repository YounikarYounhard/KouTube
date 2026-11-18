package com.koutube

import org.junit.Assert.assertEquals
import org.junit.Test

// The library search: any word form, and YouTube's tags count too.
class SearchTest {
    private fun v(title: String, tags: List<String>? = null) = Video(title, "s", Item(meta = Meta(title = title, channel = "Канал", tags = tags)), null)

    @Test fun findsATopicInAnyForm() {
        val all = listOf(v("Путешествия по Японии"), v("Влог из Сочи", tags = listOf("путешествия", "море")), v("Ремонт двигателя"))
        assertEquals(listOf("Путешествия по Японии", "Влог из Сочи"), all.filter { matches(it, "путешествие") }.map { it.title })
        assertEquals(listOf("Ремонт двигателя"), all.filter { matches(it, "ремонты") }.map { it.title })
        assertEquals(listOf("Путешествия по Японии"), all.filter { matches(it, "японии путешествия") }.map { it.title })
    }

    @Test fun exactTitleWinsAndHistoryChangeDoesNotChangeSearchDocument() {
        val title = v("Ёлки: часть 2")
        val tagged = v("Разговор с автором", tags = listOf("ёлки часть 2"))
        assertEquals(SearchDocument(title), SearchDocument(title.copy(item = title.item.copy(
            watch = Watch(ts = 100, status = Watch.STARTED, posMs = 12_000)))))
        val q = searchWords("Елки часть 2")
        org.junit.Assert.assertTrue(SearchDocument(title).rank(q) > SearchDocument(tagged).rank(q))
        org.junit.Assert.assertFalse(matches(title, "!!!"))
    }
}
