package dev.teo.claudequota

import android.view.View
import android.view.WindowInsets

/** Android 15+ (targetSdk 35+) enforces edge-to-edge: pad the content clear of the system bars. */
object Insets {
    fun pad(v: View) {
        val l = v.paddingLeft
        val t = v.paddingTop
        val r = v.paddingRight
        val b = v.paddingBottom
        v.setOnApplyWindowInsetsListener { view, insets ->
            val i = insets.getInsets(WindowInsets.Type.systemBars() or WindowInsets.Type.displayCutout() or WindowInsets.Type.ime())
            view.setPadding(l + i.left, t + i.top, r + i.right, b + i.bottom)
            insets
        }
        v.requestApplyInsets()
    }
}
