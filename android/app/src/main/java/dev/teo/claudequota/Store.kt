package dev.teo.claudequota

import android.content.Context
import android.content.SharedPreferences

/** Last known values + state of the last read. No session data here (it stays in the WebView). */
class Store(ctx: Context) {
    val prefs: SharedPreferences = ctx.applicationContext.getSharedPreferences(NAME, Context.MODE_PRIVATE)

    val snapshot: Snapshot? get() = Snapshot.fromJson(prefs.getString(K_SNAP, null))

    val error: FetchError
        get() = runCatching { FetchError.valueOf(prefs.getString(K_ERR, null) ?: "") }.getOrDefault(FetchError.NONE)

    /** A read is in progress; expires on its own after 60 s (process killed mid-read). */
    var busy: Boolean
        get() = System.currentTimeMillis() - prefs.getLong(K_BUSY, 0) < 60_000
        set(v) = prefs.edit().putLong(K_BUSY, if (v) System.currentTimeMillis() else 0).apply()

    /** Applies a read result. Returns the previous value (for threshold notifications). */
    fun apply(r: FetchResult): Snapshot? {
        val before = snapshot
        prefs.edit().apply {
            if (r.ok) {
                putString(K_SNAP, r.snapshot!!.toJson())
                putString(K_HIST, History.toJson(History.add(history7d, r.snapshot)))
                putString(K_ERR, FetchError.NONE.name)
            } else {
                putString(K_ERR, r.error.name)
            }
            putLong(K_BUSY, 0)
        }.apply()
        return before
    }

    /** Session / weekly percentages over the last 7 days (shown as a chart in the app). */
    val history7d: List<HistoryPoint> get() = History.fromJson(prefs.getString(K_HIST, null))

    /** Log of the last 100 reads (time, origin, result; nothing sensitive), readable with adb run-as. */
    val history: List<String> get() = prefs.getString(K_LOG, "")!!.lines().filter { it.isNotBlank() }

    fun log(line: String) {
        val stamp = java.time.LocalDateTime.now().withNano(0).toString().replace('T', ' ')
        prefs.edit().putString(K_LOG, (history + "$stamp $line").takeLast(100).joinToString("\n")).apply()
    }

    companion object {
        const val NAME = "quota"
        private const val K_LOG = "history" // read log (kept under its original key)
        private const val K_HIST = "history7d"
        private const val K_SNAP = "snapshot"
        private const val K_ERR = "error"
        private const val K_BUSY = "busy"
    }
}
