using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Ironwake.Content;
using Ironwake.ConsoleHarness.Viz;
using Ironwake.Core;
using Xunit;

namespace Ironwake.Console.Tests
{
    /// <summary>
    /// The viewer is checked without a browser: it is a file, and the properties that matter
    /// — self-contained, complete, reproducible — are all readable from its bytes.
    /// </summary>
    public class MatchViewerTests : IDisposable
    {
        private readonly string _dir;

        public MatchViewerTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ironwake-viz-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); }
            catch (IOException) { /* a stray temp dir is not worth failing a test over */ }
        }

        private string PathFor(string name) => Path.Combine(_dir, name);

        private static (MatchRecording Recording, IContentPack Content) Play(ulong seed)
        {
            var content = StarterPack.Load();
            IGameEngine engine = new StubEngine(content);
            var state = SampleGame.Create(content, seed);
            return (MatchRecorder.Record(engine, state, seed), content);
        }

        private string WriteViewer(ulong seed, string fileName)
        {
            var (recording, content) = Play(seed);
            var path = PathFor(fileName);
            HtmlWriter.Write(path, recording, content);
            return path;
        }

        // ---- it exists and has content -----------------------------------------

        [Fact]
        public void TheFileIsProducedAndIsNotEmpty()
        {
            var path = WriteViewer(777UL, "match.html");

            Assert.True(File.Exists(path), $"no file at {path}");

            var html = File.ReadAllText(path);
            Assert.True(html.Length > 5000, $"the viewer is only {html.Length} characters — suspiciously thin");
            Assert.StartsWith("<!DOCTYPE html>", html);
            Assert.Contains("</html>", html);
        }

        // ---- self-contained ------------------------------------------------------

        [Fact]
        public void TheFileContainsNoExternalReferences()
        {
            // The whole point: it must open with no network. A single CDN link breaks that,
            // and would show up here as a URL.
            var html = File.ReadAllText(WriteViewer(777UL, "match.html"));

            Assert.DoesNotContain("http://", html);
            Assert.DoesNotContain("https://", html);
        }

        [Fact]
        public void TheFileLoadsNoScriptsStylesheetsOrImages()
        {
            // Belt and braces: a relative src="viewer.js" carries no scheme and would slip
            // past the URL check above while still breaking a file moved on its own.
            var html = File.ReadAllText(WriteViewer(777UL, "match.html"));

            Assert.DoesNotMatch(new Regex(@"<script[^>]*\ssrc\s*=", RegexOptions.IgnoreCase), html);
            Assert.DoesNotMatch(new Regex(@"<link[^>]*\shref\s*=", RegexOptions.IgnoreCase), html);
            Assert.DoesNotMatch(new Regex(@"<img[^>]*\ssrc\s*=", RegexOptions.IgnoreCase), html);
            Assert.DoesNotMatch(new Regex(@"<iframe", RegexOptions.IgnoreCase), html);
            Assert.DoesNotMatch(new Regex(@"@import", RegexOptions.IgnoreCase), html);
        }

        // ---- completeness ---------------------------------------------------------

        [Fact]
        public void StepCountEqualsTheNumberOfActionsTaken()
        {
            var (recording, content) = Play(777UL);
            var path = PathFor("match.html");
            HtmlWriter.Write(path, recording, content);

            var html = File.ReadAllText(path);

            var declared = int.Parse(Regex.Match(html, @"data-step-count=""(\d+)""").Groups[1].Value);
            Assert.Equal(recording.Steps.Count, declared);

            // A frame per action, plus one for the starting position.
            var frames = int.Parse(Regex.Match(html, @"data-frame-count=""(\d+)""").Groups[1].Value);
            Assert.Equal(recording.Steps.Count + 1, frames);

            // And the match must actually have gone somewhere.
            Assert.True(recording.Steps.Count > 10, $"only {recording.Steps.Count} steps were recorded");
        }

        [Fact]
        public void EveryStepsEventsAppearInTheFile()
        {
            var (recording, content) = Play(777UL);
            var html = HtmlWriter.Render(recording, content);

            // Spot-check the dice, which are the events most likely to be dropped.
            var described = recording.Steps
                .SelectMany(s => s.Events)
                .Select(e => e.Describe())
                .ToList();

            Assert.NotEmpty(described);
            Assert.Contains(described, d => d.StartsWith("to-hit", StringComparison.Ordinal));

            // Event text is embedded as JSON, so compare on a distinctive substring that
            // survives escaping.
            Assert.Contains("to-hit", html);
            Assert.Contains("Match over", html);
        }

        [Fact]
        public void TheBoardTerrainAndLegendAreRendered()
        {
            var html = File.ReadAllText(WriteViewer(777UL, "match.html"));

            foreach (var terrain in new[] { "Open", "Cover", "Obscuring", "Elevated", "Impassable" })
            {
                Assert.Contains($"t-{terrain}", html);
                Assert.Contains($">{terrain}<", html);   // the legend entry
            }

            Assert.Contains("<polygon class=\"hex", html);
            Assert.Contains("id=\"slider\"", html);
            Assert.Contains("id=\"prev\"", html);
            Assert.Contains("id=\"next\"", html);
        }

        [Fact]
        public void HeaderFactsComeFromTheRecording()
        {
            var (recording, content) = Play(777UL);
            var html = HtmlWriter.Render(recording, content);

            Assert.Contains("777", html);
            Assert.Contains(content.Version, html);
        }

        [Fact]
        public void OneCircleAndLabelIsEmittedPerUnit()
        {
            var (recording, content) = Play(777UL);
            var html = HtmlWriter.Render(recording, content);

            int expected = recording.InitialState.Units.Count;

            Assert.Equal(expected, Regex.Matches(html, @"<circle id=""uc\d+""").Count);
            Assert.Equal(expected, Regex.Matches(html, @"<text id=""ul\d+""").Count);
        }

        // ---- reproducible ----------------------------------------------------------

        [Fact]
        public void TheSameSeedProducesAByteIdenticalFile()
        {
            var first = WriteViewer(777UL, "first.html");
            var second = WriteViewer(777UL, "second.html");

            Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
        }

        [Theory]
        [InlineData(1UL)]
        [InlineData(777UL)]
        [InlineData(12345UL)]
        public void RenderingIsStableAcrossRepeatedCalls(ulong seed)
        {
            var (recording, content) = Play(seed);

            var reference = HtmlWriter.Render(recording, content);
            for (int i = 0; i < 5; i++)
                Assert.Equal(reference, HtmlWriter.Render(recording, content));
        }

        [Fact]
        public void DifferentSeedsProduceDifferentFiles()
        {
            // Guards the test above from passing because the writer ignores the match entirely.
            var a = File.ReadAllText(WriteViewer(777UL, "a.html"));
            var b = File.ReadAllText(WriteViewer(999UL, "b.html"));

            Assert.NotEqual(a, b);
        }

        [Fact]
        public void TheFileCarriesNoTimestamp()
        {
            // A generation date would be the obvious way to break byte-identical output.
            var html = File.ReadAllText(WriteViewer(777UL, "match.html"));

            Assert.DoesNotContain(DateTime.Now.Year.ToString(), html);
        }
    }

    /// <summary>The recorder itself, independently of how it is rendered.</summary>
    public class MatchRecorderTests
    {
        private static (IGameEngine Engine, GameState State, IContentPack Content) Fresh(ulong seed)
        {
            var content = StarterPack.Load();
            return (new StubEngine(content), SampleGame.Create(content, seed), content);
        }

        [Fact]
        public void ARecordedMatchReachesAConclusion()
        {
            var (engine, state, _) = Fresh(777UL);

            var recording = MatchRecorder.Record(engine, state, 777UL);

            Assert.True(recording.Completed);
            Assert.Equal(PhaseKind.Complete, recording.FinalState.Phase);
        }

        [Fact]
        public void EveryStepKeepsItsActionEventsAndResultingState()
        {
            var (engine, state, _) = Fresh(777UL);

            var recording = MatchRecorder.Record(engine, state, 777UL);

            Assert.All(recording.Steps, step =>
            {
                Assert.NotNull(step.Action);
                Assert.NotNull(step.Events);
                Assert.NotNull(step.StateAfter);
            });

            // Indices are contiguous and ordered.
            Assert.Equal(Enumerable.Range(0, recording.Steps.Count), recording.Steps.Select(s => s.Index));
        }

        [Fact]
        public void TheRecordingRollsDice()
        {
            // Without this, a recording of a match where nobody acts would still look fine.
            var (engine, state, _) = Fresh(777UL);

            var recording = MatchRecorder.Record(engine, state, 777UL);

            Assert.Contains(recording.Steps.SelectMany(s => s.Events), e => e is DiceRolledEvent);
            Assert.True(recording.FinalState.Rng.Consumed > 0);
        }

        [Fact]
        public void TheSameSeedRecordsTheSameMatch()
        {
            var (engineA, stateA, _) = Fresh(777UL);
            var (engineB, stateB, _) = Fresh(777UL);

            var first = MatchRecorder.Record(engineA, stateA, 777UL);
            var second = MatchRecorder.Record(engineB, stateB, 777UL);

            Assert.Equal(first.Steps.Count, second.Steps.Count);
            Assert.Equal(
                first.Steps.SelectMany(s => s.Events).Select(e => e.Describe()),
                second.Steps.SelectMany(s => s.Events).Select(e => e.Describe()));
        }

        [Fact]
        public void SnapshotsAreDistinctStatesNotOneMutatedInstance()
        {
            // GameState is immutable, so each step must hold its own snapshot. If they were
            // the same object the slider would show the final position at every step.
            var (engine, state, _) = Fresh(777UL);

            var recording = MatchRecorder.Record(engine, state, 777UL);

            var snapshots = recording.Steps.Select(s => s.StateAfter).ToList();
            Assert.True(snapshots.Distinct().Count() == snapshots.Count,
                "steps are sharing a GameState instance");

            Assert.NotSame(recording.InitialState, recording.FinalState);
            Assert.Equal(1, recording.InitialState.Round);
        }
    }
}
