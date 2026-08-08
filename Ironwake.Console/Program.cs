using System;
using System.Linq;
using Ironwake.Content;
using Ironwake.Core;

namespace Ironwake.ConsoleHarness
{
    /// <summary>
    /// Headless proof that the engine loop works with no Unity involved.
    /// Run:  dotnet run --project Ironwake.Console
    ///
    /// Plays a full random match and prints the combat log. Run it twice with the same
    /// seed and the output must be byte-identical — that is the determinism check.
    /// </summary>
    internal static class Program
    {
        private static void Main(string[] args)
        {
            ulong seed = args.Length > 0 && ulong.TryParse(args[0], out var s) ? s : 12345UL;

            // The harness is the composition root: it is the only place that knows content
            // comes from JSON on disk. Core never learns that.
            var content = StarterPack.Load();

            IGameEngine engine = new StubEngine(content);
            var state = SampleGame.Create(content, seed);

            Console.WriteLine($"=== Ironwake stub match | seed {seed} | content {content.Version} ===\n");

            // Deterministic action picker: always take the last legal action.
            // Not clever, but repeatable — which is the point.
            int guard = 0;
            while (state.Phase != PhaseKind.Complete && guard++ < 500)
            {
                var legal = engine.LegalActions(state, state.ActivePlayer);
                if (legal.Count == 0) break;

                var choice = PickAction(legal);
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
        }

        /// <summary>Prefer shooting, then moving, then ending. Deterministic.</summary>
        private static GameAction PickAction(System.Collections.Generic.IReadOnlyList<GameAction> legal)
        {
            var shoot = legal.FirstOrDefault(a => a is ShootAt);
            if (shoot != null) return shoot;

            var activate = legal.FirstOrDefault(a => a is ActivateUnit);
            if (activate != null) return activate;

            var move = legal.OfType<MoveUnit>().OrderByDescending(m => m.Path.Count).FirstOrDefault();
            if (move != null) return move;

            return legal[legal.Count - 1];
        }
    }
}
