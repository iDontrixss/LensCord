using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Lenscord.Core;

/// <summary>
/// Envoltura de adb. No instala nada: solo usa el platform-tools portable que vive
/// dentro del proyecto (tools\android-sdk\platform-tools) y, si no existe, el del PATH.
/// </summary>
public sealed class Adb : IDisposable
{
    public string AdbPath { get; }
    private readonly string _logDir;

    public Adb(string projectRoot)
    {
        _logDir = Path.Combine(projectRoot, "evidencia", "adb");
        Directory.CreateDirectory(_logDir);

        string exe = OperatingSystem.IsWindows() ? "adb.exe" : "adb";
        var candidates = new List<string>
        {
            Path.Combine(projectRoot, "tools", "android-sdk", "platform-tools", exe),
            Path.Combine(projectRoot, "tools", "platform-tools", exe),
        };

        string? found = null;
        foreach (var c in candidates)
            if (File.Exists(c)) { found = c; break; }

        if (found is null)
        {
            var p = FindOnPath(exe);
            if (p is not null) found = p;
        }

        AdbPath = found ?? Path.Combine(projectRoot, "tools", "android-sdk", "platform-tools", exe);
    }

    public bool Available => File.Exists(AdbPath);

    private static string? FindOnPath(string exe)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                var full = Path.Combine(dir.Trim(), exe);
                if (File.Exists(full)) return full;
            }
            catch { }
        }
        return null;
    }

    public sealed record Result(int ExitCode, string StdOut, string StdErr)
    {
        public bool Ok => ExitCode == 0;
        public string All => (StdOut + "\n" + StdErr).Trim();
    }

    public async Task<Result> RunAsync(IEnumerable<string> args, int timeoutMs = 20000,
                                       string? stdin = null, Action<string>? onLine = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = AdbPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin != null,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var sbOut = new StringBuilder();
        var sbErr = new StringBuilder();

        using var proc = new Process { StartInfo = psi };
        try
        {
            if (!proc.Start()) return new Result(-1, "", "no se pudo iniciar adb");
        }
        catch (Exception e)
        {
            return new Result(-1, "", e.Message);
        }

        if (stdin != null)
        {
            try { await proc.StandardInput.WriteAsync(stdin); await proc.StandardInput.FlushAsync(); proc.StandardInput.Close(); }
            catch { }
        }

        var outTask = Pump(proc.StandardOutput, sbOut, onLine);
        var errTask = Pump(proc.StandardError, sbErr, null);

        using var cts = new CancellationTokenSource(timeoutMs);
        try { await proc.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { }
            return new Result(-2, sbOut.ToString(), sbErr + "\n[tiempo agotado: " + timeoutMs + " ms]");
        }

        await Task.WhenAll(outTask, errTask).ConfigureAwait(false);
        return new Result(proc.ExitCode, sbOut.ToString(), sbErr.ToString());
    }

    private static async Task Pump(StreamReader reader, StringBuilder sb, Action<string>? onLine)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                lock (sb) sb.AppendLine(line);
                onLine?.Invoke(line);
            }
        }
        catch { }
    }

    /// <summary>Lanza un proceso adb de larga duracion (shell / app_process).</summary>
    public Process? StartShell(IEnumerable<string> args, Action<string>? onLine = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = AdbPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var proc = new Process { StartInfo = psi };
        try
        {
            if (!proc.Start()) return null;
        }
        catch { return null; }

        if (onLine != null)
        {
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
        }
        return proc;
    }

    public async Task StartServerAsync() => await RunAsync(new[] { "start-server" }, 15000);

    // ------------------------------------------------------------- dispositivos
    public sealed record Device(string Serial, string State, string Model, bool Usb);

    public async Task<List<Device>> DevicesAsync()
    {
        var r = await RunAsync(new[] { "devices", "-l" }, 15000);
        var list = new List<Device>();
        foreach (var raw in r.StdOut.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("*", StringComparison.Ordinal)) continue;   // aviso de daemon

            var parts = Regex.Split(line, @"\s+");
            if (parts.Length < 2) continue;

            var serial = parts[0];
            var state = parts[1];
            string model = "";
            foreach (var p in parts.Skip(2))
            {
                if (p.StartsWith("model:", StringComparison.OrdinalIgnoreCase))
                    model = p[6..].Replace('_', ' ').Trim();
                if (p.StartsWith("usb:", StringComparison.OrdinalIgnoreCase)) { }
            }
            // transport_id no indica modo; un serie con ':' es adb-wifi (ip:puerto)
            var usb = !serial.Contains(':') && state is not ("offline" or "connecting");
            list.Add(new Device(serial, state, model, usb));
        }
        return list;
    }

    public async Task<Adb.Result> ForwardAsync(string serial, string local, string remote)
        => await RunAsync(new[] { "-s", serial, "forward", local, remote }, 15000);

    public async Task<Adb.Result> ForwardRemoveAllAsync(string serial)
        => await RunAsync(new[] { "-s", serial, "forward", "--remove-all" }, 10000);

    public async Task<Adb.Result> ForwardRemoveAllGlobalAsync()
        => await RunAsync(new[] { "forward", "--remove-all" }, 10000);

    public async Task<Adb.Result> PushAsync(string serial, string localPath, string remotePath)
        => await RunAsync(new[] { "-s", serial, "push", localPath, remotePath }, 60000);

    public async Task<Adb.Result> ShellAsync(string serial, string command, int timeoutMs = 20000)
        => await RunAsync(new[] { "-s", serial, "shell", command }, timeoutMs);

    public sealed record MdnsService(string Name, string Type, string Endpoint);

    public async Task<List<MdnsService>> MdnsServicesAsync()
    {
        var r = await RunAsync(new[] { "mdns", "services" }, 10000);
        var list = new List<MdnsService>();
        foreach (var raw in r.StdOut.Split('\n'))
        {
            var line = raw.Trim();
            var partes = Regex.Split(line, @"\s+");
            if (partes.Length < 3) continue;
            var tipo = partes.FirstOrDefault(p => p.StartsWith("_adb-", StringComparison.OrdinalIgnoreCase));
            var endpoint = partes.LastOrDefault(p => Regex.IsMatch(p, @":\d+$"));
            if (tipo is null || endpoint is null) continue;
            list.Add(new MdnsService(partes[0], tipo, endpoint));
        }
        return list;
    }

    public async Task<Adb.Result> ConnectAsync(string hostPort, int timeoutMs = 15000)
        => await RunAsync(new[] { "connect", hostPort }, timeoutMs);

    public async Task<Adb.Result> PairAsync(string hostPort, string code, int timeoutMs = 30000)
        => await RunAsync(new[] { "pair", hostPort, code }, timeoutMs);

    public async Task<Adb.Result> DisconnectAsync(string hostPort)
        => await RunAsync(new[] { "disconnect", hostPort }, 10000);

    public void Dispose() { }
}