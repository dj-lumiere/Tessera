package com.lapila.rider

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

/** Which Lapila builder runs the language server. Empty means the workspace dev build (see `locateServer`). */
@Service(Service.Level.APP)
@State(name = "LapilaSettings", storages = [Storage("lapila.xml")])
internal class LapilaSettings : SimplePersistentStateComponent<LapilaSettings.Options>(Options()) {
    class Options : BaseState() {
        var builderPath by string()
    }

    var builderPath: String
        get() = state.builderPath.orEmpty()
        set(value) {
            state.builderPath = value.trim().ifEmpty { null }
        }

    companion object {
        fun getInstance(): LapilaSettings = service()
    }
}

/** Settings | Languages & Frameworks | Lapila. */
internal class LapilaConfigurable : BoundConfigurable("Lapila") {
    private val settings = LapilaSettings.getInstance()

    override fun createPanel(): DialogPanel = panel {
        row(LapilaBundle.message("settings.label")) {
            textFieldWithBrowseButton(FileChooserDescriptorFactory.singleFile().withTitle(LapilaBundle.message("settings.title")))
                .bindText(settings::builderPath)
                .align(AlignX.FILL)
                .comment(LapilaBundle.message("settings.comment"))
        }
    }

    override fun apply() {
        super.apply()
        ProjectManager.getInstance().openProjects.forEach(LapilaLanguageServer::restart)
    }
}
