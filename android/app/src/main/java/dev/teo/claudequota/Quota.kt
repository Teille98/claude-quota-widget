package dev.teo.claudequota

import org.json.JSONArray
import org.json.JSONObject
import java.time.Duration
import java.time.Instant
import java.time.OffsetDateTime
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.Locale
import kotlin.math.roundToInt

// Kotlin port of ClaudeQuota.Core (Windows): same format, same rules.

/** UI texts are in English, or in French when the system language is French. */
internal fun Locale.isFrench() = language == "fr"

data class QuotaLimit(
    val kind: String,
    val percent: Int,
    val resetsAt: Instant?,
    val severity: String?,
    val model: String?,
) {
    fun label(locale: Locale = Locale.getDefault()): String {
        val fr = locale.isFrench()
        return when (kind) {
            "session" -> if (fr) "Session (5 h)" else "Session (5h)"
            "weekly_all" -> if (fr) "Semaine · tous les modèles" else "Week · all models"
            "weekly_scoped" -> if (fr) "Semaine · ${model ?: "modèle"}" else "Week · ${model ?: "model"}"
            else -> kind
        }
    }

    fun shortLabel(locale: Locale = Locale.getDefault()): String {
        val fr = locale.isFrench()
        return when (kind) {
            "session" -> if (fr) "5 h" else "5h"
            "weekly_all" -> if (fr) "7 j" else "7d"
            else -> model ?: kind
        }
    }
}

data class Snapshot(val limits: List<QuotaLimit>, val fetchedAt: Instant) {
    val session get() = limits.firstOrNull { it.kind == "session" }
    val weekly get() = limits.firstOrNull { it.kind == "weekly_all" }
    val highest get() = limits.maxByOrNull { it.percent }

    fun toJson(): String = JSONObject()
        .put("fetchedAt", fetchedAt.toEpochMilli())
        .put("limits", JSONArray(limits.map { l ->
            JSONObject()
                .put("kind", l.kind)
                .put("percent", l.percent)
                .put("resetsAt", l.resetsAt?.toEpochMilli() ?: JSONObject.NULL)
                .put("severity", l.severity ?: JSONObject.NULL)
                .put("model", l.model ?: JSONObject.NULL)
        }))
        .toString()

    companion object {
        /** Reads the cache back; null if missing or corrupt. */
        fun fromJson(s: String?): Snapshot? = try {
            if (s.isNullOrBlank()) null else {
                val o = JSONObject(s)
                val arr = o.getJSONArray("limits")
                Snapshot(
                    (0 until arr.length()).map { i ->
                        val l = arr.getJSONObject(i)
                        QuotaLimit(
                            kind = l.getString("kind"),
                            percent = l.getInt("percent"),
                            resetsAt = if (l.isNull("resetsAt")) null else Instant.ofEpochMilli(l.getLong("resetsAt")),
                            severity = l.str("severity"),
                            model = l.str("model"),
                        )
                    },
                    Instant.ofEpochMilli(o.getLong("fetchedAt")),
                )
            }
        } catch (_: Exception) {
            null
        }
    }
}

enum class FetchError { NONE, AUTH, OFFLINE, BAD_FORMAT, NO_LIMITS, TIMEOUT, UNKNOWN }

/** [org] is the organization name sent by the script: only a default account label, never logged or exported. */
data class FetchResult(val snapshot: Snapshot?, val error: FetchError, val detail: String? = null, val org: String? = null) {
    val ok get() = snapshot != null && error == FetchError.NONE

    companion object {
        fun fail(error: FetchError, detail: String? = null) = FetchResult(null, error, detail)
    }
}

enum class Level { NORMAL, WARNING, CRITICAL }

object Thresholds {
    const val WARNING = 50
    const val CRITICAL = 80
    fun of(percent: Int) = when {
        percent >= CRITICAL -> Level.CRITICAL
        percent >= WARNING -> Level.WARNING
        else -> Level.NORMAL
    }
}

/** Parses the JSON message posted by shared/usage-script.js. */
object UsageParser {
    fun parse(json: String?, now: Instant): FetchResult {
        if (json.isNullOrBlank()) return FetchResult.fail(FetchError.BAD_FORMAT, "empty response")
        val o = try {
            JSONObject(json)
        } catch (_: Exception) {
            return FetchResult.fail(FetchError.BAD_FORMAT, "unreadable JSON")
        }
        if (!o.optBoolean("ok", false)) {
            val error = when (o.str("reason")) {
                "auth" -> FetchError.AUTH
                "offline" -> FetchError.OFFLINE
                "no-limits" -> FetchError.NO_LIMITS
                "timeout" -> FetchError.TIMEOUT
                else -> FetchError.UNKNOWN
            }
            return FetchResult.fail(error, o.str("message"))
        }
        val arr = o.optJSONArray("limits") ?: return FetchResult.fail(FetchError.BAD_FORMAT, "missing limits")
        val limits = (0 until arr.length()).mapNotNull { i ->
            val l = arr.optJSONObject(i) ?: return@mapNotNull null
            val kind = l.str("kind") ?: return@mapNotNull null
            if (l.isNull("percent") || !l.has("percent")) return@mapNotNull null
            val pct = l.optDouble("percent", Double.NaN)
            if (pct.isNaN()) return@mapNotNull null
            QuotaLimit(
                kind = kind,
                percent = pct.roundToInt().coerceIn(0, 100),
                resetsAt = l.str("resets_at")?.let { s -> runCatching { OffsetDateTime.parse(s).toInstant() }.getOrNull() },
                severity = l.str("severity"),
                model = l.str("model"),
            )
        }
        if (limits.isEmpty()) return FetchResult.fail(FetchError.BAD_FORMAT, "no usable limit")
        return FetchResult(Snapshot(limits, now), FetchError.NONE, org = o.str("org")?.trim()?.takeIf { it.isNotEmpty() })
    }
}

/** Short widget texts, in English or French. */
object TimeText {
    private val HM = DateTimeFormatter.ofPattern("HH:mm")

    fun duration(d: Duration, locale: Locale = Locale.getDefault()): String {
        val m = d.toMinutes().coerceAtLeast(0)
        if (m < 60) return "$m min"
        val h = m / 60
        val r = m % 60
        val sep = if (locale.isFrench()) " " else ""
        return if (r == 0L) "$h${sep}h" else "$h${sep}h ${"%02d".format(r)}"
    }

    /** "↺ in 1h 40 · 15:49" within 24 hours, "↺ Monday 19:59" otherwise. */
    fun reset(at: Instant?, now: Instant, zone: ZoneId = ZoneId.systemDefault(), locale: Locale = Locale.getDefault()): String {
        if (at == null) return ""
        val fr = locale.isFrench()
        val d = Duration.between(now, at)
        if (d.toMinutes() < 1) return if (fr) "↺ imminente" else "↺ imminent"
        val local = at.atZone(zone)
        return if (d < Duration.ofHours(24)) "↺ ${if (fr) "dans" else "in"} ${duration(d, locale)} · ${local.format(HM)}"
        else "↺ ${local.format(DateTimeFormatter.ofPattern("EEEE HH:mm", if (fr) Locale.FRENCH else Locale.ENGLISH))}"
    }

    fun age(at: Instant, now: Instant, locale: Locale = Locale.getDefault()): String {
        val s = Duration.between(at, now).seconds.coerceAtLeast(0)
        return if (locale.isFrench()) when {
            s < 60 -> "à l'instant"
            s < 3600 -> "il y a ${s / 60} min"
            s < 86400 -> "il y a ${s / 3600} h"
            else -> "il y a ${s / 86400} j"
        } else when {
            s < 60 -> "just now"
            s < 3600 -> "${s / 60} min ago"
            s < 86400 -> "${s / 3600}h ago"
            else -> "${s / 86400}d ago"
        }
    }
}

/** optString returns "null" for JSONObject.NULL: we want a real null. */
internal fun JSONObject.str(key: String): String? =
    if (!has(key) || isNull(key)) null else optString(key).ifBlank { null }

/** One history sample: session and weekly percentages at a given time. */
data class HistoryPoint(val at: Instant, val session: Int?, val weekly: Int?)

/** 7-day usage history, kept small: one point per change, or per 15 min when nothing changes. */
object History {
    val KEEP: Duration = Duration.ofDays(7)
    private val MIN_GAP: Duration = Duration.ofMinutes(15)

    fun add(points: List<HistoryPoint>, s: Snapshot): List<HistoryPoint> {
        val p = HistoryPoint(s.fetchedAt, s.session?.percent, s.weekly?.percent)
        val last = points.lastOrNull()
        val keep = last == null || last.session != p.session || last.weekly != p.weekly ||
            Duration.between(last.at, p.at) >= MIN_GAP
        val all = if (keep) points + p else points
        return all.filter { Duration.between(it.at, p.at) <= KEEP }
    }

    fun toJson(points: List<HistoryPoint>): String = JSONArray().apply {
        for (p in points) put(JSONArray().put(p.at.epochSecond).put(p.session ?: -1).put(p.weekly ?: -1))
    }.toString()

    fun fromJson(json: String?): List<HistoryPoint> = runCatching {
        val a = JSONArray(json ?: return emptyList())
        (0 until a.length()).map {
            val e = a.getJSONArray(it)
            HistoryPoint(Instant.ofEpochSecond(e.getLong(0)), e.getInt(1).takeIf { v -> v >= 0 }, e.getInt(2).takeIf { v -> v >= 0 })
        }
    }.getOrDefault(emptyList())
}
