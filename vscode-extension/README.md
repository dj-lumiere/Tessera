# Lapila for VS Code

Lapila (`.lpa`) in VS Code: highlighting from `../Lapila.tmbundle`, comment toggling and bracket pairing,
and the Lapila language server (`lapila lsp`): errors as you type, semantic colors, hover, completion, go to
definition, rename, find usages and formatting.

## Which server runs

The setting `lapila.builderPath` sets it: `lapila.dll` (run with `dotnet`) or a `lapila` executable. Left empty, the
extension uses the dev build in the workspace, `<workspace>/Lapila/bin/Debug/net10.0/lapila.dll`, else `lapila` on the
PATH. The server runs from a copy of its build folder in the extension's storage, so it never holds the files a rebuild
overwrites, and a rebuild restarts it. The command "Lapila: Restart the language server" restarts it by hand.

## Build

Needs Node.js. The grammar, the language configuration and the icon are copied in from `../Lapila.tmbundle` and
`../rider-plugin`, their one source.

```
npm install
npm run package          # -> dist/lapila.vsix
code --install-extension dist/lapila.vsix
```
