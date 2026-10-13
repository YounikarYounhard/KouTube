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
}
