# Tessera for Rider

A Rider plugin for Tessera (`.tess`): syntax highlighting from `../Tessera.tmbundle`, `//` comment toggling and
bracket pairing (`bundle/`), the Tessera file icon, and the Tessera language server (`tessera lsp`): the builder's errors
and style warnings as you type, semantic colors, hover (declaration, `///` doc, and what each type parameter stands for), go to
definition, the file structure view, and
Reformat Code.

## Colors

The server colors what the builder resolved: types by kind, routines, blocks, SSA values, parameters, fields, presets
and cases, module paths, attributes, and terminators. Each color starts as its C# counterpart's (record as struct,
concept as interface, routine as method, SSA value as local variable, terminator as control-flow keyword); a block
starts purple and an attribute yellow. Change them in Settings | Editor | Color Scheme | Tessera.

## Which builder runs

Settings | Languages & Frameworks | Tessera sets the builder: `tessera.dll` (run with `dotnet`) or a `tessera` executable.
Left empty, the plugin uses the dev build, `<project>/Tessera/bin/Debug/net10.0/tessera.dll` (the LumiFoundry
workspace) or `<project>/bin/Debug/net10.0/tessera.dll`, else `tessera` on the PATH.

The server runs from a copy of the builder's folder in Rider's system directory (`tessera-lsp/`), so it never holds the
files a rebuild overwrites, and it is told where the standard library is (`TESSERA_STDLIB`, the `Standard/` above the
builder). When the build folder changes and then stays the same for a few seconds, the plugin restarts the server.

## Build

Needs Rider 2026.2 (build 262) or later. The build compiles against an installed Rider and uses Rider's own JDK, set
once for every plugin in `%USERPROFILE%\.gradle\gradle.properties`:

```
riderLocalPath=C:/Users/<you>/AppData/Local/Programs/Rider
org.gradle.java.installations.paths=C:/Users/<you>/AppData/Local/Programs/Rider/jbr
```

Without `riderLocalPath`, the build downloads the Rider named by `platformVersion` in `gradle.properties`.

```
gradlew buildPlugin    # -> build/distributions/tessera-rider-<version>.zip
gradlew runIde         # a sandbox Rider with the plugin loaded
```

Install the zip with Settings | Plugins | ⚙ | Install Plugin from Disk.
