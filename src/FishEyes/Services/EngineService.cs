using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FishEyes;

public record Analysis(string? Move, string Status, double? Evaluation = null, int? Mate = null);
public record CachedAnalysis(Analysis Value, bool FromCache);

public sealed class EngineService : IDisposable
{
    public const string Endpoint = "https://stockfish.online/api/s/v2.php";
    private readonly HttpClient http;
    private readonly string? cachePath;
    private readonly object gate = new();
    private readonly Dictionary<string, Analysis> completed;
    private readonly Dictionary<string, Task<Analysis>> pending = new();
    private readonly Dictionary<string, (DateTime Until, int Attempts, string Message)> failures = new();
    private readonly SemaphoreSlim requestGate = new(1);
    private readonly CancellationTokenSource shutdown = new();
    private CancellationTokenSource activeRequests = new();
    public int RequestCount { get; private set; }
    public string? CacheWarning { get; private set; }
    public EngineService(string? cachePath, HttpMessageHandler? handler = null)
    {
        this.cachePath = cachePath;
        http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = TimeSpan.FromSeconds(35);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("FishEyes/2.0");
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
        if (depth is < 1 or > 15) throw new ArgumentOutOfRangeException(nameof(depth));
        string? invalid = position.InvalidReason(white);
        if (invalid is not null) return new(new(null, "Turn not legal"), true);
        if (!position.HasLegalMove(white)) return new(new(null, "No legal move"), true);
        string fen = position.Fen(white), key = $"v1|{depth}|{fen}";
        Task<Analysis> task;
        lock (gate)
        {
            if (completed.TryGetValue(key, out var cached))
            {
                if (cached?.Move is not null && position.IsLegal(cached.Move, white)) return new(cached, true);
                completed.Remove(key); // Never trust a damaged or manually edited cache.
            }
            if (pending.TryGetValue(key, out var running)) task = running;
            else
            {
                if (failures.TryGetValue(key, out var failure) && failure.Until > DateTime.UtcNow)
                    throw new InvalidOperationException($"Retry in {Math.Ceiling((failure.Until - DateTime.UtcNow).TotalSeconds)}s: {failure.Message}");
                var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token, activeRequests.Token);
                task = Task.Run(async () =>
                {
                    using (requestCancellation)
                        return await FetchAsync(position, white, depth, key, requestCancellation.Token);
                });
                pending.Add(key, task);
            }
        }
        return new(await task.WaitAsync(cancellation), false);
    }
    private async Task<Analysis> FetchAsync(ChessPosition position, bool white, int depth, string key, CancellationToken cancellation)
    {
        try
        {
            await requestGate.WaitAsync(cancellation);
            Analysis value;
            try
            {
                RequestCount++;
                string url = $"{Endpoint}?fen={Uri.EscapeDataString(position.Fen(white))}&depth={depth}";
                using var response = await http.GetAsync(url, cancellation);
                response.EnsureSuccessStatusCode();
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
                var data = document.RootElement;
                if (!data.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
                    throw new InvalidOperationException("The engine rejected the position.");
                string best = data.TryGetProperty("bestmove", out var bestMove) ? bestMove.GetString() ?? "" : "";
                string move = Regex.Match(best, @"\b[a-h][1-8][a-h][1-8][qrbn]?\b").Value;
                if (!position.IsLegal(move, white)) throw new InvalidOperationException("The engine returned an invalid move.");
                double? evaluation = data.TryGetProperty("evaluation", out var ev) && ev.ValueKind == JsonValueKind.Number && ev.TryGetDouble(out double n) ? n : null;
                int? mate = data.TryGetProperty("mate", out var mt) && mt.ValueKind == JsonValueKind.Number && mt.TryGetInt32(out int m) ? m : null;
                value = new(move, "Ready", evaluation, mate);
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
        finally { lock (gate) pending.Remove(key); }
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
        lock (gate) { old = activeRequests; activeRequests = new(); }
        old.Cancel(); old.Dispose();
    }
    public void Dispose() { shutdown.Cancel(); http.Dispose(); }
}
