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

    public Adb(string projectRoot)
    {
        Rutas.AsegurarCarpeta(Path.Combine(Rutas.Logs, "adb"));

        string exe = Rutas.NombreExe("adb");
        var candidates = new List<string>
        {
            Path.Combine(projectRoot, "tools", "android-sdk", "platform-tools", exe),
            Path.Combine(projectRoot, "tools", "platform-tools", exe),
            Path.Combine(AppContext.BaseDirectory, "platform-tools", exe),
        };
        foreach (var sdk in new[] { Environment.GetEnvironmentVariable("ANDROID_HOME"),
                                    Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT") })
            if (!string.IsNullOrWhiteSpace(sdk))
                candidates.Add(Path.Combine(sdk, "platform-tools", exe));

        string? found = null;
        foreach (var c in candidates)
            if (File.Exists(c)) { found = c; break; }

        found ??= Rutas.BuscarEnPath(exe);

        AdbPath = found ?? Path.Combine(projectRoot, "tools", "android-sdk", "platform-tools", exe);
    }

    public bool Available => File.Exists(AdbPath);

    public sealed record Result(int ExitCode, string StdOut, string StdErr)
    {
        public bool Ok => ExitCode == 0;
        public string All => (StdOut + "\n" + StdErr).Trim();
    }

    public async Task<Result> RunAsync(IEnumerable<string> args, int timeoutMs = 20000,
                                       string? stdin = null, Action<string>? onLine = null,
                                       CancellationToken ct = default)
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
            if (!proc.Start()) return new Result(-1, "", Loc.T("core.adb.no_se_pudo_iniciar"));
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

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try { await proc.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { }
            string motivo = ct.IsCancellationRequested ? Loc.T("core.adb.cancelado") : Loc.T("core.adb.tiempo_agotado", timeoutMs);
            return new Result(-2, sbOut.ToString(), sbErr + "\n[" + motivo + "]");
        }

        try { await Task.WhenAll(outTask, errTask).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (TimeoutException) { }
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

    public Process? StartShell(IEnumerable<string> args, Action<string>? onLine = null, string? stdinLinea = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = AdbPath,
            RedirectStandardInput = stdinLinea != null,
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

        if (stdinLinea != null)
        {
            try
            {
                proc.StandardInput.WriteLine(stdinLinea);
                proc.StandardInput.Flush();
            }
            catch { }
        }

        if (onLine != null)
        {
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
        }
        return proc;
    }

    // ------------------------------------------------------------- dispositivos
    public sealed record Device(string Serial, string State, string Model, bool Usb);

    public async Task<List<Device>> DevicesAsync()
    {
        var r = await RunAsync(new[] { "devices", "-l" }, 15000);
        return ParsearDispositivos(r.StdOut);
    }

    public static List<Device> ParsearDispositivos(string salida)
    {
        var list = new List<Device>();
        foreach (var raw in salida.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("*", StringComparison.Ordinal)) continue;

            var parts = Regex.Split(line, @"\s+");
            if (parts.Length < 2) continue;

            var serial = parts[0];
            var state = parts[1];
            string model = "";
            foreach (var p in parts.Skip(2))
            {
                if (p.StartsWith("model:", StringComparison.OrdinalIgnoreCase))
                    model = p[6..].Replace('_', ' ').Trim();
            }
            var usb = !EsWifi(serial) && state is not ("offline" or "connecting");
            list.Add(new Device(serial, state, model, usb));
        }
        return list;
    }

    public static bool EsWifi(string serial) =>
        serial.Contains(':') || serial.Contains("._adb-tls-connect.", StringComparison.OrdinalIgnoreCase);

    public async Task<Adb.Result> ForwardAsync(string serial, string local, string remote)
        => await RunAsync(new[] { "-s", serial, "forward", local, remote }, 15000);

    public async Task<int?> ForwardDinamicoAsync(string serial, int remoto)
    {
        var r = await RunAsync(new[] { "-s", serial, "forward", "tcp:0", $"tcp:{remoto}" }, 15000);
        return r.Ok && int.TryParse(r.StdOut.Trim(), out int puerto) && puerto > 0 ? puerto : null;
    }

    public async Task<Adb.Result> ForwardRemoveAsync(string serial, int puertoLocal)
        => await RunAsync(new[] { "-s", serial, "forward", "--remove", $"tcp:{puertoLocal}" }, 10000);

    public async Task ForwardRemoveRemotosAsync(string serial, params int[] remotos)
    {
        var lista = await RunAsync(new[] { "-s", serial, "forward", "--list" }, 10000);
        if (!lista.Ok) return;
        foreach (var linea in lista.StdOut.Split('\n'))
        {
            var partes = Regex.Split(linea.Trim(), @"\s+");
            if (partes.Length < 3 || partes[0] != serial) continue;
            if (remotos.Any(p => partes[2] == $"tcp:{p}"))
                await RunAsync(new[] { "-s", serial, "forward", "--remove", partes[1] }, 10000);
        }
    }

    public async Task<Adb.Result> PushAsync(string serial, string localPath, string remotePath,
                                            CancellationToken ct = default)
        => await RunAsync(new[] { "-s", serial, "push", localPath, remotePath }, 60000, ct: ct);

    public async Task<Adb.Result> ShellAsync(string serial, string command, int timeoutMs = 20000,
                                             CancellationToken ct = default)
        => await RunAsync(new[] { "-s", serial, "shell", command }, timeoutMs, ct: ct);

    public sealed record MdnsService(string Name, string Type, string Endpoint);

    public async Task<List<MdnsService>> MdnsServicesAsync()
    {
        var r = await RunAsync(new[] { "mdns", "services" }, 10000);
        return ParsearMdns(r.StdOut);
    }

    public static List<MdnsService> ParsearMdns(string salida)
    {
        var list = new List<MdnsService>();
        foreach (var raw in salida.Split('\n'))
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
