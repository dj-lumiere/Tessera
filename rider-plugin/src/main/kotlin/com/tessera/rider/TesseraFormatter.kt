package com.tessera.rider

import com.intellij.execution.ExecutionException
import com.intellij.execution.configurations.GeneralCommandLine
import com.intellij.execution.configurations.PathEnvironmentVariableUtil
import com.intellij.execution.process.CapturingProcessHandler
import com.intellij.formatting.service.AsyncDocumentFormattingService
import com.intellij.formatting.service.AsyncFormattingRequest
import com.intellij.formatting.service.FormattingService
import com.intellij.openapi.project.Project
import com.intellij.psi.PsiFile
import java.nio.charset.StandardCharsets
import java.nio.file.Files
import java.nio.file.Path

/**
 * Reformat Code for `.tess` files: the editor's text goes through the Tessera builder's formatter (`tessera fmt -`,
 * standard input to standard output), so an unsaved buffer formats as it stands.
 */
class TesseraFormatter : AsyncDocumentFormattingService() {
    override fun getFeatures(): Set<FormattingService.Feature> = emptySet()

    override fun canFormat(file: PsiFile): Boolean = file.virtualFile?.isTessera() == true

    override fun getName(): String = "Tessera"

    override fun getNotificationGroupId(): String = "Tessera"

    override fun createFormattingTask(request: AsyncFormattingRequest): FormattingTask? {
        val command = try {
            builderCommand(project = request.context.project)
        } catch (e: ExecutionException) {
            request.onError("Tessera", e.message ?: "The Tessera builder was not found.")
            return null
        }

        val handler = CapturingProcessHandler(command.withParameters("fmt", "-").withCharset(StandardCharsets.UTF_8))
        return object : FormattingTask {
            override fun run() {
                handler.processInput.use { it.write(request.documentText.toByteArray(StandardCharsets.UTF_8)) }
                val output = handler.runProcess(TIMEOUT_MILLIS)
                when {
                    output.isTimeout -> request.onError("Tessera", "The Tessera formatter did not finish in time.")
                    output.exitCode != 0 -> request.onError("Tessera", output.stderr.ifBlank { "tessera fmt failed." })
                    else -> request.onTextReady(output.stdout)
                }
            }

            override fun cancel(): Boolean {
                handler.destroyProcess()
                return true
            }

            override fun isRunUnderProgress(): Boolean = true
        }
    }

    private companion object {
        const val TIMEOUT_MILLIS = 10_000
    }
}

/**
 * The Tessera builder to run: the one set in Settings | Languages & Frameworks | Tessera, else the workspace dev build
 * (`<project>/Tessera/bin/Debug/net10.0/tessera.dll` in the LumiFoundry workspace, `<project>/bin/Debug/net10.0/tessera.dll`
 * for the Tessera project on its own), else `tessera` on the PATH. A `.dll` runs under `dotnet`.
 */
internal fun builderCommand(project: Project): GeneralCommandLine {
    val configured = TesseraSettings.getInstance().builderPath
    val builder: Path = when {
        configured.isNotEmpty() -> Path.of(configured).also {
            if (!Files.isRegularFile(it)) {
                throw ExecutionException("The Tessera builder set in Settings | Languages & Frameworks | Tessera doesn't exist: $it")
            }
        }

        else -> project.basePath?.let { Path.of(it) }
            ?.let { base ->
                listOf(
                    base.resolve("Tessera/bin/Debug/net10.0/tessera.dll"),
                    base.resolve("bin/Debug/net10.0/tessera.dll"),
                ).firstOrNull { Files.isRegularFile(it) }
            }
            ?: PathEnvironmentVariableUtil.findExecutableInWindowsPath("tessera")?.let { Path.of(it) }
                ?.takeIf { Files.isRegularFile(it) }
            ?: throw ExecutionException(
                "The Tessera builder wasn't found. Build Tessera, or set it in Settings | Languages & Frameworks | Tessera."
            )
    }

    return if (builder.fileName.toString().endsWith(".dll", ignoreCase = true)) {
        GeneralCommandLine("dotnet", builder.toString())
    } else {
        GeneralCommandLine(builder.toString())
    }
}
