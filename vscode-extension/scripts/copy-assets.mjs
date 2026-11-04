// The grammar, the language configuration and the icon have one source each, next to this extension: the TextMate
// bundle and the Rider plugin's bundle. A build copies them in.
import { copyFileSync, mkdirSync } from "node:fs";

mkdirSync("syntaxes", { recursive: true });
mkdirSync("icons", { recursive: true });
copyFileSync("../Lapila.tmbundle/Syntaxes/Lapila.tmLanguage", "syntaxes/Lapila.tmLanguage");
copyFileSync("../rider-plugin/bundle/language-configuration.json", "language-configuration.json");
copyFileSync("../rider-plugin/src/main/resources/icons/lapila.svg", "icons/lapila.svg");
