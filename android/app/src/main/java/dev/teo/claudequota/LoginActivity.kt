package dev.teo.claudequota

import android.app.Activity
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.os.Message
import android.webkit.JavascriptInterface
import android.webkit.WebChromeClient
import android.webkit.WebView
import android.webkit.WebViewClient
import android.widget.FrameLayout
import java.time.Instant

/**
 * Sign-in to claude.ai in a visible WebView (including "Continue with Google").
 * Closes itself as soon as a quota read succeeds.
 */
class LoginActivity : Activity() {
    private lateinit var web: WebView
    private lateinit var frame: FrameLayout
    private var popup: WebView? = null
    private val main = Handler(Looper.getMainLooper())
    private var checking = false
    private lateinit var script: String
    private lateinit var account: Account

    // claude.ai is a SPA: the URL changes without a page load after sign-in, hence the periodic check.
    private val poll = object : Runnable {
        override fun run() {
            check()
            main.postDelayed(this, 3000)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        actionBar?.hide()
        script = QuotaReader.script(this)
        account = Accounts(this).get(intent.getStringExtra(EXTRA_ACCOUNT))
        frame = FrameLayout(this)
        web = WebView(this)
        Accounts.attach(web, account)
        frame.addView(web)
        setContentView(frame)
        Insets.pad(frame)
        setup(web)
        // "Continue with Google" opens a pop-up that hands the result back to claude.ai: it needs a real child window.
        web.webChromeClient = object : WebChromeClient() {
            override fun onCreateWindow(view: WebView, isDialog: Boolean, isUserGesture: Boolean, resultMsg: Message): Boolean {
                val child = WebView(this@LoginActivity)
                Accounts.attach(child, account) // the pop-up must share the account's cookies
                setup(child)
                child.webChromeClient = object : WebChromeClient() {
                    override fun onCloseWindow(w: WebView) = closePopup()
                }
                closePopup()
                popup = child
                frame.addView(child)
                (resultMsg.obj as WebView.WebViewTransport).webView = child
                resultMsg.sendToTarget()
                return true
            }

            override fun onCloseWindow(w: WebView) = closePopup()
        }
        web.addJavascriptInterface(Bridge(), "QuotaBridge")
        web.loadUrl("https://claude.ai/login")
        main.postDelayed(poll, 3000)
    }

    private fun setup(w: WebView) {
        w.settings.javaScriptEnabled = true
        w.settings.domStorageEnabled = true
        w.settings.setSupportMultipleWindows(true)
        w.settings.javaScriptCanOpenWindowsAutomatically = true
        // Cloudflare and Google use third-party frames.
        Accounts.cookies(account).setAcceptThirdPartyCookies(w, true)
        w.webViewClient = WebViewClient() // keep navigation inside the WebView
    }

    private fun closePopup() {
        popup?.let { frame.removeView(it); it.destroy() }
        popup = null
    }

    private fun check() {
        val url = web.url ?: return
        if (checking || !url.startsWith("https://claude.ai/") || url.contains("/login")) return
        checking = true
        web.evaluateJavascript(script, null)
    }

    inner class Bridge {
        @JavascriptInterface
        fun post(msg: String) {
            main.post {
                checking = false
                val r = UsageParser.parse(msg, Instant.now())
                if (r.ok) {
                    Store(this@LoginActivity, account.id).apply(r)
                    Accounts(this@LoginActivity).nameFromOrg(account.id, r.org)
                    Accounts.cookies(account).flush() // persist the session to disk for background reads
                    Widgets.updateAll(this@LoginActivity)
                    RefreshWorker.schedule(this@LoginActivity)
                    setResult(RESULT_OK)
                    finish()
                }
            }
        }
    }

    companion object {
        const val EXTRA_ACCOUNT = "account"

        fun intent(ctx: android.content.Context, accountId: String) =
            android.content.Intent(ctx, LoginActivity::class.java).putExtra(EXTRA_ACCOUNT, accountId)
    }

    @Deprecated("Plain Activity, no OnBackPressedDispatcher")
    override fun onBackPressed() {
        when {
            popup != null -> closePopup()
            web.canGoBack() -> web.goBack()
            else -> super.onBackPressed()
        }
    }

    override fun onDestroy() {
        main.removeCallbacks(poll)
        closePopup()
        web.destroy()
        super.onDestroy()
    }
}
