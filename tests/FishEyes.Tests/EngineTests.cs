using System.Diagnostics;

namespace FishEyes;

internal static class EngineTests
{
    private const string Start = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR";
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Engine test failed: " + message); }
    private static bool IsAlive(int id)
    {
        try { using var p = Process.GetProcessById(id); return !p.HasExited; }
        catch (ArgumentException) { return false; }
    }
    private static LocalStockfishEngine Fixture(string log, string mode = "normal", int timeout = 3000) =>
        new(_ =>
        {
            var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "FishEyes.Tests.exe"));
            info.ArgumentList.Add("--uci-fixture"); info.ArgumentList.Add(mode); info.ArgumentList.Add(log);
            return Task.FromResult(info);
        }, TimeSpan.FromMilliseconds(timeout));

    // A genuine child-process protocol fixture: covers pipe framing, scores, crashes and timeout cleanup.
    public static int RunFixture(string[] args)
    {
        string mode = args[1], log = args[2];
        bool white = true;
        string? command;
        while ((command = Console.ReadLine()) is not null)
        {
            File.AppendAllText(log, command + "\n");
            if (command == "uci") Console.WriteLine("id name Fixture\nuciok");
            if (command == "isready") Console.WriteLine("readyok");
            if (command.StartsWith("position fen ")) white = command.Contains(" w ");
            if (command.StartsWith("go "))
            {
                if (mode == "crash") return 3;
                if (mode == "hang") { Thread.Sleep(10000); continue; }
                Console.WriteLine($"info depth 9 multipv 1 score cp 125 nodes 100 pv {(white ? "e2e4" : "e7e5")}");
                Console.WriteLine("info depth 10 score cp 999 lowerbound pv e2e4");
                Console.WriteLine("info string this line has no score");
                if (mode == "mate") Console.WriteLine("info depth 11 score mate 3 pv e7e5");
                Console.WriteLine($"bestmove {(white ? "e2e4" : "e7e5")} ponder a2a3");
            }
            Console.Out.Flush();
        }
        return 0;
    }

    public static async Task RunProtocolAsync(string outputDirectory)
    {
        string log = Path.Combine(outputDirectory, "uci-commands.txt");
        File.WriteAllText(log, "");
        var position = ChessPosition.Parse(Start);
        int pid;
        using (var engine = Fixture(log))
        {
            var white = await engine.AnalyzeAsync(position.Fen(true), 40, default);
            pid = engine.ProcessId!.Value;
            var black = await engine.AnalyzeAsync(position.Fen(false), 20, default);
            Check(white.Move == "e2e4" && white.Evaluation == 1.25 && white.Depth == 9, "white score, PV depth and bestmove parsed");
            Check(black.Move == "e7e5" && black.Evaluation == -1.25, "black score converted to White's perspective");
            Check(pid == engine.ProcessId, "reuse one engine process");
            string[] commands = File.ReadAllLines(log);
            Check(commands.Count(c => c == "uci") == 1 && commands.Contains("go depth 40 movetime 500"), "handshake and bounded search");
            Check(commands.Contains("setoption name Hash value 128"), "memory setting");
        }
        await Task.Delay(80);
        Check(!IsAlive(pid), "dispose terminates engine");
        using (var engine = Fixture(log, "mate"))
        {
            var result = await engine.AnalyzeAsync(position.Fen(false), 20, default);
            Check(result.Mate == -3 && result.Evaluation is null && result.Depth == 11, "mate score replaces centipawn score");
        }
        foreach (string mode in new[] { "hang", "crash" })
        {
            using var engine = Fixture(log, mode, timeout: 1000);
            bool failed = false;
            try { await engine.AnalyzeAsync(position.Fen(true), 20, default); }
            catch (Exception e) when (e is TimeoutException or IOException) { failed = true; }
            Check(failed && engine.ProcessId is null, mode + " cleans up child process");
        }
        using (var engine = Fixture(log, "hang"))
        {
            using var cancel = new CancellationTokenSource();
            var task = engine.AnalyzeAsync(position.Fen(true), 40, cancel.Token);
            for (int i = 0; i < 100 && engine.ProcessId is null; i++) await Task.Delay(10);
            pid = engine.ProcessId!.Value;
            cancel.Cancel();
            bool cancelled = false;
            try { await task; } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && engine.ProcessId is null, "cancel stops active engine");
            await Task.Delay(80);
            Check(!IsAlive(pid), "cancel leaves no child running");
        }
        // A cancelled generation must not remove an immediately restarted request for the same key.
        var fake = new SelfTests.FakeEngine();
        using (var service = new EngineService(null, fake))
        {
            var old = service.AnalyzeAsync(position, true, 12);
            await Task.Delay(40);
            service.CancelPending();
            var next = service.AnalyzeAsync(position, true, 12);
            try { await old; } catch (OperationCanceledException) { }
            var duplicate = service.AnalyzeAsync(position, true, 12);
            await Task.WhenAll(next, duplicate);
            Check(fake.Calls == 2, "rapid restart preserves deduplication");
        }
        string cache = Path.Combine(outputDirectory, "engine-identity-cache.json");
        File.WriteAllText(cache, "{}");
        using (var service = new EngineService(cache, new SelfTests.FakeEngine(identity: "engine-v1-ms500")))
            await service.AnalyzeAsync(position, true, 12);
        var replacement = new SelfTests.FakeEngine(identity: "engine-v2-ms1000");
        using (var service = new EngineService(cache, replacement))
        {
            Check(!(await service.AnalyzeAsync(position, true, 12)).FromCache && replacement.Calls == 1,
                "engine version or settings change cannot reuse older results");
        }
    }

    public static async Task<object> RunRealAsync()
    {
        var position = ChessPosition.Parse(Start);
        var backend = new LocalStockfishEngine();
        int pid;
        var watch = Stopwatch.StartNew();
        Analysis white, black;
        using (var service = new EngineService(null, backend))
        {
            white = (await service.AnalyzeAsync(position, true, 40)).Value;
            double firstMs = watch.Elapsed.TotalMilliseconds;
            pid = backend.ProcessId!.Value;
            watch.Restart();
            black = (await service.AnalyzeAsync(position, false, 40)).Value;
            Check(pid == backend.ProcessId && white.Depth > 0 && black.Depth > 0, "real engine reuses process and reports depth");
            Check(position.IsLegal(white.Move!, true) && position.IsLegal(black.Move!, false), "real engine moves legal");
            Check((await service.AnalyzeAsync(position, true, 40)).FromCache && service.RequestCount == 2, "real local cache");
            double secondMs = watch.Elapsed.TotalMilliseconds;
            // Kill the idle process externally to simulate a crash, then verify recovery.
            using (var child = Process.GetProcessById(pid)) { child.Kill(); await child.WaitForExitAsync(); }
            await service.AnalyzeAsync(position, true, 39);
            Check(backend.ProcessId != pid, "recover from exited engine");
            var old = service.AnalyzeAsync(position, true, 38);
            var queued = service.AnalyzeAsync(position, false, 38);
            await Task.Delay(80);
            service.CancelPending();
            var resumed = service.AnalyzeAsync(position, true, 38);
            try { await Task.WhenAll(old, queued); } catch (OperationCanceledException) { }
            Check(position.IsLegal((await resumed).Value.Move!, true), "real engine resumes after cancellation");
            pid = backend.ProcessId!.Value;
            service.Dispose();
            await Task.Delay(80);
            Check(!IsAlive(pid), "real process closed on dispose");
            return new { white, black, firstMs, secondMs, backend.Threads, hashMiB = LocalStockfishEngine.HashMegabytes, passed = true };
        }
    }
}
