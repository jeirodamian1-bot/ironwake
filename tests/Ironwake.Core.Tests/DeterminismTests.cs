using System.Linq;
using System.Text;
using Ironwake.Core;
using Xunit;

namespace Ironwake.Core.Tests
{
    /// <summary>
    /// The determinism guarantee, pinned in-process so a regression fails `dotnet test`
    /// rather than waiting to be caught by diffing two console runs.
    /// </summary>
    public class DeterminismTests
    {
        /// <summary>
        /// Plays a whole match with a fixed action policy and returns a transcript of every
        /// event, plus the final state. Mirrors what Ironwake.Console prints.
        /// </summary>
        private static string PlayMatch(ulong seed)
        {
            var content = TestContent.ForSampleGame();
            IGameEngine engine = new StubEngine(content);
            var state = SampleGame.Create(content, seed);
            var log = new StringBuilder();

            int guard = 0;
            while (state.Phase != PhaseKind.Complete && guard++ < 500)
            {
                var legal = engine.LegalActions(state, state.ActivePlayer);
                if (legal.Count == 0) break;

                var result = engine.Execute(state, MatchPolicy.Pick(legal));
                foreach (var e in result.Events)
                    log.Append("[R").Append(state.Round).Append("] ").AppendLine(e.Describe());

                state = result.NextState;
                if (result.IsTerminal) break;
            }

            log.Append("final round ").Append(state.Round)
               .Append(" score ").Append(state.ScoreA).Append('-').Append(state.ScoreB)
               .AppendLine();
            foreach (var u in state.Units)
                log.Append(u.Id).Append(' ').Append(u.Owner).Append(' ').Append(u.Position)
                   .Append(' ').Append(u.ModelsAlive).Append('/').Append(u.Models.Count)
                   .AppendLine();
            log.Append("rng ").Append(state.Rng);

            return log.ToString();
        }

        [Theory]
        [InlineData(777UL)]
        [InlineData(12345UL)]
        [InlineData(1UL)]
        public void TheSameSeedPlaysTheSameMatch(ulong seed)
        {
            Assert.Equal(PlayMatch(seed), PlayMatch(seed));
        }

        [Theory]
        [InlineData(777UL)]
        [InlineData(12345UL)]
        [InlineData(1UL)]
        public void TheMatchActuallyRollsDiceAndFinishes(ulong seed)
        {
            // Guards the test above from passing vacuously. If the action policy ever stops
            // activating units, both players simply pass, the transcripts still match, and
            // the determinism assertion becomes worthless. These two must hold for it to mean
            // anything: the match reached a conclusion, and the RNG was actually consumed.
            var transcript = PlayMatch(seed);

            Assert.Contains("to-hit", transcript);
            Assert.Contains("Match over", transcript);
            Assert.DoesNotContain("rng seed:0 used:0", transcript);
        }

        [Fact]
        public void DifferentSeedsPlayDifferentMatches()
        {
            Assert.NotEqual(PlayMatch(1UL), PlayMatch(2UL));
        }

        [Fact]
        public void ExecutingTheSameActionOnTheSameStateGivesTheSameResult()
        {
            // Execute must be a pure function of (state, action) — no hidden RNG,
            // no clock, no ambient state carried between calls.
            var content = TestContent.ForSampleGame();
            IGameEngine engine = new StubEngine(content);
            var state = SampleGame.Create(content, 999UL);
            var unit = state.UnitsOf(PlayerId.A).First().Id;

            state = engine.Execute(state, new ActivateUnit(PlayerId.A, unit)).NextState;
            var target = state.UnitsOf(PlayerId.B).First().Id;

            // Reach shooting range, then fire the same shot twice from the identical state.
            var shot = new ShootAt(PlayerId.A, unit, target, "stub_weapon");
            var shooter = state.GetUnit(unit);
            if (!engine.Validate(state, shot).IsLegal)
            {
                var path = shooter.Position.LineTo(state.GetUnit(target).Position)
                                  .Take(5).ToList();
                state = engine.Execute(state, new MoveUnit(PlayerId.A, unit, path)).NextState;
            }

            Assert.True(engine.Validate(state, shot).IsLegal);

            var first = engine.Execute(state, shot);
            var second = engine.Execute(state, shot);

            Assert.Equal(
                first.Events.Select(e => e.Describe()),
                second.Events.Select(e => e.Describe()));
            Assert.Equal(first.NextState.Rng.Consumed, second.NextState.Rng.Consumed);
        }

        [Fact]
        public void BoardHexEnumerationOrderIsStable()
        {
            // AllHexes backs rendering and any board scan; if its order ever drifts,
            // anything that picks "the first matching hex" desyncs.
            var board = new BoardState(radius: 5);
            Assert.Equal(board.AllHexes().ToList(), board.AllHexes().ToList());
            Assert.Equal(board.AllHexes().ToList(), new BoardState(radius: 5).AllHexes().ToList());
        }
    }
}
