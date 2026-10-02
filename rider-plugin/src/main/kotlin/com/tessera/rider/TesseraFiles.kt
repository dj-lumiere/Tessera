package com.tessera.rider

import com.intellij.ide.FileIconProvider
import com.intellij.ide.plugins.PluginManagerCore
import com.intellij.openapi.extensions.PluginId
import com.intellij.openapi.project.Project
import com.intellij.openapi.util.IconLoader
import com.intellij.openapi.vfs.VirtualFile
import org.jetbrains.plugins.textmate.api.TextMateBundleProvider
import javax.swing.Icon

internal const val PLUGIN_ID = "com.tessera.rider"

internal fun VirtualFile.isTessera(): Boolean = extension.equals("tess", ignoreCase = true)

/**
 * Registers the TextMate bundle shipped next to the plugin's jar: the grammar from Tessera.tmbundle plus the comment and
 * bracket rules of language-configuration.json. `.tess` files open as TextMate files, highlighted by that grammar.
 */
class TesseraBundleProvider : TextMateBundleProvider {
    override fun getBundles(): List<TextMateBundleProvider.PluginBundle> {
        val plugin = PluginManagerCore.getPlugin(PluginId.getId(PLUGIN_ID)) ?: return emptyList()
        return listOf(TextMateBundleProvider.PluginBundle("Tessera", plugin.pluginPath.resolve("bundle")))
    }
}

/** Gives `.tess` files the Tessera icon in place of the generic TextMate one. */
class TesseraIconProvider : FileIconProvider {
    override fun getIcon(file: VirtualFile, flags: Int, project: Project?): Icon? =
        if (file.isTessera()) ICON else null

    private companion object {
        val ICON: Icon = IconLoader.getIcon("/icons/tessera.svg", TesseraIconProvider::class.java)
    }
}
