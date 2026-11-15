package dev.teo.claudequota

import android.Manifest
import android.app.Activity
import android.appwidget.AppWidgetManager
import android.content.ComponentName
import android.content.Intent
import android.content.SharedPreferences
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Bundle
import android.os.PowerManager
import android.provider.Settings
import android.view.View
import android.view.ViewGroup.LayoutParams.MATCH_PARENT
import android.view.ViewGroup.LayoutParams.WRAP_CONTENT
import android.widget.Button
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import java.time.Duration
import java.time.Instant

/** App screen: the same panel as the 4×2 widget, sign-in and useful settings. */
class MainActivity : Activity() {
    private lateinit var panel: FrameLayout
    private lateinit var battery: Button
    private lateinit var chart: HistoryChart
    private val store by lazy { Store(this) }
    private val listener = SharedPreferences.OnSharedPreferenceChangeListener { _, _ -> runOnUiThread { render() } }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        actionBar?.hide()
        val pad = dp(20)
        val col = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(pad, pad, pad, pad) }
        col.addView(TextView(this).apply {
            text = getString(R.string.app_name); textSize = 24f; setTextColor(getColor(R.color.text))
            setPadding(0, 0, 0, dp(16))
        })
        panel = FrameLayout(this)
        col.addView(panel, LinearLayout.LayoutParams(MATCH_PARENT, WRAP_CONTENT))

        fun button(text: String, action: () -> Unit) = Button(this).apply {
            this.text = text
            setOnClickListener { action() }
            col.addView(this, LinearLayout.LayoutParams(MATCH_PARENT, WRAP_CONTENT).apply { topMargin = dp(8) })
        }
        col.addView(View(this), LinearLayout.LayoutParams(1, dp(12)))
        button(getString(R.string.btn_refresh)) { RefreshWorker.runNow(this) }
        button(getString(R.string.btn_login)) { startActivity(Intent(this, LoginActivity::class.java)) }
        button(getString(R.string.btn_pin)) { pinWidget() }
        battery = button(getString(R.string.btn_battery)) { askBattery() }
        col.addView(TextView(this).apply {
            text = getString(R.string.history_title); textSize = 16f; setTextColor(getColor(R.color.text))
            setPadding(0, dp(24), 0, dp(8))
        })
        chart = HistoryChart(this)
        col.addView(chart, LinearLayout.LayoutParams(MATCH_PARENT, dp(160)))
        col.addView(TextView(this).apply {
            text = getString(R.string.main_note)
            textSize = 13f; setTextColor(getColor(R.color.muted)); setPadding(0, dp(16), 0, 0)
        })
        val scroll = ScrollView(this).apply { addView(col) }
        setContentView(scroll)
        Insets.pad(scroll)

        Notifier.ensureChannel(this)
        if (checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED)
            requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 1)
        RefreshWorker.schedule(this)
    }

    private fun render() {
        panel.removeAllViews()
        panel.addView(Widgets.panel(this).apply(this, panel))
        chart.points = store.history7d
        battery.visibility = if (getSystemService(PowerManager::class.java).isIgnoringBatteryOptimizations(packageName)) View.GONE else View.VISIBLE
    }

    override fun onResume() {
        super.onResume()
        store.prefs.registerOnSharedPreferenceChangeListener(listener)
        render()
        val snap = store.snapshot
        if (!store.busy && (snap == null || Duration.between(snap.fetchedAt, Instant.now()) > Duration.ofMinutes(1)))
            RefreshWorker.runNow(this)
    }

    override fun onPause() {
        store.prefs.unregisterOnSharedPreferenceChangeListener(listener)
        super.onPause()
    }

    private fun pinWidget() {
        val mgr = getSystemService(AppWidgetManager::class.java)
        if (mgr.isRequestPinAppWidgetSupported) mgr.requestPinAppWidget(ComponentName(this, QuotaWidget::class.java), null, null)
    }

    /** OnePlus / ColorOS readily kill background work: ask for the battery optimization exemption. */
    private fun askBattery() {
        startActivity(Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, Uri.parse("package:$packageName")))
    }

    private fun dp(v: Int) = (v * resources.displayMetrics.density).toInt()
}
