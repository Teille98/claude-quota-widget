package dev.teo.claudequota

import android.content.Context
import android.graphics.Canvas
import android.graphics.DashPathEffect
import android.graphics.Paint
import android.graphics.Path
import android.view.View
import java.time.Instant
import java.time.ZoneId
import java.time.format.TextStyle
import java.time.temporal.ChronoUnit

/** 7-day chart: weekly usage (thick line) and session usage (thin line), with the 50 % / 80 % thresholds. */
class HistoryChart(ctx: Context) : View(ctx) {
    var points: List<HistoryPoint> = emptyList()
        set(v) { field = v; invalidate() }

    private val d = resources.displayMetrics.density
    private val text = Paint(Paint.ANTI_ALIAS_FLAG).apply { textSize = 11 * d; color = ctx.getColor(R.color.muted) }
    private val grid = Paint(Paint.ANTI_ALIAS_FLAG).apply { strokeWidth = d; color = ctx.getColor(R.color.track) }
    private val threshold = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        strokeWidth = d; style = Paint.Style.STROKE; pathEffect = DashPathEffect(floatArrayOf(4 * d, 4 * d), 0f)
    }
    private val line = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE; strokeJoin = Paint.Join.ROUND; strokeCap = Paint.Cap.ROUND
    }

    override fun onDraw(c: Canvas) {
        val left = 30 * d; val right = width - 4 * d; val top = 6 * d; val bottom = height - 34 * d
        val now = Instant.now()
        val start = now.minus(History.KEEP)
        fun x(t: Instant) = left + (right - left) * (ChronoUnit.SECONDS.between(start, t).toFloat() / History.KEEP.seconds)
        fun y(p: Int) = bottom - (bottom - top) * p / 100f

        for (p in listOf(0, 50, 100)) {
            c.drawLine(left, y(p), right, y(p), grid)
            c.drawText("$p", 0f, y(p) + 4 * d, text)
        }
        threshold.color = context.getColor(R.color.warning); c.drawLine(left, y(50), right, y(50), threshold)
        threshold.color = context.getColor(R.color.critical); c.drawLine(left, y(80), right, y(80), threshold)

        // Day labels at local midnights.
        val zone = ZoneId.systemDefault()
        var day = start.atZone(zone).toLocalDate().plusDays(1)
        while (!day.atStartOfDay(zone).toInstant().isAfter(now)) {
            val t = day.atStartOfDay(zone).toInstant()
            c.drawLine(x(t), top, x(t), bottom, grid)
            c.drawText(day.dayOfWeek.getDisplayName(TextStyle.SHORT, context.locale()), x(t) + 2 * d, bottom + 13 * d, text)
            day = day.plusDays(1)
        }

        if (points.size < 2) {
            c.drawText(context.getString(R.string.history_empty), left + 8 * d, top + (bottom - top) / 2, text)
        } else {
            series(c, { it.session }, context.getColor(R.color.muted), 1.5f * d, ::x, ::y)
            series(c, { it.weekly }, context.getColor(R.color.normal), 2.5f * d, ::x, ::y)
        }

        // Legend.
        val ly = height - 6 * d
        var lx = left
        for ((label, color, w) in listOf(
            Triple(context.getString(R.string.history_week), context.getColor(R.color.normal), 2.5f * d),
            Triple(context.getString(R.string.history_session), context.getColor(R.color.muted), 1.5f * d),
        )) {
            line.color = color; line.strokeWidth = w
            c.drawLine(lx, ly - 4 * d, lx + 14 * d, ly - 4 * d, line)
            c.drawText(label, lx + 18 * d, ly, text)
            lx += 18 * d + text.measureText(label) + 16 * d
        }
    }

    /** Draws one series; a gap of more than an hour (phone off, offline) breaks the line. */
    private fun series(c: Canvas, value: (HistoryPoint) -> Int?, color: Int, w: Float, x: (Instant) -> Float, y: (Int) -> Float) {
        line.color = color; line.strokeWidth = w
        val path = Path()
        var prev: HistoryPoint? = null
        for (p in points) {
            val v = value(p) ?: run { prev = null; null } ?: continue
            val gap = prev?.let { ChronoUnit.MINUTES.between(it.at, p.at) > 60 } ?: true
            if (gap) path.moveTo(x(p.at), y(v)) else path.lineTo(x(p.at), y(v))
            prev = p
        }
        c.drawPath(path, line)
    }
}
