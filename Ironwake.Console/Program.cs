using System;
using System.Collections.Generic;
using System.IO;
using Ironwake.Content;
using Ironwake.ConsoleHarness.Viz;
using Ironwake.Core;

namespace Ironwake.ConsoleHarness
{
    /// <summary>
    /// Headless proof that the engine loop works with no Unity involved.
    ///
    ///   dotnet run --project Ironwake.Console                      print the combat log
    ///   dotnet run --project Ironwake.Console 777                  ... with a given seed
    ///   dotnet run --project Ironwake.Console -- --html match.html write a match viewer
    ///   dotnet run --project Ironwake.Console -- --html m.html --seed 777
    ///   dotnet run --project Ironwake.Console -- --sweep 200      balance statistics
    ///
    /// Plays a full match and prints the combat log. Run it twice with the same seed and the
    /// output must be byte-identical — that is the determinism check.
    /// </summary>
    internal static class Program
    {
        private const ulong DefaultSeed = 12345UL;

        private static int Main(string[] args)
        {
            Options options;
            try
            {
                options = Options.Parse(args);
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine(ex.Message);
                Console.Error.WriteLine(
                    "usage: Ironwake.Console [seed] [--html <path>] [--seed <number>] [--sweep <count>]");
                return 1;
            }

            // The harness is the composition root: it is the only place that knows content
            // comes from JSON on disk. Core never learns that.
            var content = StarterPack.Load();

            IGameEngine engine = new RulesEngine(content);
            var state = SampleGame.Create(content, options.Seed);

            if (options.SweepMatches > 0)
            {
                var sweep = Sweep.Run(content, options.SweepMatches, options.Seed);
                Console.Write(Sweep.Report(sweep, content, options.Seed));
                return 0;
            }

            if (options.HtmlPath != null)
                return WriteViewer(engine, content, state, options);

            return PrintMatch(engine, state, content, options.Seed);
        }

        /// <summary>The original behaviour, unchanged.</summary>
        private static int PrintMatch(IGameEngine engine, GameState state, IContentPack content, ulong seed)
        {
            Console.WriteLine($"=== Ironwake stub match | seed {seed} | content {content.Version} ===\n");

            int guard = 0;
            while (state.Phase != PhaseKind.Complete && guard++ < 500)
            {
                var legal = engine.LegalActions(state, state.ActivePlayer);
                if (legal.Count == 0) break;

                var choice = MatchPolicy.Pick(legal);
                var check = engine.Validate(state, choice);
                if (!check.IsLegal)
                {
                    Console.WriteLine($"!! engine offered an illegal action: {check}");
                    break;
                }

                var result = engine.Execute(state, choice);
                foreach (var e in result.Events)
                    Console.WriteLine($"  [R{state.Round}] {e.Describe()}");

                state = result.NextState;
                if (result.IsTerminal) break;
            }

            Console.WriteLine("\n=== final ===");
            Console.WriteLine($"Round {state.Round}  |  A {state.ScoreA} - {state.ScoreB} B");
            foreach (var u in state.Units)
                Console.WriteLine($"  {u.Id} {u.Owner} @ {u.Position}  models {u.ModelsAlive}/{u.Models.Count}");
            Console.WriteLine($"\nRNG: {state.Rng}");
            return 0;
        }

        private static int WriteViewer(
            IGameEngine engine, IContentPack content, GameState state, Options options)
        {
            var recording = MatchRecorder.Record(engine, state, options.Seed);

            var fullPath = Path.GetFullPath(options.HtmlPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            HtmlWriter.Write(fullPath, recording, content);

            Console.WriteLine($"seed {options.Seed} | content {content.Version} | " +
                              $"{recording.Steps.Count} steps | " +
                              (recording.Completed ? "match completed" : "stopped at step guard"));
            Console.WriteLine(fullPath);
            return 0;
        }

        private sealed class Options
        {
            public ulong Seed = DefaultSeed;
            public string HtmlPath;

            /// <summary>Matches to play for statistics. Zero means "not a sweep".</summary>
            public int SweepMatches;

            /// <summary>
            /// Accepts the original bare positional seed as well as the flags, so the
            /// documented determinism check keeps working untouched.
            /// </summary>
            public static Options Parse(string[] args)
            {
                var options = new Options();

                for (int i = 0; i < args.Length; i++)
                {
                    switch (args[i])
                    {
                        case "--html":
                            options.HtmlPath = Next(args, ref i, "--html needs a file path.");
                            break;

                        case "--sweep":
                            var count = Next(args, ref i, "--sweep needs a match count.");
                            if (!int.TryParse(count, out options.SweepMatches) || options.SweepMatches < 1)
                                throw new ArgumentException($"'{count}' is not a valid match count.");
                            break;

                        case "--seed":
                            var raw = Next(args, ref i, "--seed needs a number.");
                            if (!ulong.TryParse(raw, out options.Seed))
                                throw new ArgumentException($"'{raw}' is not a valid seed.");
                            break;

                        default:
                            if (args[i].StartsWith("-", StringComparison.Ordinal))
                                throw new ArgumentException($"Unknown option '{args[i]}'.");

                            // Bare positional argument: the original seed form.
                            if (!ulong.TryParse(args[i], out options.Seed))
                                throw new ArgumentException($"'{args[i]}' is not a valid seed.");
                            break;
                    }
                }

                return options;
            }

            private static string Next(IReadOnlyList<string> args, ref int i, string message)
            {
                if (i + 1 >= args.Count) throw new ArgumentException(message);
                return args[++i];
            }
        }
    }
}
