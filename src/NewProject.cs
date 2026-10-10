namespace Tessera;

/// `tessera new [folder]`: starts a solution in the folder (the current one when none is given, made when missing).
/// Writes config.toml (the package named after the folder), a main.tess that prints a greeting, a .gitignore for build
/// output, and the VS Code files that recommend the Tessera extension and bind run and build. A file that is already
/// there is kept as it is.
static class NewProject
{
    public static int Run(string[] args, string version)
    {
        string folder = Path.GetFullPath(args.Length > 0 ? args[0] : Directory.GetCurrentDirectory());
        Directory.CreateDirectory(folder);
        string name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        (string Path, string Text)[] files =
        [
            ("config.toml", $"""
                [package]
                name = "{name}"
                version = "0.1.0"
                tessera-version = "{version}"

                [target]
                executable = "main.tess"
                mode = "debug"

                """),
            ("main.tess", """
                import Standard/Format
                import Standard/Os

                /// Writes a greeting.
                routine start() -> Void
                    block entry()
                        out : @Out = Out.writer()
                        out.write("Hello, world!\n")
                        return()

                """),
            (".gitignore", """
                build/

                """),
            (Path.Combine(".vscode", "extensions.json"), """
                {
                  "recommendations": ["lumifoundry.tessera"]
                }

                """),
            (Path.Combine(".vscode", "tasks.json"), """
                {
                  "version": "2.0.0",
                  "tasks": [
                    {
                      "label": "run",
                      "type": "shell",
                      "command": "tessera run",
                      "group": { "kind": "build", "isDefault": true },
                      "problemMatcher": []
                    },
                    {
                      "label": "build",
                      "type": "shell",
                      "command": "tessera build",
                      "problemMatcher": []
                    }
                  ]
                }

                """),
        ];

        foreach ((string relative, string text) in files)
        {
            string path = Path.Combine(folder, relative);
            if (File.Exists(path))
            {
                Console.WriteLine($"  kept     {relative} (already there)");
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            Console.WriteLine($"  created  {relative}");
        }
        Console.WriteLine("Run it with: tessera run");
        return 0;
    }
}
