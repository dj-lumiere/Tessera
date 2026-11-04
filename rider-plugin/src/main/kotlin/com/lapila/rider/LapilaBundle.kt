package com.lapila.rider

import com.intellij.DynamicBundle
import org.jetbrains.annotations.PropertyKey

private const val BUNDLE = "messages.LapilaBundle"

/** The plugin's text in the IDE's language (messages/LapilaBundle*.properties). */
internal object LapilaBundle : DynamicBundle(BUNDLE) {
    fun message(@PropertyKey(resourceBundle = BUNDLE) key: String, vararg params: Any): String = getMessage(key, *params)
}
