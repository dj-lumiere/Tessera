using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Lapila;

/// The build daemon: a `lapila daemon` process that builds a manifest's program for `lapila build` and `lapila buildandrun`
/// when the manifest says `[target] use-daemon = true`. It keeps what a build reads again unchanged (the standard
/// library's declarations, the target's CPU and data layout) between builds, so a build after an edit parses only the
/// program's own files. It builds one request at a time, and stops after half an hour without one.
///
/// A client reaches it through a Unix socket in the temporary folder whose name comes from the builder binary and the
/// `LAPILA_*` environment, so a daemon only ever serves the builder and the settings it was started with. A build
/// that finds no daemon starts one and builds on its own, and the next build goes through the daemon. The daemon
/// builds and the client runs the program, so the program has the client's console.
internal static class BuildDaemon
{
    /// Bumped when the request or the reply changes.
    private const int ProtocolVersion = 1;

    private static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(30);

    /// What the daemon gives back: the build's exit status, what it printed, and the executable when it built one.
    public sealed record Reply(int Code, string Stdout, string Stderr, string? Exe);

    /// The daemon's socket: one per builder binary and `LAPILA_*` environment.
    private static string SocketPath()
    {
        var sb = new StringBuilder();
        foreach (var path in new[] { Environment.ProcessPath, typeof(BuildDaemon).Assembly.Location })
            if (path is { Length: > 0 } && File.Exists(path))
            {
                var info = new FileInfo(path);
                sb.Append(path).Append(' ').Append(info.Length).Append(' ').Append(info.LastWriteTimeUtc.Ticks).Append('\n');
            }
        foreach (var (name, value) in Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                     .Select(e => ((string)e.Key, (string?)e.Value))
                     .Where(e => e.Item1.StartsWith("LAPILA_", StringComparison.Ordinal)).OrderBy(e => e.Item1, StringComparer.Ordinal))
            sb.Append(name).Append('=').Append(value).Append('\n');
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16].ToLowerInvariant();
        return Path.Combine(Path.GetTempPath(), $"lapila-daemon-{hash}.sock");
    }

    // ── The client ──────────────────────────────────────────────────────────

    /// Builds through the daemon, or gives null when none is running (after starting one for the next build) or it
    /// couldn't finish the request: the caller builds on its own then.
    public static Reply? Build(List<string> files)
    {
        string socket = SocketPath();
        try
        {
            using var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            s.Connect(new UnixDomainSocketEndPoint(socket));
            using var stream = new NetworkStream(s);
            using var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            w.Write(ProtocolVersion);
            w.Write(Directory.GetCurrentDirectory());
            w.Write(files.Count);
            foreach (var f in files) w.Write(Path.GetFullPath(f));
            w.Flush();
            int code = r.ReadInt32();
            if (code < 0) return null;
            string stdout = r.ReadString(), stderr = r.ReadString();
            string? exe = r.ReadBoolean() ? r.ReadString() : null;
            return new Reply(code, stdout, stderr, exe);
        }
        catch (SocketException)
        {
            Start();
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// Starts a daemon in its own session, so it outlives the build and a Ctrl+C in the terminal doesn't reach it.
    private static void Start()
    {
        string? exe = Environment.ProcessPath;
        if (exe is null) return;
        var args = new List<string>();
        // Run as a managed assembly (dotnet lapila.dll), the daemon is that assembly again.
        if (Path.GetFileNameWithoutExtension(exe) == "dotnet") args.Add(typeof(BuildDaemon).Assembly.Location);
        args.Add("daemon");
        string file = exe;
        if (!OperatingSystem.IsWindows() && File.Exists("/usr/bin/setsid"))
        {
            args.Insert(0, exe);
            file = "/usr/bin/setsid";
        }
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetTempPath(),
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            // The daemon writes nothing to them: closing them here leaves the build's own console free.
            p?.StandardInput.Close();
            p?.StandardOutput.Close();
            p?.StandardError.Close();
        }
        catch (System.ComponentModel.Win32Exception) { }
    }

    // ── The daemon ──────────────────────────────────────────────────────────

    /// `lapila daemon`: serves builds until it has been idle for IdleLimit. Exits at once when another daemon has the
    /// socket.
    public static int Serve()
    {
        string socket = SocketPath();
        if (File.Exists(socket))
        {
            try
            {
                using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                probe.Connect(new UnixDomainSocketEndPoint(socket));
                return 0;
            }
            catch (SocketException)
            {
                File.Delete(socket);
            }
        }
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(socket));
        }
        catch (SocketException)
        {
            return 0;
        }
        listener.Listen(8);
        try
        {
            InstanceSummaries.Enabled = true;
            try { Cli.ParseStandardLibrary(); }
            catch (Exception e) when (e is CompileError or Cli.ToolError or IOException) { }
            while (true)
            {
                var accepting = listener.AcceptAsync();
                if (!accepting.Wait(IdleLimit)) return 0;
                using var client = accepting.Result;
                Answer(client);
                GC.Collect();
            }
        }
        finally
        {
            try { File.Delete(socket); } catch (IOException) { }
        }
    }

    /// Builds one request and writes the reply. A request it can't read, or a fault of the daemon itself, gets the
    /// reply -1, and the client builds on its own.
    private static void Answer(Socket client)
    {
        using var stream = new NetworkStream(client);
        using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        using var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        try
        {
            if (r.ReadInt32() != ProtocolVersion)
            {
                w.Write(-1);
                return;
            }
            string cwd = r.ReadString();
            var files = new List<string>();
            for (int i = r.ReadInt32(); i > 0; i--) files.Add(r.ReadString());

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var (outer, outerError) = (Console.Out, Console.Error);
            int code = 0;
            string? exe = null;
            try
            {
                Directory.SetCurrentDirectory(cwd);
                Console.SetOut(stdout);
                Console.SetError(stderr);
                try { exe = Cli.BuildForDaemon(files); }
                catch (CompileError e)
                {
                    Console.Error.WriteLine(e.Message);
                    code = 1;
                }
                catch (Cli.ToolError e)
                {
                    Console.Error.WriteLine(e.Report);
                    code = 1;
                }
                catch (ManifestError e)
                {
                    Console.Error.WriteLine(e.Report);
                    code = 1;
                }
            }
            catch (Exception)
            {
                code = -1;
            }
            finally
            {
                Console.SetOut(outer);
                Console.SetError(outerError);
                Directory.SetCurrentDirectory(Path.GetTempPath());
            }
            w.Write(code);
            if (code < 0) return;
            w.Write(stdout.ToString());
            w.Write(stderr.ToString());
            w.Write(exe is not null);
            if (exe is not null) w.Write(exe);
            w.Flush();
        }
        catch (IOException) { }
    }
}
