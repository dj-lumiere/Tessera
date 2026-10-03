package com.tessera.rider

import com.intellij.openapi.components.BaseState
import com.intellij.openapi.components.Service
import com.intellij.openapi.components.SimplePersistentStateComponent
import com.intellij.openapi.components.State
import com.intellij.openapi.components.Storage
import com.intellij.openapi.components.service
import com.intellij.openapi.fileChooser.FileChooserDescriptorFactory
import com.intellij.openapi.options.BoundConfigurable
import com.intellij.openapi.project.ProjectManager
import com.intellij.openapi.ui.DialogPanel
import com.intellij.ui.dsl.builder.AlignX
import com.intellij.ui.dsl.builder.bindText
import com.intellij.ui.dsl.builder.panel

/** Which Tessera builder runs the language server. Empty means the workspace dev build (see `locateServer`). */
@Service(Service.Level.APP)
@State(name = "TesseraSettings", storages = [Storage("tessera.xml")])
internal class TesseraSettings : SimplePersistentStateComponent<TesseraSettings.Options>(Options()) {
    class Options : BaseState() {
        var builderPath by string()
    }

    var builderPath: String
        get() = state.builderPath.orEmpty()
        set(value) {
            state.builderPath = value.trim().ifEmpty { null }
        }

    companion object {
        fun getInstance(): TesseraSettings = service()
    }
}

/** Settings | Languages & Frameworks | Tessera. */
internal class TesseraConfigurable : BoundConfigurable("Tessera") {
    private val settings = TesseraSettings.getInstance()

    override fun createPanel(): DialogPanel = panel {
        row("Builder:") {
            textFieldWithBrowseButton(FileChooserDescriptorFactory.singleFile().withTitle("Tessera Builder"))
                .bindText(settings::builderPath)
                .align(AlignX.FILL)
                .comment(
                    "tessera.dll (run with dotnet) or a tessera executable. Empty uses the dev build, " +
                        "&lt;project&gt;/Tessera/bin/Debug/net10.0/tessera.dll, or else tessera on the PATH."
                )
        }
    }

    override fun apply() {
        super.apply()
        ProjectManager.getInstance().openProjects.forEach(TesseraLanguageServer::restart)
    }
}
