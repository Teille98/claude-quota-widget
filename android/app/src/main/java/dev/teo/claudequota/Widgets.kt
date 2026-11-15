package dev.teo.claudequota

import android.app.PendingIntent
import android.appwidget.AppWidgetManager
import android.appwidget.AppWidgetProvider
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.res.ColorStateList
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Paint
import android.graphics.RectF
import android.util.SizeF
import android.view.View
import android.widget.RemoteViews
import java.time.Instant
import java.util.Locale

/** The widget: 2×1 (two rings), 4×2 (details) and 5×2 (three columns), picked by Android from the size. */
class QuotaWidget : AppWidgetProvider() {
    override fun onUpdate(ctx: Context, mgr: AppWidgetManager, ids: IntArray) {
        mgr.updateAppWidget(ids, Widgets.views(ctx))
        RefreshWorker.schedule(ctx)
    }

    override fun onEnabled(ctx: Context) {
        RefreshWorker.schedule(ctx)
        RefreshWorker.runNow(ctx)
    }

    override fun onDisabled(ctx: Context) {
        RefreshWorker.cancel(ctx)
    }

    override fun onReceive(ctx: Context, intent: Intent) {
        if (intent.action == Widgets.ACTION_REFRESH) RefreshWorker.runNow(ctx)
        else super.onReceive(ctx, intent)
    }
}

object Widgets {
    const val ACTION_REFRESH = "dev.teo.claudequota.REFRESH"

    fun updateAll(ctx: Context) {
        val mgr = AppWidgetManager.getInstance(ctx)
        val ids = mgr.getAppWidgetIds(ComponentName(ctx, QuotaWidget::class.java))
        if (ids.isNotEmpty()) mgr.updateAppWidget(ids, views(ctx))
    }

    /** Sizes in dp: Android picks the largest one that fits the widget. */
    fun views(ctx: Context) = RemoteViews(
        mapOf(
            SizeF(100f, 40f) to small(ctx),
            SizeF(220f, 110f) to large(ctx),
            SizeF(320f, 110f) to detailed(ctx, R.layout.widget_wide, wide = true), // 5×2: three columns
        )
    )

    private class State(ctx: Context) {
        val accounts = Accounts(ctx)
        val account = accounts.selected
        /** Account name shown in the header when there are several accounts. */
        val label = if (accounts.list.size > 1) AccountModel.shortName(accounts.displayName(account)) else null
        val store = Store(ctx, account.id)
        val snap = store.snapshot
        val error = store.error
        val busy = store.busy
        val stale = error != FetchError.NONE
        val needsLogin = error == FetchError.AUTH || (snap == null && error == FetchError.NO_LIMITS)
    }

    private fun color(ctx: Context, level: Level, stale: Boolean) = ctx.getColor(
        if (stale) R.color.muted else when (level) {
            Level.CRITICAL -> R.color.critical
            Level.WARNING -> R.color.warning
            Level.NORMAL -> R.color.normal
        }
    )

    private fun openIntent(ctx: Context, s: State): PendingIntent {
        val target = if (s.needsLogin) LoginActivity.intent(ctx, s.account.id) else Intent(ctx, MainActivity::class.java)
        return PendingIntent.getActivity(
            ctx, if (s.needsLogin) 2 else 1, target.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        )
    }

    private fun refreshIntent(ctx: Context) = PendingIntent.getBroadcast(
        ctx, 0, Intent(ctx, QuotaWidget::class.java).setAction(ACTION_REFRESH), PendingIntent.FLAG_IMMUTABLE
    )

    private fun small(ctx: Context): RemoteViews {
        val s = State(ctx)
        val v = RemoteViews(ctx.packageName, R.layout.widget_small)
        v.setOnClickPendingIntent(R.id.root, openIntent(ctx, s))
        val snap = s.snap
        if (snap == null) {
            v.setViewVisibility(R.id.rings, View.GONE)
            v.setViewVisibility(R.id.message, View.VISIBLE)
            v.setTextViewText(R.id.message, message(ctx, s))
            return v
        }
        v.setViewVisibility(R.id.rings, View.VISIBLE)
        v.setViewVisibility(R.id.message, View.GONE)
        val size = (40 * ctx.resources.displayMetrics.density).toInt()
        for ((limit, ring, pct) in listOf(
            Triple(snap.session, R.id.ring1, R.id.pct1),
            Triple(snap.weekly, R.id.ring2, R.id.pct2),
        )) {
            val p = limit?.percent
            v.setImageViewBitmap(ring, ring(ctx, p, color(ctx, Thresholds.of(p ?: 0), s.stale), size))
            v.setTextViewText(pct, p?.toString() ?: "–")
        }
        v.setViewVisibility(R.id.dot, if (s.needsLogin) View.VISIBLE else View.GONE)
        return v
    }

    /** The 4×2 panel, reused as is on the app screen. */
    fun panel(ctx: Context) = large(ctx)

    private fun large(ctx: Context) = detailed(ctx, R.layout.widget_large, wide = false)

    private fun detailed(ctx: Context, layout: Int, wide: Boolean): RemoteViews {
        val s = State(ctx)
        val now = Instant.now()
        val loc = ctx.locale()
        val v = RemoteViews(ctx.packageName, layout)
        v.setOnClickPendingIntent(R.id.root, openIntent(ctx, s))
        v.setOnClickPendingIntent(R.id.refresh, refreshIntent(ctx))
        val snap = s.snap
        val age = when {
            s.busy -> ctx.getString(R.string.reading)
            snap != null -> TimeText.age(snap.fetchedAt, now, loc)
            else -> ""
        }
        v.setTextViewText(R.id.age, listOfNotNull(s.label, age.ifEmpty { null }).joinToString(" · "))
        val banner = if (snap == null || s.stale) message(ctx, s) else null
        v.setViewVisibility(R.id.banner, if (banner != null) View.VISIBLE else View.GONE)
        v.setTextViewText(R.id.banner, banner ?: "")
        v.setTextColor(R.id.banner, ctx.getColor(if (s.needsLogin) R.color.warning else R.color.muted))

        val rows = listOf(
            Row(R.id.row1, R.id.label1, R.id.value1, R.id.bar1, R.id.reset1, snap?.session),
            Row(R.id.row2, R.id.label2, R.id.value2, R.id.bar2, R.id.reset2, snap?.weekly),
        ) + if (wide) listOf(
            Row(R.id.row3, R.id.label3, R.id.value3, R.id.bar3, R.id.reset3, snap?.limits?.firstOrNull { it.kind == "weekly_scoped" })
        ) else emptyList()
        for (r in rows) {
            val l = r.limit
            v.setViewVisibility(r.row, if (l != null) View.VISIBLE else View.GONE)
            if (l == null) continue
            val c = color(ctx, Thresholds.of(l.percent), s.stale)
            v.setTextViewText(r.label, if (wide) columnLabel(ctx, l) else l.label(loc))
            v.setTextViewText(r.value, ctx.getString(R.string.percent, l.percent))
            v.setTextColor(r.value, c)
            v.setProgressBar(r.bar, 100, l.percent, false)
            v.setColorStateList(r.bar, "setProgressTintList", ColorStateList.valueOf(c))
            v.setTextViewText(r.reset, TimeText.reset(l.resetsAt, now, locale = loc))
        }
        return v
    }

    /** Short labels for the 5×2 columns. */
    private fun columnLabel(ctx: Context, l: QuotaLimit) = when (l.kind) {
        "session" -> ctx.getString(R.string.col_session)
        "weekly_all" -> ctx.getString(R.string.col_week)
        "weekly_scoped" -> ctx.getString(R.string.col_model, l.model ?: ctx.getString(R.string.model_fallback))
        else -> l.kind
    }

    private data class Row(val row: Int, val label: Int, val value: Int, val bar: Int, val reset: Int, val limit: QuotaLimit?)

    private fun message(ctx: Context, s: State) = ctx.getString(
        when {
            s.needsLogin -> R.string.msg_login
            s.busy -> R.string.msg_busy
            s.error == FetchError.OFFLINE || s.error == FetchError.TIMEOUT -> R.string.msg_offline
            s.error == FetchError.BAD_FORMAT || s.error == FetchError.NO_LIMITS -> R.string.msg_format
            s.snap == null -> R.string.msg_first
            else -> R.string.msg_error
        }
    )

    /** Ring filled to [percent]% (like the Windows tray icon). */
    fun ring(ctx: Context, percent: Int?, color: Int, size: Int): Bitmap {
        val bmp = Bitmap.createBitmap(size, size, Bitmap.Config.ARGB_8888)
        val c = Canvas(bmp)
        val w = size * 0.125f
        val r = RectF(w / 2, w / 2, size - w / 2, size - w / 2)
        val p = Paint(Paint.ANTI_ALIAS_FLAG).apply { style = Paint.Style.STROKE; strokeWidth = w }
        p.color = ctx.getColor(R.color.track)
        c.drawOval(r, p)
        if (percent != null && percent > 0) {
            p.color = color
            p.strokeCap = Paint.Cap.ROUND
            c.drawArc(r, -90f, maxOf(4f, 360f * percent / 100f), false, p)
        }
        return bmp
    }
}

/** Locale of the current resources (follows the system language). */
fun Context.locale(): Locale = resources.configuration.locales[0]
