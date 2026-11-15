package dev.teo.claudequota

import android.content.Context
import android.os.Handler
import android.os.Looper
import android.webkit.CookieManager
import android.webkit.JavascriptInterface
import android.webkit.WebResourceError
import android.webkit.WebResourceRequest
import android.webkit.WebView
import android.webkit.WebViewClient
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import org.json.JSONObject
import kotlin.coroutines.resume

/**
 * One quota read (~1.5 s): an off-screen WebView loads a lightweight claude.ai page, then runs the shared script.
 * The session lives in the WebView's storage: this code never reads a cookie.
 */
object QuotaReader {
    private const val HOST_URL = "https://claude.ai/api/organizations"
    private const val TIMEOUT_MS = 30_000L

    fun script(ctx: Context): String = ctx.assets.open("usage-script.js").bufferedReader().use { it.readText() }

    suspend fun read(ctx: Context): String = withContext(Dispatchers.Main) {
        val script = script(ctx)
        val main = Handler(Looper.getMainLooper())
        var web: WebView? = null
        try {
            withTimeoutOrNull(TIMEOUT_MS) {
                suspendCancellableCoroutine { cont ->
                    var done = false
                    var injected = false
                    fun finish(msg: String) {
                        if (done) return
                        done = true
                        if (cont.isActive) cont.resume(msg)
                    }
                    val w = WebView(ctx.applicationContext)
                    web = w
                    w.settings.javaScriptEnabled = true
                    w.settings.domStorageEnabled = true
                    CookieManager.getInstance().setAcceptThirdPartyCookies(w, true)
                    w.addJavascriptInterface(Bridge { msg -> main.post { finish(msg) } }, "QuotaBridge")
                    w.webViewClient = object : WebViewClient() {
                        override fun onPageFinished(view: WebView, url: String) {
                            if (done || injected) return
                            injected = true
                            view.evaluateJavascript(script, null)
                        }

                        override fun onReceivedError(view: WebView, req: WebResourceRequest, err: WebResourceError) {
                            if (req.isForMainFrame) finish(
                                JSONObject().put("ok", false).put("reason", "offline")
                                    .put("message", err.description.toString()).toString()
                            )
                        }
                    }
                    w.loadUrl(HOST_URL)
                }
            } ?: """{"ok":false,"reason":"timeout"}"""
        } finally {
            web?.let { w -> main.post { w.stopLoading(); w.destroy() } }
        }
    }

    /** Called by the script (window.QuotaBridge.post), off the main thread. */
    class Bridge(private val onMessage: (String) -> Unit) {
        @JavascriptInterface
        fun post(msg: String) = onMessage(msg)
    }
}
