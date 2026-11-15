package dev.teo.claudequota

import android.content.Context
import androidx.work.Constraints
import androidx.work.CoroutineWorker
import androidx.work.ExistingPeriodicWorkPolicy
import androidx.work.ExistingWorkPolicy
import androidx.work.NetworkType
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.OutOfQuotaPolicy
import androidx.work.PeriodicWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import java.time.Instant
import java.util.concurrent.TimeUnit

/** One background quota read, then widget update and threshold notifications. */
class RefreshWorker(ctx: Context, params: WorkerParameters) : CoroutineWorker(ctx, params) {
    override suspend fun doWork(): Result {
        refresh(applicationContext, if (NOW in tags) "immediate" else "periodic")
        return Result.success()
    }

    companion object {
        private const val PERIODIC = "quota-periodic"
        private const val NOW = "quota-now"

        suspend fun refresh(ctx: Context, origin: String) {
            val store = Store(ctx)
            store.busy = true
            Widgets.updateAll(ctx)
            val t0 = System.currentTimeMillis()
            val r = UsageParser.parse(QuotaReader.read(ctx), Instant.now())
            val screen = if (ctx.getSystemService(android.os.PowerManager::class.java).isInteractive) "screen on" else "screen off"
            store.log("[$origin] " + (if (r.ok) "ok ${r.snapshot!!.session?.percent ?: "–"} %" else r.error.name) +
                " · ${System.currentTimeMillis() - t0} ms · $screen")
            val prevError = store.error
            val before = store.apply(r)
            if (r.ok) Notifier.thresholds(ctx, before, r.snapshot!!)
            else if (r.error == FetchError.AUTH && prevError != FetchError.AUTH) Notifier.authExpired(ctx)
            Widgets.updateAll(ctx)
        }

        /** Android runs it at best every 15 min (enforced minimum), only with a network. */
        fun schedule(ctx: Context) = WorkManager.getInstance(ctx).enqueueUniquePeriodicWork(
            PERIODIC, ExistingPeriodicWorkPolicy.KEEP,
            PeriodicWorkRequestBuilder<RefreshWorker>(15, TimeUnit.MINUTES)
                .setConstraints(Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build())
                .build()
        )

        fun cancel(ctx: Context) = WorkManager.getInstance(ctx).cancelUniqueWork(PERIODIC)

        /** Immediate read (refresh button, app opened). */
        fun runNow(ctx: Context) = WorkManager.getInstance(ctx).enqueueUniqueWork(
            NOW, ExistingWorkPolicy.KEEP,
            OneTimeWorkRequestBuilder<RefreshWorker>()
                .addTag(NOW)
                .setExpedited(OutOfQuotaPolicy.RUN_AS_NON_EXPEDITED_WORK_REQUEST)
                .build()
        )
    }
}
