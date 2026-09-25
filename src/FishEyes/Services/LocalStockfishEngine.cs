using System.Diagnostics;
using System.Globalization;

namespace FishEyes;

public interface IAnalysisEngine : IDisposable
{
    string CacheIdentity { get; }
    Task<Analysis> AnalyzeAsync(string fen, int depth, CancellationToken cancellation);
}

// EngineService serializes access. One UCI process stays alive between positions.
public sealed class LocalStockfishEngine : IAnalysisEngine
{
    public const int MoveTimeMilliseconds = 500;
    public const int HashMegabytes = 128;
    private readonly object lifecycle = new();
    private Process? process;
    private bool disposed;
    private readonly Func<CancellationToken, Task<ProcessStartInfo>>? startInfoFactory;
    private readonly TimeSpan responseTimeout;
    public LocalStockfishEngine() : this(null, TimeSpan.FromSeconds(15)) { }
    internal LocalStockfishEngine(Func<CancellationToken, Task<ProcessStartInfo>>? startInfoFactory, TimeSpan responseTimeout)
    { this.startInfoFactory = startInfoFactory; this.responseTimeout = responseTimeout; }
    public int Threads { get; } = Math.Clamp(Environment.ProcessorCount - 1, 1, 2);
    public string CacheIdentity => $"sf{StockfishBundle.Version}-uci-v1-t{Threads}-h{HashMegabytes}-ms{MoveTimeMilliseconds}";
    public int? ProcessId { get { lock (lifecycle) return process is { HasExited: false } p ? p.Id : null; } }

    public async Task<Analysis> AnalyzeAsync(string fen, int depth, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(responseTimeout); // Includes extraction/startup, not just search time.
        try
        {
            var engine = await GetReadyAsync(timeout.Token).ConfigureAwait(false);
            await SendAsync(engine, "position fen " + fen, timeout.Token);
            await SendAsync(engine, $"go depth {depth} movetime {MoveTimeMilliseconds}", timeout.Token);
            int? reachedDepth = null, mate = null;
            double? evaluation = null;
            bool white = fen.Split(' ')[1] == "w";
            while (true)
            {
                string line = await ReadAsync(engine, timeout.Token);
                var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length >= 2 && tokens[0] == "bestmove")
                    return new(tokens[1], "Ready", evaluation, mate, reachedDepth);
                if (tokens.Length == 0 || tokens[0] != "info") continue;
                // Ignore progress-only lines and bound scores; report the latest completed PV.
                if (!tokens.Contains("pv") || tokens.Contains("lowerbound") || tokens.Contains("upperbound")) continue;
                int d = Array.IndexOf(tokens, "depth"), s = Array.IndexOf(tokens, "score");
                if (d >= 0 && d + 1 < tokens.Length && int.TryParse(tokens[d + 1], out int n)) reachedDepth = n;
                if (s >= 0 && s + 2 < tokens.Length && int.TryParse(tokens[s + 2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int score))
                {
                    int signed = white ? score : -score; // Public result uses White's perspective.
                    evaluation = tokens[s + 1] == "cp" ? signed / 100.0 : null;
                    mate = tokens[s + 1] == "mate" ? signed : null;
                }
            }
        }
        catch (Exception) when (cancellation.IsCancellationRequested)
        {
            StopProcess(); // Discard buffered bestmove lines before the next position.
            throw new OperationCanceledException(cancellation);
        }
        catch (OperationCanceledException)
        {
            StopProcess();
            throw new TimeoutException("Local Stockfish did not respond. It will restart on the next analysis.");
        }
        catch { StopProcess(); throw; }
    }

    private async Task<Process> GetReadyAsync(CancellationToken cancellation)
    {
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (process is { HasExited: false }) return process;
        }
        StopProcess();
        ProcessStartInfo startInfo;
        if (startInfoFactory is not null) startInfo = await startInfoFactory(cancellation).ConfigureAwait(false);
        else
        {
            string executable = await StockfishBundle.EnsureInstalledAsync(cancellation).ConfigureAwait(false);
            startInfo = new ProcessStartInfo(executable) { WorkingDirectory = Path.GetDirectoryName(executable)! };
        }
        startInfo.UseShellExecute = false; startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardInput = startInfo.RedirectStandardOutput = startInfo.RedirectStandardError = true;
        Process engine;
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellation.ThrowIfCancellationRequested();
            engine = new Process { StartInfo = startInfo };
            process = engine;
            engine.Start();
            engine.ErrorDataReceived += (_, _) => { }; // Drain stderr so it cannot block the engine.
            engine.BeginErrorReadLine();
            try { engine.PriorityClass = ProcessPriorityClass.BelowNormal; }
            catch (System.ComponentModel.Win32Exception) { }
        }
        await SendAsync(engine, "uci", cancellation);
        await WaitForAsync(engine, "uciok", cancellation);
        await SendAsync(engine, $"setoption name Threads value {Threads}", cancellation);
        await SendAsync(engine, $"setoption name Hash value {HashMegabytes}", cancellation);
        await SendAsync(engine, "setoption name MultiPV value 1", cancellation);
        await SendAsync(engine, "isready", cancellation);
        await WaitForAsync(engine, "readyok", cancellation);
        return engine;
    }

    private static async Task SendAsync(Process engine, string command, CancellationToken cancellation)
    {
        await engine.StandardInput.WriteLineAsync(command.AsMemory(), cancellation).ConfigureAwait(false);
        await engine.StandardInput.FlushAsync(cancellation).ConfigureAwait(false);
    }
    private static async Task<string> ReadAsync(Process engine, CancellationToken cancellation) =>
        await engine.StandardOutput.ReadLineAsync(cancellation).ConfigureAwait(false)
        ?? throw new IOException("Local Stockfish exited unexpectedly.");
    private static async Task WaitForAsync(Process engine, string response, CancellationToken cancellation)
    {
        while (await ReadAsync(engine, cancellation) != response) { }
    }
    private void StopProcess()
    {
        lock (lifecycle)
        {
            var old = process; process = null;
            if (old is null) return;
            try { if (!old.HasExited) old.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            finally { old.Dispose(); }
        }
    }
    public void Dispose()
    {
        lock (lifecycle) { disposed = true; StopProcess(); }
    }
}
