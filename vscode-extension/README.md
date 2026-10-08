# Tessera for VS Code

Tessera (`.tess`) in VS Code: highlighting from `../Tessera.tmbundle`, comment toggling and bracket pairing,
and the Tessera language server (`tessera lsp`): errors as you type, semantic colors, hover, completion, go to
definition, rename, find usages and formatting.

## Which server runs

The setting `tessera.builderPath` sets it: `tessera.dll` (run with `dotnet`) or a `tessera` executable. Left empty, the
extension uses the dev build in the workspace, `<workspace>/Tessera/bin/Debug/net10.0/tessera.dll`, else `tessera` on the
PATH. The server runs from a copy of its build folder in the extension's storage, so it never holds the files a rebuild
overwrites, and a rebuild restarts it. The command "Tessera: Restart the language server" restarts it by hand.

## Build

Needs Node.js. The grammar, the language configuration and the icon are copied in from `../Tessera.tmbundle` and
`../rider-plugin`, their one source.

```
npm install
npm run package          # -> dist/tessera.vsix
code --install-extension dist/tessera.vsix
```
