using System.Collections.Generic;
using System.Linq;
using System.Text;
using Ironwake.Core;
using Xunit;

namespace Ironwake.Core.Tests
{
    /// <summary>
    /// Replay fidelity: a match rebuilt by re-executing its action log against a fresh state
    /// from the same seed must land in EXACTLY the state the live run reached.
    ///
    /// This is the structural check the project has been missing. Determinism tests compare
    /// two identical runs, which cannot catch a bug in what gets carried BETWEEN steps — the
    /// RNG checkpoint being taken before morale rolled its dice was exactly that shape, and
    /// two live runs agreed with each other perfectly while the stored position was wrong.
    /// Replaying from the log exercises the stored state instead of the in-memory Rng.
    /// </summary>
    public class ReplayTests
    {
        private static readonly IContentPack Content = TestContent.ForSampleGame();

        /// <summary>Everything about a state that a replay has to reproduce.</summary>
        private static string Fingerprint(GameState state)
        {
            var sb = new StringBuilder();
            sb.Append("round=").Append(state.Round)
              .Append(" phase=").Append(state.Phase)
              .Append(" active=").Append(state.ActivePlayer)
              .Append(" activeUnit=").Append(state.ActiveUnit)
              .Append(" score=").Append(state.ScoreA).Append(':').Append(state.ScoreB)
              .Append(" rng=").Append(state.Rng.Seed).Append('/').Append(state.Rng.Consumed)
              .AppendLine();

            foreach (var unit in state.Units.OrderBy(u => u.Id.Value))
            {
                sb.Append(unit.Id).Append(' ').Append(unit.Owner)
                  .Append(" @").Append(unit.Position)
                  .Append(" models=").Append(unit.ModelsAlive).Append('/').Append(unit.Models.Count)
                  .Append(" wounds=").Append(string.Join(",", unit.Models.Select(m => m.WoundsRemaining)))
                  .Append(" lost=").Append(unit.ModelsLostThisRound)
                  .Append(" activated=").Append(unit.HasActivated)
                  .Append(" actions=").Append(unit.ActionsRemaining)
                  .Append(" statuses=").Append(string.Join(",", unit.Statuses.OrderBy(s => (int)s)))
                  .AppendLine();
            }

            foreach (var objective in state.Objectives.OrderBy(o => o.Id.Value))
            {
                sb.Append(objective.Id).Append(" @").Append(objective.Position)
                  .Append(" worth=").Append(objective.PointValue)
                  .Append(" held=").Append(objective.ControlledBy?.ToString() ?? "-")
                  .AppendLine();
            }

            return sb.ToString();
        }

        private sealed class LiveRun
        {
            public GameState Final;
            public List<GameAction> Log = new List<GameAction>();
            public List<string> EventLog = new List<string>();
        }

        private static LiveRun PlayLive(ulong seed)
        {
            var engine = new StubEngine(Content);
            var state = SampleGame.Create(Content, seed);
            var run = new LiveRun();

            int guard = 0;
            while (state.Phase != PhaseKind.Complete && guard++ < 500)
            {
                var legal = engine.LegalActions(state, state.ActivePlayer);
                if (legal.Count == 0) break;

                var choice = MatchPolicy.Pick(legal);
                run.Log.Add(choice);

                var result = engine.Execute(state, choice);
                run.EventLog.AddRange(result.Events.Select(e => e.Describe()));

                state = result.NextState;
                if (result.IsTerminal) break;
            }

            run.Final = state;
            return run;
        }

        /// <summary>Re-executes a recorded log against a fresh state built from the same seed.</summary>
        private static (GameState Final, List<string> Events) Replay(ulong seed, List<GameAction> log)
        {
            var engine = new StubEngine(Content);
            var state = SampleGame.Create(Content, seed);
            var events = new List<string>();

            foreach (var action in log)
            {
                // A replayed action must still be legal — if it is not, the state diverged.
                var check = engine.Validate(state, action);
                Assert.True(check.IsLegal,
                    $"replayed {action.Kind} was refused: {check}. The rebuilt state has diverged.");

                var result = engine.Execute(state, action);
                events.AddRange(result.Events.Select(e => e.Describe()));
                state = result.NextState;
                if (result.IsTerminal) break;
            }

            return (state, events);
        }

        [Theory]
        [InlineData(777UL)]
        [InlineData(12345UL)]
        [InlineData(1UL)]
        [InlineData(99UL)]
        public void ReplayingTheActionLogReproducesTheStateExactly(ulong seed)
        {
            var live = PlayLive(seed);
            var (replayed, replayedEvents) = Replay(seed, live.Log);

            Assert.Equal(Fingerprint(live.Final), Fingerprint(replayed));
            Assert.Equal(live.EventLog, replayedEvents);
        }

        [Theory]
        [InlineData(777UL)]
        [InlineData(12345UL)]
        public void ReplayIsStableAcrossRepeatedRuns(ulong seed)
        {
            var live = PlayLive(seed);

            for (int i = 0; i < 3; i++)
            {
                var (replayed, _) = Replay(seed, live.Log);
                Assert.Equal(Fingerprint(live.Final), Fingerprint(replayed));
            }
        }

        [Fact]
        public void TheStoredRngPositionAccountsForEveryDieRolled()
        {
            // The specific failure the replay test exists to catch: if the checkpoint is taken
            // before some dice are rolled, the stored Consumed count lags what actually
            // happened, and a replay resuming from that state rolls the wrong numbers.
            var live = PlayLive(777UL);

            int rollsInEvents = 0;
            var engine = new StubEngine(Content);
            var state = SampleGame.Create(Content, 777UL);

            foreach (var action in live.Log)
            {
                var result = engine.Execute(state, action);

                int diceThisStep = result.Events.OfType<DiceRolledEvent>().Sum(e => e.Results.Length);
                rollsInEvents += diceThisStep;

                // Consumed must advance by exactly the dice this step reported.
                Assert.Equal(state.Rng.Consumed + diceThisStep, result.NextState.Rng.Consumed);

                state = result.NextState;
                if (result.IsTerminal) break;
            }

            Assert.True(rollsInEvents > 0, "no dice were rolled, so this proves nothing");
            Assert.Equal(rollsInEvents, state.Rng.Consumed);
        }

        [Fact]
        public void AMatchThatRunsToTheFinalRoundReplaysToo()
        {
            // Round-end work — scoring, morale, resets — is where cross-step state is most
            // likely to be dropped, so a match that survives to the end matters most here.
            var live = PlayLive(12345UL);
            Assert.True(live.Final.Round >= 2, "the match ended too early to exercise round end");

            var (replayed, _) = Replay(12345UL, live.Log);

            Assert.Equal(live.Final.Round, replayed.Round);
            Assert.Equal(live.Final.ScoreA, replayed.ScoreA);
            Assert.Equal(live.Final.ScoreB, replayed.ScoreB);
            Assert.Equal(Fingerprint(live.Final), Fingerprint(replayed));
        }
    }
}
