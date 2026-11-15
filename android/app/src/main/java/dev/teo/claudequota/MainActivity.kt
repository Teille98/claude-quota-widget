package dev.teo.claudequota

import android.Manifest
import android.app.Activity
import android.app.AlertDialog
import android.appwidget.AppWidgetManager
import android.content.ComponentName
import android.content.Intent
import android.content.SharedPreferences
import android.content.pm.PackageManager
import android.graphics.Typeface
import android.net.Uri
import android.os.Bundle
import android.os.PowerManager
import android.provider.Settings
import android.view.View
import android.view.ViewGroup.LayoutParams.MATCH_PARENT
import android.view.ViewGroup.LayoutParams.WRAP_CONTENT
import android.widget.Button
import android.widget.EditText
import android.widget.FrameLayout
import android.widget.HorizontalScrollView
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import java.time.Duration
import java.time.Instant

/**
 * App screen: account tabs (when there are several accounts), the same panel as the 4×2 widget,
 * the 7-day chart, sign-in and useful settings. Everything applies to the selected account,
 * which is also the one shown by the widget.
 */
class MainActivity : Activity() {
    private lateinit var tabsScroll: HorizontalScrollView
    private lateinit var tabs: LinearLayout
    private lateinit var panel: FrameLayout
    private lateinit var battery: Button
    private lateinit var remove: Button
    private lateinit var chart: HistoryChart
    private val accounts by lazy { Accounts(this) }
    private val prefs by lazy { Store(this).prefs }
    private val listener = SharedPreferences.OnSharedPreferenceChangeListener { _, _ -> runOnUiThread { render() } }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        actionBar?.hide()
        val pad = dp(20)
        val col = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(pad, pad, pad, pad) }
        col.addView(TextView(this).apply {
            text = getString(R.string.app_name); textSize = 24f; setTextColor(getColor(R.color.text))
            setPadding(0, 0, 0, dp(12))
        })
        tabs = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        tabsScroll = HorizontalScrollView(this).apply { isHorizontalScrollBarEnabled = false; addView(tabs) }
        col.addView(tabsScroll, LinearLayout.LayoutParams(MATCH_PARENT, WRAP_CONTENT).apply { bottomMargin = dp(8) })
        panel = FrameLayout(this)
        col.addView(panel, LinearLayout.LayoutParams(MATCH_PARENT, WRAP_CONTENT))

        fun button(text: String, action: () -> Unit) = Button(this).apply {
            this.text = text
            setOnClickListener { action() }
            col.addView(this, LinearLayout.LayoutParams(MATCH_PARENT, WRAP_CONTENT).apply { topMargin = dp(8) })
        }
        col.addView(View(this), LinearLayout.LayoutParams(1, dp(12)))
        button(getString(R.string.btn_refresh)) { RefreshWorker.runNow(this) }
        button(getString(R.string.btn_login)) { startActivity(LoginActivity.intent(this, accounts.selected.id)) }
        button(getString(R.string.btn_add_account)) { addAccount() }.visibility =
            if (Accounts.multiProfile) View.VISIBLE else View.GONE
        remove = button(getString(R.string.btn_remove_account)) { confirmRemove() }
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
        val all = accounts.list
        val selected = accounts.selected
        renderTabs(all, selected)
        panel.removeAllViews()
        panel.addView(Widgets.panel(this).apply(this, panel))
        chart.points = Store(this, selected.id).history7d
        remove.visibility = if (selected.isDefault) View.GONE else View.VISIBLE
        battery.visibility = if (getSystemService(PowerManager::class.java).isIgnoringBatteryOptimizations(packageName)) View.GONE else View.VISIBLE
    }

    /** One tab per account; tap = select (app and widget), long press = rename. Hidden with a single account. */
    private fun renderTabs(all: List<Account>, selected: Account) {
        tabs.removeAllViews()
        tabsScroll.visibility = if (all.size > 1) View.VISIBLE else View.GONE
        if (all.size < 2) return
        for (a in all) {
            val on = a.id == selected.id
            tabs.addView(Button(this).apply {
                text = accounts.displayName(a)
                isAllCaps = false
                setTextColor(getColor(if (on) R.color.normal else R.color.muted))
                setTypeface(typeface, if (on) Typeface.BOLD else Typeface.NORMAL)
                setOnClickListener { select(a) }
                setOnLongClickListener { rename(a); true }
            }, LinearLayout.LayoutParams(WRAP_CONTENT, WRAP_CONTENT).apply { marginEnd = dp(6) })
        }
    }

    private fun select(a: Account) {
        accounts.selected = a
        Widgets.updateAll(this)
        render()
        if (Store(this, a.id).snapshot == null) RefreshWorker.runNow(this)
    }

    private fun addAccount() {
        val a = accounts.add()
        accounts.selected = a
        Widgets.updateAll(this)
        startActivity(LoginActivity.intent(this, a.id))
    }

    private fun rename(a: Account) {
        val input = EditText(this).apply { setText(accounts.displayName(a)); setSelection(text.length); isSingleLine = true }
        val box = FrameLayout(this).apply { setPadding(dp(20), dp(8), dp(20), 0); addView(input) }
        AlertDialog.Builder(this)
            .setTitle(R.string.rename_title)
            .setView(box)
            .setPositiveButton(android.R.string.ok) { _, _ -> accounts.rename(a.id, input.text.toString()); Widgets.updateAll(this) }
            .setNegativeButton(android.R.string.cancel, null)
            .show()
    }

    private fun confirmRemove() {
        val a = accounts.selected
        if (a.isDefault) return
        AlertDialog.Builder(this)
            .setTitle(getString(R.string.remove_title, accounts.displayName(a)))
            .setMessage(R.string.remove_message)
            .setPositiveButton(R.string.remove_confirm) { _, _ ->
                accounts.remove(a.id)
                Widgets.updateAll(this)
                render()
            }
            .setNegativeButton(android.R.string.cancel, null)
            .show()
    }

    override fun onResume() {
        super.onResume()
        prefs.registerOnSharedPreferenceChangeListener(listener)
        render()
        val store = Store(this, accounts.selected.id)
        val snap = store.snapshot
        if (!store.busy && (snap == null || Duration.between(snap.fetchedAt, Instant.now()) > Duration.ofMinutes(1)))
            RefreshWorker.runNow(this)
    }

    override fun onPause() {
        prefs.unregisterOnSharedPreferenceChangeListener(listener)
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
