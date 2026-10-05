package com.tessera.rider

import com.intellij.DynamicBundle
import org.jetbrains.annotations.PropertyKey

private const val BUNDLE = "messages.TesseraBundle"

/** The plugin's text in the IDE's language (messages/TesseraBundle*.properties). */
internal object TesseraBundle : DynamicBundle(BUNDLE) {
    fun message(@PropertyKey(resourceBundle = BUNDLE) key: String, vararg params: Any): String = getMessage(key, *params)
}
