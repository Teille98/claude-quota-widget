package dev.teo.claudequota

import android.Manifest
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import java.time.Instant

/** Notifications: crossing 80% (once per crossing) and expired claude.ai session. */
object Notifier {
    private const val CHANNEL = "quota"

    fun ensureChannel(ctx: Context) {
        val nm = ctx.getSystemService(NotificationManager::class.java)
        if (nm.getNotificationChannel(CHANNEL) == null)
            nm.createNotificationChannel(NotificationChannel(CHANNEL, ctx.getString(R.string.channel_name), NotificationManager.IMPORTANCE_DEFAULT))
    }

    /** [label] = account name, only when there are several accounts. */
    fun thresholds(ctx: Context, account: Account, label: String?, before: Snapshot?, after: Snapshot) {
        if (before == null) return
        for (l in after.limits) {
            val prev = before.limits.firstOrNull { it.kind == l.kind && it.model == l.model } ?: continue
            if (prev.percent < Thresholds.CRITICAL && l.percent >= Thresholds.CRITICAL)
                post(
                    ctx, "${account.id}/${l.kind}/${l.model}".hashCode(),
                    prefixed(label, ctx.getString(R.string.notif_threshold, l.label(ctx.locale()), l.percent)),
                    TimeText.reset(l.resetsAt, Instant.now(), locale = ctx.locale()), Intent(ctx, MainActivity::class.java)
                )
        }
    }

    fun authExpired(ctx: Context, account: Account, label: String?) =
        post(
            ctx, "${account.id}/auth".hashCode(), prefixed(label, ctx.getString(R.string.app_name)),
            ctx.getString(R.string.notif_auth), LoginActivity.intent(ctx, account.id)
        )

    private fun prefixed(label: String?, text: String) = if (label == null) text else "$label · $text"

    private fun post(ctx: Context, id: Int, title: String, text: String, target: Intent) {
        if (ctx.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) return
        ensureChannel(ctx)
        val pi = PendingIntent.getActivity(
            ctx, id, target.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        )
        val n = android.app.Notification.Builder(ctx, CHANNEL)
            .setSmallIcon(R.drawable.ic_ring)
            .setContentTitle(title)
            .setContentText(text)
            .setContentIntent(pi)
            .setAutoCancel(true)
            .build()
        ctx.getSystemService(NotificationManager::class.java).notify(id, n)
    }
}
