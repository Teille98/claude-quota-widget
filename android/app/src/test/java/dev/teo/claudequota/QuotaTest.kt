package dev.teo.claudequota

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.time.Duration
import java.time.Instant
import java.time.ZoneId
import java.util.Locale

class QuotaTest {
    private val now = Instant.parse("2026-11-15T13:10:00Z")
    private val paris = ZoneId.of("Europe/Paris")
    private val fr = Locale.FRENCH
    private val en = Locale.ENGLISH

    // Real message (shape) posted by shared/usage-script.js.
    private val real = """
        {"ok":true,"limits":[
          {"kind":"session","percent":16,"resets_at":"2026-11-15T14:50:00.467635+00:00","severity":"normal","model":null},
          {"kind":"weekly_all","percent":33,"resets_at":"2026-11-16T19:00:00.467653+00:00","severity":"normal","model":null},
          {"kind":"weekly_scoped","percent":0,"resets_at":"2026-11-16T19:00:00+00:00","severity":"normal","model":"Fable"}
        ]}
    """.trimIndent()

    @Test fun parsesRealMessage() {
        val r = UsageParser.parse(real, now)
        assertTrue(r.ok)
        val s = r.snapshot!!
        assertEquals(3, s.limits.size)
        assertEquals(16, s.session!!.percent)
        assertEquals(33, s.weekly!!.percent)
        assertEquals("Fable", s.limits[2].model)
        assertNull(s.session!!.model) // JSON null -> real null, not the string "null"
        assertEquals(Instant.parse("2026-11-15T14:50:00.467635Z"), s.session!!.resetsAt)
        assertEquals(now, s.fetchedAt)
        assertEquals(33, s.highest!!.percent)
    }

    @Test fun labelsFrench() {
        val s = UsageParser.parse(real, now).snapshot!!
        assertEquals("Session (5 h)", s.limits[0].label(fr))
        assertEquals("Semaine · tous les modèles", s.limits[1].label(fr))
        assertEquals("Semaine · Fable", s.limits[2].label(fr))
        assertEquals("5 h", s.limits[0].shortLabel(fr))
        assertEquals("7 j", s.limits[1].shortLabel(fr))
    }

    @Test fun labelsEnglish() {
        val s = UsageParser.parse(real, now).snapshot!!
        assertEquals("Session (5h)", s.limits[0].label(en))
        assertEquals("Week · all models", s.limits[1].label(en))
        assertEquals("Week · Fable", s.limits[2].label(en))
        assertEquals("5h", s.limits[0].shortLabel(en))
        assertEquals("7d", s.limits[1].shortLabel(en))
    }

    @Test fun failureReasons() {
        assertEquals(FetchError.AUTH, UsageParser.parse("""{"ok":false,"reason":"auth","status":403}""", now).error)
        assertEquals(FetchError.OFFLINE, UsageParser.parse("""{"ok":false,"reason":"offline"}""", now).error)
        assertEquals(FetchError.NO_LIMITS, UsageParser.parse("""{"ok":false,"reason":"no-limits"}""", now).error)
        assertEquals(FetchError.TIMEOUT, UsageParser.parse("""{"ok":false,"reason":"timeout"}""", now).error)
        assertEquals(FetchError.UNKNOWN, UsageParser.parse("""{"ok":false,"reason":"error","message":"x"}""", now).error)
    }

    @Test fun badFormat() {
        for (bad in listOf(null, "", "pas du json", """{"ok":true}""", """{"ok":true,"limits":[]}""",
            """{"ok":true,"limits":[{"kind":"session"}]}""", """{"ok":true,"limits":[{"percent":5}]}""")) {
            val r = UsageParser.parse(bad, now)
            assertFalse(bad.toString(), r.ok)
            assertEquals(bad.toString(), FetchError.BAD_FORMAT, r.error)
        }
    }

    @Test fun roundsAndClampsPercent() {
        val r = UsageParser.parse(
            """{"ok":true,"limits":[{"kind":"session","percent":12.6},{"kind":"weekly_all","percent":140},{"kind":"x","percent":-3}]}""",
            now
        ).snapshot!!
        assertEquals(listOf(13, 100, 0), r.limits.map { it.percent })
    }

    @Test fun invalidResetIsNull() {
        val r = UsageParser.parse("""{"ok":true,"limits":[{"kind":"session","percent":1,"resets_at":"demain"}]}""", now)
        assertNull(r.snapshot!!.session!!.resetsAt)
    }

    @Test fun thresholds() {
        assertEquals(Level.NORMAL, Thresholds.of(0))
        assertEquals(Level.NORMAL, Thresholds.of(49))
        assertEquals(Level.WARNING, Thresholds.of(50))
        assertEquals(Level.WARNING, Thresholds.of(79))
        assertEquals(Level.CRITICAL, Thresholds.of(80))
        assertEquals(Level.CRITICAL, Thresholds.of(100))
    }

    @Test fun durationTextFrench() {
        assertEquals("0 min", TimeText.duration(Duration.ofSeconds(-30), fr))
        assertEquals("45 min", TimeText.duration(Duration.ofMinutes(45), fr))
        assertEquals("2 h", TimeText.duration(Duration.ofMinutes(120), fr))
        assertEquals("1 h 40", TimeText.duration(Duration.ofMinutes(100), fr))
        assertEquals("2 h 05", TimeText.duration(Duration.ofMinutes(125), fr))
    }

    @Test fun durationTextEnglish() {
        assertEquals("45 min", TimeText.duration(Duration.ofMinutes(45), en))
        assertEquals("2h", TimeText.duration(Duration.ofMinutes(120), en))
        assertEquals("1h 40", TimeText.duration(Duration.ofMinutes(100), en))
        assertEquals("2h 05", TimeText.duration(Duration.ofMinutes(125), en))
    }

    @Test fun resetTextFrench() {
        // 13:10 Z = 14:10 in Paris; reset at 14:50 Z = 15:50 in Paris.
        assertEquals("↺ dans 1 h 40 · 15:50", TimeText.reset(Instant.parse("2026-11-15T14:50:00Z"), now, paris, fr))
        assertEquals("↺ lundi 20:00", TimeText.reset(Instant.parse("2026-11-16T19:00:00Z"), now, paris, fr))
        assertEquals("↺ imminente", TimeText.reset(now.plusSeconds(20), now, paris, fr))
        assertEquals("↺ imminente", TimeText.reset(now.minusSeconds(600), now, paris, fr))
        assertEquals("", TimeText.reset(null, now, paris, fr))
    }

    @Test fun resetTextEnglish() {
        assertEquals("↺ in 1h 40 · 15:50", TimeText.reset(Instant.parse("2026-11-15T14:50:00Z"), now, paris, en))
        assertEquals("↺ Monday 20:00", TimeText.reset(Instant.parse("2026-11-16T19:00:00Z"), now, paris, en))
        assertEquals("↺ imminent", TimeText.reset(now.plusSeconds(20), now, paris, en))
    }

    @Test fun ageTextFrench() {
        assertEquals("à l'instant", TimeText.age(now.minusSeconds(20), now, fr))
        assertEquals("il y a 12 min", TimeText.age(now.minusSeconds(12 * 60), now, fr))
        assertEquals("il y a 3 h", TimeText.age(now.minusSeconds(3 * 3600 + 100), now, fr))
        assertEquals("il y a 2 j", TimeText.age(now.minusSeconds(2 * 86400), now, fr))
        assertEquals("à l'instant", TimeText.age(now.plusSeconds(30), now, fr)) // clock skew
    }

    @Test fun ageTextEnglish() {
        assertEquals("just now", TimeText.age(now.minusSeconds(20), now, en))
        assertEquals("12 min ago", TimeText.age(now.minusSeconds(12 * 60), now, en))
        assertEquals("3h ago", TimeText.age(now.minusSeconds(3 * 3600 + 100), now, en))
        assertEquals("2d ago", TimeText.age(now.minusSeconds(2 * 86400), now, en))
    }

    @Test fun cacheRoundTrip() {
        val s = UsageParser.parse(real, now).snapshot!!
        val back = Snapshot.fromJson(s.toJson())
        assertNotNull(back)
        // The cache keeps millisecond precision: compare at that precision.
        assertEquals(s.limits.map { it.copy(resetsAt = it.resetsAt?.let { i -> Instant.ofEpochMilli(i.toEpochMilli()) }) }, back!!.limits)
        assertEquals(s.fetchedAt, back.fetchedAt)
    }

    @Test fun corruptCacheIsNull() {
        assertNull(Snapshot.fromJson(null))
        assertNull(Snapshot.fromJson(""))
        assertNull(Snapshot.fromJson("{broken"))
        assertNull(Snapshot.fromJson("""{"fetchedAt":1}"""))
    }

    private fun snap(at: Instant, session: Int, weekly: Int) = Snapshot(
        listOf(QuotaLimit("session", session, null, null, null), QuotaLimit("weekly_all", weekly, null, null, null)), at
    )

    @Test fun historyKeepsChangesAndHeartbeat() {
        var h = History.add(emptyList(), snap(now, 10, 30))
        h = History.add(h, snap(now.plusSeconds(5 * 60), 10, 30))   // same values, 5 min later: skipped
        assertEquals(1, h.size)
        h = History.add(h, snap(now.plusSeconds(6 * 60), 12, 30))   // changed: kept
        h = History.add(h, snap(now.plusSeconds(30 * 60), 12, 30))  // unchanged but 24 min later: kept
        assertEquals(listOf(10, 12, 12), h.map { it.session })
    }

    @Test fun historyDropsOlderThanSevenDays() {
        var h = History.add(emptyList(), snap(now, 1, 1))
        h = History.add(h, snap(now.plus(Duration.ofDays(8)), 2, 2))
        assertEquals(listOf(2), h.map { it.session })
    }

    @Test fun historyJsonRoundTrip() {
        val h = listOf(HistoryPoint(Instant.ofEpochSecond(100), 5, null), HistoryPoint(Instant.ofEpochSecond(200), 6, 40))
        assertEquals(h, History.fromJson(History.toJson(h)))
        assertEquals(emptyList<HistoryPoint>(), History.fromJson("{broken"))
        assertEquals(emptyList<HistoryPoint>(), History.fromJson(null))
    }
}
