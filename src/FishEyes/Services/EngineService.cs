using System.Text.Json;

namespace FishEyes;

public record Analysis(string? Move, string Status, double? Evaluation = null, int? Mate = null, int? Depth = null);
public record CachedAnalysis(Analysis Value, bool FromCache);

public sealed class EngineService : IDisposable
{
    public const int MinimumDepth = 1;
    public const int MaximumDepth = 40;
    private readonly IAnalysisEngine backend;
    private readonly string? cachePath;
    private readonly object gate = new();
    private readonly Dictionary<string, Analysis> completed;
    private readonly Dictionary<string, (Task<Analysis> Task, int Generation)> pending = new();
    private readonly Dictionary<string, (DateTime Until, int Attempts, string Message)> failures = new();
    private readonly SemaphoreSlim requestGate = new(1);
    private readonly CancellationTokenSource shutdown = new();
    private CancellationTokenSource activeRequests = new();
    private int generation;
    private bool disposed;
    public int RequestCount { get; private set; }
    public string? CacheWarning { get; private set; }
    public EngineService(string? cachePath, IAnalysisEngine? backend = null)
    {
        this.cachePath = cachePath;
        this.backend = backend ?? new LocalStockfishEngine();
        completed = new();
        if (cachePath is not null && File.Exists(cachePath))
        {
            try { completed = JsonSerializer.Deserialize<Dictionary<string, Analysis>>(File.ReadAllText(cachePath)) ?? new(); }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            { CacheWarning = "Saved cache could not be read; using memory cache."; }
        }
    }
    public async Task<CachedAnalysis> AnalyzeAsync(ChessPosition position, bool white, int depth, CancellationToken cancellation = default)
    {
        if (depth is < MinimumDepth or > MaximumDepth) throw new ArgumentOutOfRangeException(nameof(depth));
        string? invalid = position.InvalidReason(white);
        if (invalid is not null) return new(new(null, "Turn not legal"), true);
        if (!position.HasLegalMove(white)) return new(new(null, "No legal move"), true);
        string fen = position.Fen(white), key = $"{backend.CacheIdentity}|{depth}|{fen}";
        Task<Analysis> task;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (completed.TryGetValue(key, out var cached))
            {
                if (cached?.Move is not null && position.IsLegal(cached.Move, white)) return new(cached, true);
                completed.Remove(key); // Never trust a damaged or manually edited cache.
            }
            if (pending.TryGetValue(key, out var running)) task = running.Task;
            else
            {
                if (failures.TryGetValue(key, out var failure) && failure.Until > DateTime.UtcNow)
                    throw new InvalidOperationException($"Retry in {Math.Ceiling((failure.Until - DateTime.UtcNow).TotalSeconds)}s: {failure.Message}");
                var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token, activeRequests.Token);
                int requestGeneration = generation;
                task = Task.Run(async () =>
                {
                    using (requestCancellation)
                        return await FetchAsync(position, white, depth, key, requestGeneration, requestCancellation.Token);
                });
                pending.Add(key, (task, requestGeneration));
            }
        }
        return new(await task.WaitAsync(cancellation), false);
    }
    private async Task<Analysis> FetchAsync(ChessPosition position, bool white, int depth, string key, int requestGeneration, CancellationToken cancellation)
    {
        try
        {
            await requestGate.WaitAsync(cancellation);
            Analysis value;
            try
            {
                RequestCount++;
                value = await backend.AnalyzeAsync(position.Fen(white), depth, cancellation).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                if (value.Move is null || !position.IsLegal(value.Move, white))
                    throw new InvalidOperationException("The engine returned an invalid move.");
            }
            finally { requestGate.Release(); }
            lock (gate)
            {
                completed[key] = value;
                failures.Remove(key);
                SaveCache();
            }
            return value;
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellation.IsCancellationRequested)
        {
            lock (gate)
            {
                int attempts = failures.TryGetValue(key, out var previous) ? previous.Attempts + 1 : 1;
                string message = e is TaskCanceledException ? "Engine timed out." : e.Message;
                failures[key] = (DateTime.UtcNow.AddSeconds(Math.Min(300, 15 * Math.Pow(2, Math.Min(5, attempts - 1)))), attempts, message);
            }
            throw;
        }
        finally
        {
            lock (gate)
                if (pending.TryGetValue(key, out var item) && item.Generation == requestGeneration) pending.Remove(key);
        }
    }
    private void SaveCache()
    {
        if (cachePath is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            string temporary = cachePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(completed));
            File.Move(temporary, cachePath, true);
            CacheWarning = null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { CacheWarning = "Cannot save cache; repeats are still cached until exit."; }
    }
    public void CancelPending()
    {
        CancellationTokenSource old;
        lock (gate)
        {
            if (disposed) return;
            old = activeRequests; activeRequests = new();
            generation++; pending.Clear();
        }
        old.Cancel(); old.Dispose();
    }
    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; }
        shutdown.Cancel(); backend.Dispose();
        activeRequests.Dispose(); shutdown.Dispose();
    }
}
