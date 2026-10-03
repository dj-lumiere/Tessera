package com.tessera.rider

import com.intellij.execution.ExecutionException
import com.intellij.execution.configurations.GeneralCommandLine
import com.intellij.execution.configurations.PathEnvironmentVariableUtil
import com.intellij.openapi.Disposable
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.application.PathManager
import com.intellij.openapi.components.Service
import com.intellij.openapi.components.service
import com.intellij.openapi.diagnostic.logger
import com.intellij.openapi.fileEditor.FileEditorManager
import com.intellij.openapi.fileEditor.FileEditorManagerListener
import com.intellij.openapi.project.Project
import com.intellij.openapi.project.guessProjectDir
import com.intellij.openapi.util.io.FileUtil
import com.intellij.openapi.util.io.NioFiles
import com.intellij.openapi.util.text.StringUtil
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.platform.lsp.api.LspClientDescriptor
import com.intellij.platform.lsp.api.LspClientManager
import com.intellij.platform.lsp.api.LspIntegrationProvider
import com.intellij.platform.lsp.api.customization.LspCustomization
import com.intellij.platform.lsp.api.customization.LspDiagnosticsCustomizer
import com.intellij.platform.lsp.api.customization.LspDiagnosticsSupport
import com.intellij.platform.lsp.api.customization.LspSemanticTokensCustomizer
import com.intellij.util.concurrency.AppExecutorUtil
import org.eclipse.lsp4j.Diagnostic
import java.io.IOException
import java.io.UncheckedIOException
import java.nio.charset.StandardCharsets
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.StandardCopyOption
import java.util.concurrent.ScheduledFuture
import java.util.concurrent.TimeUnit

private val LOG = logger<TesseraLanguageServer>()

/**
 * Starts the Tessera language server (`tessera lsp`) when a `.tess` file opens. Rider asks this provider only about files in
 * project content, which in Rider means files a .csproj includes, so TesseraFileOpenListener starts the server for the rest.
 */
class TesseraLanguageServer : LspIntegrationProvider {
    override fun fileOpened(project: Project, file: VirtualFile, clientStarter: LspIntegrationProvider.LspClientStarter) {
        TesseraLanguage.ensureRegistered()
        if (file.isTessera()) {
            clientStarter.ensureClientStarted(TesseraClientDescriptor(project))
        }
    }

    internal companion object {
        fun restart(project: Project) {
            LspClientManager.getInstance(project).stopAndRestartClientsIfNeeded(TesseraLanguageServer::class.java)
        }
    }
}

/**
 * Starts the server for every `.tess` file that opens, including the ones outside project content (the stdlib,
 * playgrounds and tests, which no .csproj includes). Rider keeps one client per provider and descriptor, so
 * starting it again for each file is free.
 */
internal class TesseraFileOpenListener(private val project: Project) : FileEditorManagerListener {
    override fun fileOpened(source: FileEditorManager, file: VirtualFile) {
        TesseraLanguage.ensureRegistered()
        if (file.isTessera()) {
            LspClientManager.getInstance(project)
                .ensureClientStarted(TesseraLanguageServer::class.java, TesseraClientDescriptor(project))
        }
    }
}

/**
 * The server's diagnostics are plain text, and the editor shows a tooltip as HTML: a message naming a type with angle
 * brackets (`to<@Byte>`, `<error>`) would lose them as tags. The tooltip shows the message as written.
 */
private object PlainTextDiagnostics : LspDiagnosticsSupport() {
    override fun getTooltip(diagnostic: Diagnostic): String =
        StringUtil.escapeXmlEntities(diagnostic.message).replace("\n", "<br>")
}

/**
 * One server per project. Its root is the project (solution) folder rather than Rider's content roots, so files that no
 * .csproj includes still reach the server.
 */
private class TesseraClientDescriptor(project: Project) :
    LspClientDescriptor(project, "Tessera", *listOfNotNull(project.guessProjectDir()).toTypedArray()) {
    override fun isSupportedFile(file: VirtualFile): Boolean = file.isTessera()

    override fun getLanguageId(file: VirtualFile): String = "tessera"

    override val lspCustomization: LspCustomization = object : LspCustomization() {
        override val semanticTokensCustomizer: LspSemanticTokensCustomizer = TesseraSemanticTokens
        override val diagnosticsCustomizer: LspDiagnosticsCustomizer = PlainTextDiagnostics
    }

    override fun createCommandLine(): GeneralCommandLine {
        val server = locateServer(project)
        val stamp = buildStamp(server.parent)
        val staged = stageServer(server, stamp)
        project.service<ServerBuildWatcher>().watch(server.parent, stamp)
        LOG.info("Starting the Tessera language server from $staged (a copy of $server)")

        val command = if (staged.fileName.toString().endsWith(".dll", ignoreCase = true)) {
            GeneralCommandLine("dotnet", staged.toString(), "lsp")
        } else {
            GeneralCommandLine(staged.toString(), "lsp")
        }
        // The builder looks for its standard library above itself, which the copy no longer is: name the one the
        // builder it was copied from would find.
        stdlibAbove(server.parent)?.let { command.withEnvironment("TESSERA_STDLIB", it.toString()) }
        return command.withWorkDirectory(staged.parent.toFile()).withCharset(StandardCharsets.UTF_8)
    }
}

/**
 * The builder set in Settings | Languages & Frameworks | Tessera, else the workspace dev build
 * (`<project>/Tessera/bin/Debug/net10.0/tessera.dll` in the LumiFoundry workspace, `<project>/bin/Debug/net10.0/tessera.dll`
 * for the Tessera project on its own), else `tessera` on the PATH.
 */
private fun locateServer(project: Project): Path {
    val configured = TesseraSettings.getInstance().builderPath
    if (configured.isNotEmpty()) {
        val path = Path.of(configured)
        if (!Files.isRegularFile(path)) {
            throw ExecutionException("The Tessera builder set in Settings | Languages & Frameworks | Tessera doesn't exist: $path")
        }
        return path
    }

    val base = project.basePath?.let { Path.of(it) }
    val candidates = listOfNotNull(
        base?.resolve("Tessera/bin/Debug/net10.0/tessera.dll"),
        base?.resolve("bin/Debug/net10.0/tessera.dll"),
    )
    return candidates.firstOrNull { Files.isRegularFile(it) }
        ?: PathEnvironmentVariableUtil.findExecutableInWindowsPath("tessera").let { Path.of(it) }.takeIf { Files.isRegularFile(it) }
        ?: throw ExecutionException(
            "The Tessera builder isn't at ${candidates.joinToString(" or ")} or on the PATH. Build Tessera, or set it in " +
                "Settings | Languages & Frameworks | Tessera."
        )
}

/** The nearest `Standard/` (one holding Prelude.tess) above a folder: where the builder there finds its library. */
private fun stdlibAbove(folder: Path): Path? =
    generateSequence(folder) { it.parent }
        .map { it.resolve("Standard") }
        .firstOrNull { Files.isRegularFile(it.resolve("Prelude.tess")) }

/** The newest file time in a server's folder: changes whenever a rebuild writes into it. */
private fun buildStamp(folder: Path): Long =
    Files.walk(folder).use { files ->
        files.filter { Files.isRegularFile(it) }.mapToLong { Files.getLastModifiedTime(it).toMillis() }.max().orElse(0)
    }

private val stagingRoot: Path get() = Path.of(PathManager.getSystemPath(), "tessera-lsp")

/**
 * Copies the server's folder to Rider's system directory and returns the server inside the copy. A server run straight
 * from bin/ holds its dlls open, and the next `dotnet build` then fails to overwrite them (MSB3021). There is one copy per
 * build, named by its stamp, and copies of earlier builds are deleted once no running server holds them.
 */
private fun stageServer(server: Path, stamp: Long): Path {
    val root = stagingRoot
    val target = root.resolve(stamp.toString())
    if (!Files.isDirectory(target)) {
        Files.createDirectories(root)
        val partial = root.resolve("partial-${ProcessHandle.current().pid()}-${System.nanoTime()}")
        FileUtil.copyDir(server.parent.toFile(), partial.toFile())
        try {
            Files.move(partial, target, StandardCopyOption.ATOMIC_MOVE)
        } catch (_: IOException) {
            // Another project staged the same build first.
            deleteQuietly(partial)
        }
    }
    deleteEarlierCopies(root, keep = target)
    return target.resolve(server.fileName)
}

/**
 * A copy still held by a running server can't be renamed on Windows, so a copy is deleted only after renaming it out of
 * the way succeeds. An unfinished copy (`partial-`) is left alone for a day, since another project may still be writing it.
 */
private fun deleteEarlierCopies(root: Path, keep: Path) {
    val dayAgo = System.currentTimeMillis() - TimeUnit.DAYS.toMillis(1)
    val earlier = Files.list(root).use { entries -> entries.filter { it != keep }.toList() }
    for (copy in earlier) {
        val name = copy.fileName.toString()
        if (name.startsWith("deleting-")) {
            deleteQuietly(copy)
            continue
        }
        if (name.startsWith("partial-") && Files.getLastModifiedTime(copy).toMillis() > dayAgo) continue
        val doomed = root.resolve("deleting-$name")
        try {
            Files.move(copy, doomed)
        } catch (_: IOException) {
            // A running server still holds it.
            continue
        }
        deleteQuietly(doomed)
    }
}

/** A copy that fails to delete now is retried the next time a server starts, as a `deleting-` leftover. */
private fun deleteQuietly(path: Path) {
    try {
        NioFiles.deleteRecursively(path)
    } catch (_: IOException) {
    }
}

/**
 * Restarts the project's server after its build folder changes, so a rebuilt builder takes over without a manual restart.
 * It waits for the rebuild to settle: the folder changed since the server started and then stayed the same for one poll.
 */
@Service(Service.Level.PROJECT)
internal class ServerBuildWatcher(private val project: Project) : Disposable {
    private var folder: Path? = null
    private var running = 0L
    private var seen = 0L
    private var poller: ScheduledFuture<*>? = null

    @Synchronized
    fun watch(folder: Path, stamp: Long) {
        this.folder = folder
        running = stamp
        seen = stamp
        if (poller == null) {
            poller = AppExecutorUtil.getAppScheduledExecutorService()
                .scheduleWithFixedDelay(::poll, POLL_SECONDS, POLL_SECONDS, TimeUnit.SECONDS)
        }
    }

    private fun poll() {
        if (!settled()) return
        ApplicationManager.getApplication().invokeLater({ TesseraLanguageServer.restart(project) }, project.disposed)
    }

    @Synchronized
    private fun settled(): Boolean {
        val folder = folder ?: return false
        val stamp = try {
            buildStamp(folder)
        } catch (_: IOException) {
            // Mid-rebuild, a file vanished between listing and reading it.
            return false
        } catch (_: UncheckedIOException) {
            return false
        }
        val restart = stamp != running && stamp == seen
        if (restart) running = stamp
        seen = stamp
        return restart
    }

    @Synchronized
    override fun dispose() {
        poller?.cancel(false)
    }

    private companion object {
        const val POLL_SECONDS = 3L
    }
}
