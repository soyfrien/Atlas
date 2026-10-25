using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Atlas.Web.Services;

public sealed record ProcResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>Runs an external tool without a shell (arguments are passed as a list, so file names can't inject commands).</summary>
public static class ProcessRunner
{
    private static readonly Dictionary<string, bool> Available = new();

    /// <summary>True when <paramref name="exe"/> can be found on PATH.</summary>
    public static bool Has(string exe)
    {
        lock (Available)
        {
            if (Available.TryGetValue(exe, out var v)) return v;
            var found = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Any(dir => { try { return File.Exists(Path.Combine(dir, exe)); } catch { return false; } });
            return Available[exe] = found;
        }
    }

    /// <summary>Runs the tool; returns null when it isn't installed. Output beyond <paramref name="maxStdout"/> chars is dropped.</summary>
    public static async Task<ProcResult?> RunAsync(string exe, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct,
        int maxStdout = 16_000_000, IDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env is not null) foreach (var (k, v) in env) psi.Environment[k] = v;

        using var p = new Process { StartInfo = psi };
        try { p.Start(); }
        catch (Win32Exception) { return null; }
        p.StandardInput.Close();

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var outTask = Task.Run(async () =>
        {
            var buf = new char[8192];
            int n;
            while ((n = await p.StandardOutput.ReadAsync(buf, 0, buf.Length)) > 0)
                if (stdout.Length < maxStdout) stdout.Append(buf, 0, Math.Min(n, maxStdout - stdout.Length));
        });
        var errTask = Task.Run(async () =>
        {
            var buf = new char[4096];
            int n;
            while ((n = await p.StandardError.ReadAsync(buf, 0, buf.Length)) > 0)
                if (stderr.Length < 20_000) stderr.Append(buf, 0, Math.Min(n, 20_000 - stderr.Length));
        });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            ct.ThrowIfCancellationRequested();
            throw new PipelineException($"{Path.GetFileName(exe)} took too long and was stopped.");
        }
        await Task.WhenAll(outTask, errTask);
        return new ProcResult(p.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
