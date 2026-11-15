package dev.teo.claudequota

import android.content.Context
import android.webkit.WebView
import androidx.webkit.ProfileStore
import androidx.webkit.WebViewCompat
import androidx.webkit.WebViewFeature
import org.json.JSONArray
import org.json.JSONObject
import java.util.Locale

/**
 * A claude.ai account. "default" is the original account: it uses the default WebView profile and the
 * original preference keys. Other accounts get their own WebView profile ("acc-<id>") and suffixed keys.
 * [name] is empty until the user renames the account or the first successful read provides the org name.
 */
data class Account(val id: String, val name: String = "") {
    val isDefault get() = id == AccountModel.DEFAULT
    val profile get() = if (isDefault) null else "acc-$id"
}

/** Pure account logic (unit-tested). */
object AccountModel {
    const val DEFAULT = "default"

    /** Preference key of [base] for an account: unchanged for the default account. */
    fun key(base: String, id: String) = if (id == DEFAULT) base else "$base@$id"

    fun displayName(a: Account, index: Int, locale: Locale = Locale.getDefault()) =
        a.name.ifBlank { if (locale.isFrench()) "Compte ${index + 1}" else "Account ${index + 1}" }

    /** Short name for the widget header. */
    fun shortName(name: String, max: Int = 14) = if (name.length <= max) name else name.take(max - 1).trimEnd() + "…"

    fun nextId(accounts: List<Account>) =
        ((accounts.mapNotNull { it.id.toIntOrNull() }.maxOrNull() ?: 1) + 1).toString()

    /** After a successful read: an unnamed account takes the organization name. */
    fun withOrgName(accounts: List<Account>, id: String, org: String?) =
        if (org.isNullOrBlank()) accounts
        else accounts.map { if (it.id == id && it.name.isBlank()) it.copy(name = org) else it }

    fun toJson(accounts: List<Account>): String =
        JSONArray(accounts.map { JSONObject().put("id", it.id).put("name", it.name) }).toString()

    fun fromJson(json: String?): List<Account> {
        val list = runCatching {
            val a = JSONArray(json ?: "[]")
            (0 until a.length()).map { val o = a.getJSONObject(it); Account(o.getString("id"), o.optString("name", "")) }
        }.getOrDefault(emptyList())
        // The default account always exists, first.
        return listOf(list.firstOrNull { it.isDefault } ?: Account(DEFAULT)) + list.filter { !it.isDefault }
    }
}

/** Account list and selection, stored next to the quota values. */
class Accounts(ctx: Context) {
    private val prefs = ctx.applicationContext.getSharedPreferences(Store.NAME, Context.MODE_PRIVATE)
    private val locale = ctx.locale()

    var list: List<Account>
        get() = AccountModel.fromJson(prefs.getString(K_LIST, null))
        set(v) = prefs.edit().putString(K_LIST, AccountModel.toJson(v)).apply()

    /** Account shown in the app and in the widget. */
    var selected: Account
        get() = prefs.getString(K_SELECTED, null)?.let { id -> list.firstOrNull { it.id == id } } ?: list.first()
        set(v) = prefs.edit().putString(K_SELECTED, v.id).apply()

    fun get(id: String?) = list.firstOrNull { it.id == id } ?: list.first()

    fun displayName(a: Account) = AccountModel.displayName(a, list.indexOfFirst { it.id == a.id }.coerceAtLeast(0), locale)

    fun add(): Account {
        val a = Account(AccountModel.nextId(list))
        list = list + a
        return a
    }

    fun rename(id: String, name: String) {
        list = list.map { if (it.id == id) it.copy(name = name.trim()) else it }
    }

    fun nameFromOrg(id: String, org: String?) {
        val updated = AccountModel.withOrgName(list, id, org)
        if (updated != list) list = updated
    }

    /** Removes an added account, its stored values and its WebView profile (cookies included). */
    fun remove(id: String) {
        val a = list.firstOrNull { it.id == id } ?: return
        if (a.isDefault) return
        list = list.filter { it.id != id }
        if (prefs.getString(K_SELECTED, null) == id) prefs.edit().remove(K_SELECTED).apply()
        Store(prefsContext, id).clear()
        a.profile?.let { p -> if (multiProfile) runCatching { ProfileStore.getInstance().deleteProfile(p) } }
    }

    private val prefsContext = ctx.applicationContext

    companion object {
        private const val K_LIST = "accounts"
        private const val K_SELECTED = "selected_account"

        /** Separate WebView profiles need a recent Android System WebView. */
        val multiProfile get() = WebViewFeature.isFeatureSupported(WebViewFeature.MULTI_PROFILE)

        /** Must be called before the WebView loads anything. */
        fun attach(web: WebView, a: Account) {
            val p = a.profile ?: return
            if (!multiProfile) return
            ProfileStore.getInstance().getOrCreateProfile(p)
            WebViewCompat.setProfile(web, p)
        }

        /** Cookie manager of the account's profile (the session itself is never read by the app). */
        fun cookies(a: Account): android.webkit.CookieManager {
            val p = a.profile
            return if (p == null || !multiProfile) android.webkit.CookieManager.getInstance()
            else ProfileStore.getInstance().getOrCreateProfile(p).cookieManager
        }
    }
}
