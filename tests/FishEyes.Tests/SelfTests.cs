using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using OpenCvSharp;
using Rect = OpenCvSharp.Rect;

namespace FishEyes;

internal static class SelfTests
{
    private const string Start = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR";
    private static void Check(bool condition, string description)
    { if (!condition) throw new InvalidOperationException("Test failed: " + description); }

    internal sealed class FakeEngine(bool fail = false, string? forcedMove = null) : HttpMessageHandler
    {
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            await Task.Delay(180, cancellationToken);
            if (fail) return new(HttpStatusCode.ServiceUnavailable);
            string decoded = Uri.UnescapeDataString(request.RequestUri!.Query);
            string move = forcedMove ?? (decoded.Contains(" w ") ? "e2e4" : "e7e5");
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { success = true, bestmove = "bestmove " + move, evaluation = .25, mate = (int?)null }), Encoding.UTF8, "application/json") };
        }
    }

    public static async Task<object> RunAsync(string samplePath, bool live, string outputDirectory)
    {
        var watch = Stopwatch.StartNew();
        var checks = new List<string>();
        var position = ChessPosition.Parse(Start);
        Check(position.InvalidReason(true) is null && position.InvalidReason(false) is null, "both turn assumptions");
        Check(position.IsLegal("e2e4", true) && position.IsLegal("e7e5", false), "pawn moves");
        Check(!position.IsLegal("e2e5", true) && !position.IsLegal("e1e3", true) && !position.IsLegal("e7e5", true), "illegal moves rejected");
        var check = ChessPosition.Parse("4k3/8/8/8/8/8/4R3/4K3");
        Check(check.InvalidReason(true) is not null && check.InvalidReason(false) is null, "illegal turn assumption skipped");
        var pinned = ChessPosition.Parse("4r1k1/8/8/8/8/8/4R3/4K3");
        Check(!pinned.IsLegal("e2a2", true), "pinned piece");
        var promotion = ChessPosition.Parse("4k3/P7/8/8/8/8/8/4K3");
        Check(promotion.IsLegal("a7a8q", true) && !promotion.IsLegal("a7a8", true), "promotion");
        var mate = ChessPosition.Parse("7k/6Q1/5K2/8/8/8/8/8");
        Check(!mate.HasLegalMove(false), "checkmate");
        var stalemate = ChessPosition.Parse("7k/5Q2/6K1/8/8/8/8/8");
        Check(!stalemate.HasLegalMove(false), "stalemate");
        checks.Add("Chess legality, check, pin, promotion, checkmate and stalemate");
        using var source = Cv2.ImDecode(File.ReadAllBytes(samplePath), ImreadModes.Color);
        Check(!source.Empty(), "sample loads");
        var detector = new BoardDetector();
        using var recognizer = new PieceRecognizer();
        var detections = new List<object>();
        foreach (var scenario in new[] { (Width: 640, X: 173, Y: 109, Flip: false), (Width: 448, X: 527, Y: 201, Flip: false), (Width: 560, X: 81, Y: 243, Flip: true) })
        {
            using var screen = new Mat(1000, 1600, MatType.CV_8UC3, new Scalar(37, 32, 29));
            Cv2.Rectangle(screen, new Rect(10, 10, 800, 48), new Scalar(81, 72, 68), -1);
            Cv2.PutText(screen, "Board detection test", new OpenCvSharp.Point(900, 90), HersheyFonts.HersheySimplex, 1, new Scalar(220, 220, 220));
            using var board = new Mat();
            Cv2.Resize(source, board, new OpenCvSharp.Size(scenario.Width, scenario.Width));
            if (scenario.Flip)
            {
                using var original = board.Clone();
                int cell = scenario.Width / 8;
                for (int square = 0; square < 64; square++)
                {
                    using var from = new Mat(original, new Rect(square % 8 * cell, square / 8 * cell, cell, cell));
                    using var to = new Mat(board, new Rect((63 - square) % 8 * cell, (63 - square) / 8 * cell, cell, cell));
                    from.CopyTo(to);
                }
            }
            var expected = new Rect(scenario.X, scenario.Y, scenario.Width, scenario.Width);
            using (var target = new Mat(screen, expected)) board.CopyTo(target);
            var detection = detector.Detect(screen);
            Check(detection is not null, $"board detected at {expected}");
            var actual = detection!.Bounds;
            Check(Math.Abs(actual.X - expected.X) <= 5 && Math.Abs(actual.Y - expected.Y) <= 5 && Math.Abs(actual.Width - expected.Width) <= 8, $"grid bounds {actual} vs {expected}");
            var recognized = recognizer.Recognize(screen, actual);
            Check(recognized.Position.Placement == Start, $"recognition: {recognized.Position.Placement}; bounds {actual}");
            Check(recognized.WhiteBottom == !scenario.Flip, "automatic orientation");
            Check(recognized.MinConfidence >= .60 && recognized.MeanConfidence >= .95, $"confidence {recognized.MinConfidence}/{recognized.MeanConfidence}");
            detections.Add(new { expected = expected.ToString(), actual = actual.ToString(), detection.Score, recognized.MinConfidence, recognized.MeanConfidence, recognized.WhiteBottom });
        }
        using (var blank = new Mat(900, 1400, MatType.CV_8UC3, new Scalar(220, 220, 220)))
            Check(detector.Detect(blank) is null, "blank screen does not become a board");
        checks.Add("Full-screen grid detection, multiple sizes, flipped orientation, model and confidence");
        using (var band = Cv2.ImDecode(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "band-class.png")), ImreadModes.Color))
        {
            var board = detector.Detect(band);
            Check(board is not null, "Band Class textured board detected without cropping");
            var recognized = recognizer.Recognize(band, board!.Bounds);
            Check(recognized.Theme == "Band Class" && recognized.Position.Placement == Start, "Band Class screenshot exact position");
            Check(recognized.MinConfidence >= .60 && recognized.MeanConfidence >= .95, "Band Class accepted by capture confidence gate");
            var again = recognizer.Recognize(band, board.Bounds, recognized);
            Check(again.Position.Placement == Start && again.Theme == "Band Class", "cached theme and orientation");
            var normalBoard = detector.Detect(source)!;
            var changed = recognizer.Recognize(source, normalBoard.Bounds, recognized);
            Check(changed.Position.Placement == Start && changed.MinConfidence >= .60 && changed.MeanConfidence >= .95, "changing skin invalidates cached theme automatically");
        }
        checks.Add("Band Class screenshot regression, textured grid, cached recognition and live skin changes");
        const string basesPosition = "r1bqkbnr/ppp2pp1/2np4/4p2p/3PP3/2N2N2/PPP2PPP/R1BQKB1R";
        using (var review = Cv2.ImDecode(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "bases-review.png")), ImreadModes.Color))
        {
            foreach (double scale in new[] { 1.0, .7, .5 })
            {
                using var resized = new Mat();
                Cv2.Resize(review, resized, new OpenCvSharp.Size(), scale, scale, InterpolationFlags.Area);
                var board = detector.Detect(resized);
                Check(board is not null, $"Bases review grid at scale {scale}");
                var recognized = recognizer.Recognize(resized, board!.Bounds);
                Check(recognized.Theme == "Bases" && recognized.Position.Placement == basesPosition,
                    $"Bases review exact position at {scale}: {recognized.Position.Placement}");
                Check(recognized.WhiteBottom && recognized.MinConfidence >= .60 && recognized.MeanConfidence >= .95,
                    $"Bases review capture gate at {scale}");
                Check(recognized.Position.Squares[23] == '.', "review badge on h6 is not a piece");
                var repeated = recognizer.Recognize(resized, board.Bounds, recognized);
                Check(repeated.Position.Placement == basesPosition && repeated.Theme == "Bases", "Bases cached theme recognition");
            }
        }
        checks.Add("Bases review screenshot at three scales, thin outlines, highlights and move badge");
        var bounds = new Rectangle(-600, 120, 640, 640);
        Check(ArrowOverlay.SquareCenter("e2", bounds, true) == new PointF(-240, 640), "negative monitor coordinates");
        Check(ArrowOverlay.SquareCenter("e7", bounds, false) == new PointF(-320, 640), "flipped arrow coordinates");
        string cache = Path.Combine(Path.GetTempPath(), "FishEyes-test-" + Guid.NewGuid() + ".json");
        var fake = new FakeEngine();
        using (var engine = new EngineService(cache, fake))
        {
            await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => engine.AnalyzeAsync(position, true, 10)));
            Check(fake.Calls == 1, "in-flight request deduplication");
            await engine.AnalyzeAsync(position, false, 10);
            await engine.AnalyzeAsync(position, true, 10);
            await engine.AnalyzeAsync(position, false, 10);
            Check(fake.Calls == 2, "both-side results cached");
            await engine.AnalyzeAsync(position, true, 11);
            Check(fake.Calls == 3, "depth is part of cache key");
            await engine.AnalyzeAsync(mate, false, 10);
            await engine.AnalyzeAsync(check, true, 10);
            Check(fake.Calls == 3, "terminal and illegal assumptions need no HTTP call");
        }
        var afterRestart = new FakeEngine();
        using (var engine = new EngineService(cache, afterRestart))
        {
            Check((await engine.AnalyzeAsync(position, true, 10)).FromCache, "persistent cache");
            await engine.AnalyzeAsync(position, false, 10);
            Check(afterRestart.Calls == 0, "no repeat requests after restart");
        }
        File.Delete(cache);
        var failed = new FakeEngine(fail: true);
        using (var engine = new EngineService(null, failed))
        {
            for (int i = 0; i < 3; i++)
            {
                try { await engine.AnalyzeAsync(position, true, 10); throw new Exception("Expected API failure"); }
                catch (Exception e) when (e is HttpRequestException or InvalidOperationException) { }
            }
            Check(failed.Calls == 1, "failed request backs off instead of retrying each second");
        }
        using (var engine = new EngineService(null, new FakeEngine(forcedMove: "e2e5")))
        {
            bool rejected = false;
            try { await engine.AnalyzeAsync(position, true, 10); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "illegal API move rejected");
        }
        var cancellable = new FakeEngine();
        using (var engine = new EngineService(null, cancellable))
        {
            var first = engine.AnalyzeAsync(position, true, 10);
            var second = engine.AnalyzeAsync(position, false, 10);
            await Task.Delay(40);
            engine.CancelPending();
            try { await Task.WhenAll(first, second); } catch (OperationCanceledException) { }
            Check(cancellable.Calls <= 1, "Off cancels the active and queued API calls");
            Check((await engine.AnalyzeAsync(position, true, 10)).Value.Move == "e2e4", "resume after cancellation");
        }
        checks.Add("Cache per side/depth, concurrent deduplication, disk persistence, backoff and API validation");
        object? liveResult = null;
        if (live)
        {
            using var engine = new EngineService(null);
            var white = await engine.AnalyzeAsync(position, true, 10);
            var black = await engine.AnalyzeAsync(position, false, 10);
            await engine.AnalyzeAsync(position, true, 10);
            await engine.AnalyzeAsync(position, false, 10);
            Check(engine.RequestCount == 2, "real API cache");
            string outputImage = Path.Combine(outputDirectory, "move-arrows.png");
            using var annotated = new Bitmap(samplePath);
            using (var graphics = Graphics.FromImage(annotated)) ArrowOverlay.DrawMoves(graphics, new Rectangle(0, 0, annotated.Width, annotated.Height), true, white.Value, black.Value);
            annotated.Save(outputImage, System.Drawing.Imaging.ImageFormat.Png);
            liveResult = new { white = white.Value, black = black.Value, engine.RequestCount, image = outputImage };
            checks.Add("Real Stockfish API, both sides, repeated requests served from cache, arrow image");
        }
        return new { passed = true, checks, detections, liveResult, seconds = watch.Elapsed.TotalSeconds };
    }

    public static int RunGui(string samplePath, string output)
    {
        using var image = new Bitmap(samplePath);
        using var board = new Form { Text = "FishEyes test board", FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(180, 240), ClientSize = new System.Drawing.Size(640, 640), TopMost = true, BackgroundImage = image, BackgroundImageLayout = ImageLayout.Stretch };
        var fake = new FakeEngine();
        using var service = new EngineService(null, fake);
        using var panel = new MainForm(service);
        using var timer = new System.Windows.Forms.Timer { Interval = 300 };
        var watch = Stopwatch.StartNew();
        bool paused = false;
        int capturesAtPause = 0;
        double pausedAt = 0;
        int exit = 1;
        static IEnumerable<Control> Descendants(Control parent) => parent.Controls.Cast<Control>()
            .SelectMany(child => new[] { child }.Concat(Descendants(child)));
        var controls = Descendants(panel).ToArray();
        var power = controls.OfType<Button>().Single(button => button.AccessibleName == "Turn on analysis");
        var increase = controls.OfType<Button>().Single(button => button.AccessibleName == "Increase search depth");
        var decrease = controls.OfType<Button>().Single(button => button.AccessibleName == "Decrease search depth");
        var close = controls.OfType<Button>().Single(button => button.AccessibleName == "Close FishEyes");
        var disclosure = controls.OfType<Button>().Single(button => button.AccessibleName == "Show detected board");
        var detectedBoard = controls.OfType<BoardPreview>().Single();
        var depthInput = controls.OfType<TextBox>().Single();
        board.Show();
        panel.Shown += async (_, _) =>
        {
            try
            {
                // Controls need a live Windows message loop; keep these checks
                // in the GUI suite so async console tests cannot inherit one.
                using (var preview = new BoardPreview())
                {
                    var recognized = new Recognition(ChessPosition.Parse(Start), true, 1, 1);
                    preview.UpdateBoard(new BoardFrame(recognized, Rectangle.Empty, 1), true);
                    Check(preview.PieceAtDisplaySquare(0, 0) == 'r' && preview.PieceAtDisplaySquare(7, 4) == 'K', "preview white-bottom mapping");
                    preview.UpdateBoard(new BoardFrame(recognized with { WhiteBottom = false }, Rectangle.Empty, 1), true);
                    Check(preview.PieceAtDisplaySquare(0, 0) == 'R' && preview.PieceAtDisplaySquare(7, 3) == 'k', "preview black-bottom mapping");
                    var uncertain = new BoardFrame(recognized with { MinConfidence = .2f }, Rectangle.Empty, 1);
                    preview.UpdateBoard(uncertain, true);
                    Check(!uncertain.IsReliable && preview.AccessibleDescription!.StartsWith("Uncertain"), "uncertain preview is inspectable but not analyzable");
                    preview.UpdateBoard(null, false);
                    Check(preview.Frame is null && preview.PieceAtDisplaySquare(0, 0) == '.', "paused preview clears stale pieces");
                }
                increase.PerformClick(); Check(depthInput.Text == "13", "depth increment button");
                decrease.PerformClick(); Check(depthInput.Text == "12", "depth decrement button");
                depthInput.Focus(); depthInput.Text = "99"; power.Focus();
                Check(depthInput.Text == "15", "typed depth is clamped to 15");
                depthInput.Focus(); depthInput.Text = "12"; power.Focus();
                using (var preview = new Bitmap(panel.Width, panel.Height))
                {
                    panel.DrawToBitmap(preview, new Rectangle(0, 0, panel.Width, panel.Height));
                    preview.Save(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "overlay-paused.png"));
                }
                using (var sequence = new Bitmap(power.Width * 3, power.Height))
                {
                    void CaptureToggle(int frame)
                    {
                        using var shot = new Bitmap(power.Width, power.Height);
                        power.DrawToBitmap(shot, new Rectangle(0, 0, shot.Width, shot.Height));
                        using var g = Graphics.FromImage(sequence);
                        g.DrawImageUnscaled(shot, frame * power.Width, 0);
                    }
                    CaptureToggle(0);
                    power.PerformClick(); Check(panel.IsRunning, "On switch starts capture");
                    await Task.Delay(65);
                    CaptureToggle(1);
                    await Task.Delay(220);
                    CaptureToggle(2);
                    sequence.Save(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "toggle-animation.png"));
                }
                Check(panel.Icon is not null && panel.Icon.Width == 32, "custom taskbar icon loaded");
                timer.Start();
            }
            catch (Exception e)
            {
                File.WriteAllText(output, JsonSerializer.Serialize(new { passed = false, error = e.ToString() }));
                panel.Close();
            }
        };
        timer.Tick += async (_, _) =>
        {
            try
            {
                if (watch.Elapsed.TotalSeconds > 40) throw new Exception($"GUI test timeout: captures={panel.CaptureCount}, arrows={panel.ArrowCount}, requests={fake.Calls}, board={panel.CurrentFrame?.Recognition.Position.Placement}");
                if (!paused && panel.CaptureCount >= 5 && panel.ArrowCount == 2)
                {
                    timer.Stop();
                    Check(panel.CurrentFrame?.Recognition.Position.Placement == Start, "live screen recognition");
                    Check(fake.Calls == 2, "live captures do not repeat API calls");
                    Check(panel.ArrowsClickThrough, "native click-through arrows");
                    Check(panel.ExcludesOwnWindows, "overlay excluded from captured screen");
                    Check(detectedBoard.Frame?.Recognition.Position.Placement == Start, "collapsed preview receives live detection");
                    int collapsedHeight = panel.Height;
                    var area = Screen.FromControl(panel).WorkingArea;
                    panel.Top = area.Bottom - panel.Height;
                    disclosure.PerformClick();
                    await Task.Delay(70);
                    int intermediateHeight = panel.Height;
                    using (var shot = new Bitmap(panel.Width, panel.Height))
                    {
                        panel.DrawToBitmap(shot, new Rectangle(0, 0, shot.Width, shot.Height));
                        shot.Save(Path.Combine(Path.GetDirectoryName(output)!, "preview-animation.png"));
                    }
                    await Task.Delay(250);
                    int expandedHeight = panel.Height;
                    Check(expandedHeight > collapsedHeight && intermediateHeight > collapsedHeight && intermediateHeight <= expandedHeight, "preview expands with animation");
                    Check(panel.Bottom <= area.Bottom && detectedBoard.Visible, "expanded preview stays on screen");
                    Check(disclosure.AccessibleName == "Hide detected board", "preview disclosure accessible state");
                    using (var preview = new Bitmap(panel.Width, panel.Height))
                    {
                        panel.DrawToBitmap(preview, new Rectangle(0, 0, panel.Width, panel.Height));
                        preview.Save(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "overlay.png"));
                    }
                    disclosure.PerformClick();
                    await Task.Delay(55);
                    disclosure.PerformClick();
                    await Task.Delay(280);
                    Check(panel.Height == expandedHeight && detectedBoard.Visible, "mid-animation reversal reaches expanded state");
                    disclosure.PerformClick();
                    await Task.Delay(280);
                    Check(panel.Height == collapsedHeight && !detectedBoard.Visible, "preview collapses fully");
                    power.PerformClick();
                    Check(!panel.IsRunning, "Off switch pauses capture");
                    Check(detectedBoard.Frame is null, "pause clears detected preview");
                    capturesAtPause = panel.CaptureCount; pausedAt = watch.Elapsed.TotalSeconds; paused = true;
                    timer.Start();
                }
                if (paused && watch.Elapsed.TotalSeconds - pausedAt > 2.5)
                {
                    Check(panel.CaptureCount == capturesAtPause, "Off stops captures");
                    Check(panel.ArrowCount == 0 && fake.Calls == 2, "Off clears arrows and stops requests");
                    File.WriteAllText(output, JsonSerializer.Serialize(new { passed = true, captures = capturesAtPause, requests = fake.Calls, clickThrough = true, captureExclusion = true, bothArrows = true, offStopsCapture = true, depthControls = true, onOffSwitch = true, boardPreview = true, previewAnimation = true, previewReversal = true, dpi = panel.DeviceDpi, seconds = watch.Elapsed.TotalSeconds }, new JsonSerializerOptions { WriteIndented = true }));
                    exit = 0; timer.Stop(); close.PerformClick();
                }
            }
            catch (Exception e)
            {
                File.WriteAllText(output, JsonSerializer.Serialize(new { passed = false, error = e.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
                timer.Stop(); panel.Close();
            }
        };
        Application.Run(panel);
        board.Close();
        return exit;
    }
}
