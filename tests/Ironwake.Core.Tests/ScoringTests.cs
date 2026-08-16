using System.Collections.Generic;
using System.Linq;
using Ironwake.Core;
using Xunit;

namespace Ironwake.Core.Tests
{
    /// <summary>
    /// Objective control, round-end scoring, and how a match ends.
    /// </summary>
    public class ScoringTests
    {
        private static readonly ObjectiveId Obj = new ObjectiveId(1);

        private static IContentPack Pack()
        {
            var gun = new WeaponDefinition("gun", "Gun", 60, 2, 4, 0, 1);
            var unit = new UnitDefinition("trooper", "f", "Trooper", 50, 5,
                new Statline(40, 4, 4, 4, 4, 1, 6), new[] { "gun" });
            return new TestContentPack(
                new[] { unit }, new[] { gun }, new[] { new FactionDefinition("f", "F") });
        }

        private static UnitState Trooper(int id, PlayerId owner, Hex at, int models,
                                         params StatusKind[] statuses) =>
            new UnitState(new UnitId(id), owner, "trooper", at, 0,
                Enumerable.Range(0, models).Select(_ => new ModelState(1)).ToList(),
                statuses.ToList(), hasActivated: true, actionsRemaining: 0);

        private static GameState Field(Hex objectiveAt, int pointValue, params UnitState[] units) =>
            new GameState(
                round: 1, phase: PhaseKind.Activation,
                activePlayer: PlayerId.A, activeUnit: UnitId.None,
                board: new BoardState(10),
                units: units.ToList(),
                objectives: new List<ObjectiveState> { new ObjectiveState(Obj, objectiveAt, pointValue) },
                scoreA: 0, scoreB: 0, rng: new RngState(1UL), contentVersion: "test");

        private static ObjectiveState TheObjective(GameState s) => s.Objectives.First();

        // ---- control ---------------------------------------------------------------

        [Fact]
        public void ControlNeedsStrictlyMoreModels()
        {
            var state = Field(Hex.Zero, 2,
                Trooper(1, PlayerId.A, new Hex(1, 0), models: 3),
                Trooper(2, PlayerId.B, new Hex(-1, 0), models: 2));

            Assert.Equal(PlayerId.A, Scoring.ControllerOf(state, TheObjective(state)));
            Assert.Equal(3, Scoring.ContributionOf(state, TheObjective(state), PlayerId.A));
            Assert.Equal(2, Scoring.ContributionOf(state, TheObjective(state), PlayerId.B));
        }

        [Fact]
        public void EqualModelsAreContestedAndScoreForNobody()
        {
            var state = Field(Hex.Zero, 2,
                Trooper(1, PlayerId.A, new Hex(1, 0), models: 3),
                Trooper(2, PlayerId.B, new Hex(-1, 0), models: 3));

            Assert.Null(Scoring.ControllerOf(state, TheObjective(state)));

            var scored = Scoring.ScoreRound(state, new List<GameEvent>());
            Assert.Equal(0, scored.ScoreA);
            Assert.Equal(0, scored.ScoreB);
        }

        [Fact]
        public void ModelsOutsideTheRadiusDoNotCount()
        {
            var far = new Hex(Scoring.ControlRadiusHexes + 1, 0);
            var near = new Hex(Scoring.ControlRadiusHexes, 0);

            var state = Field(Hex.Zero, 2,
                Trooper(1, PlayerId.A, far, models: 9),
                Trooper(2, PlayerId.B, near, models: 1));

            Assert.Equal(0, Scoring.ContributionOf(state, TheObjective(state), PlayerId.A));
            Assert.Equal(PlayerId.B, Scoring.ControllerOf(state, TheObjective(state)));
        }

        [Fact]
        public void ShakenModelsDoNotCountTowardControl()
        {
            // A ruling, and it is what gives morale teeth beyond the to-hit penalty: the same
            // models in the same hexes hand the objective over the moment they break.
            var steady = Field(Hex.Zero, 2,
                Trooper(1, PlayerId.A, new Hex(1, 0), models: 3),
                Trooper(2, PlayerId.B, new Hex(-1, 0), models: 2));

            Assert.Equal(PlayerId.A, Scoring.ControllerOf(steady, TheObjective(steady)));

            var broken = steady.WithUnit(steady.GetUnit(new UnitId(1)).WithStatus(StatusKind.Shaken));

            Assert.Equal(0, Scoring.ContributionOf(broken, TheObjective(broken), PlayerId.A));
            Assert.Equal(PlayerId.B, Scoring.ControllerOf(broken, TheObjective(broken)));
        }

        [Fact]
        public void EngagedModelsDoCountTowardControl()
        {
            // Standing on the objective swinging at somebody is what holding it looks like.
            var state = Field(Hex.Zero, 2,
                Trooper(1, PlayerId.A, new Hex(1, 0), 3, StatusKind.Engaged),
                Trooper(2, PlayerId.B, new Hex(2, 0), 2, StatusKind.Engaged));

            Assert.Equal(3, Scoring.ContributionOf(state, TheObjective(state), PlayerId.A));
            Assert.Equal(PlayerId.A, Scoring.ControllerOf(state, TheObjective(state)));
        }

        [Fact]
        public void DeadModelsDoNotCount()
        {
            var state = Field(Hex.Zero, 2,
                Trooper(1, PlayerId.A, new Hex(1, 0), models: 3),
                Trooper(2, PlayerId.B, new Hex(-1, 0), models: 1));

            var wiped = state.WithUnit(state.GetUnit(new UnitId(1)).With(
                models: Enumerable.Range(0, 3).Select(_ => new ModelState(0, isSlain: true)).ToList()));

            Assert.Equal(0, Scoring.ContributionOf(wiped, TheObjective(wiped), PlayerId.A));
        }

        // ---- scoring ----------------------------------------------------------------

        [Fact]
        public void ScoringAwardsThePointValueToTheHolder()
        {
            var state = Field(Hex.Zero, 3, Trooper(1, PlayerId.B, new Hex(1, 0), models: 2));

            var scored = Scoring.ScoreRound(state, new List<GameEvent>());

            Assert.Equal(0, scored.ScoreA);
            Assert.Equal(3, scored.ScoreB);
            Assert.Equal(PlayerId.B, TheObjective(scored).ControlledBy);
        }

        [Fact]
        public void ScoringHappensAtRoundEndNotPerAction()
        {
            // Walking onto an objective mid-round must not pay out until the round closes.
            var engine = new StubEngine(Pack());
            var state = Field(Hex.Zero, 2, Trooper(1, PlayerId.A, new Hex(3, 0), models: 5))
                .With(activePlayer: PlayerId.A, activeUnit: new UnitId(1));
            state = state.WithUnit(state.GetUnit(new UnitId(1)).With(
                hasActivated: false, actionsRemaining: 2));

            var path = Movement.FindPath(state, new UnitId(1), new Hex(1, 0), 4);
            var after = engine.Execute(state, new MoveUnit(PlayerId.A, new UnitId(1), path)).NextState;

            // Control is already projected, but nothing has been paid.
            Assert.Equal(PlayerId.A, engine.ProjectedControl(after)[Obj]);
            Assert.Equal(0, after.ScoreA);
            Assert.Null(after.Objectives.First().ControlledBy);
        }

        [Fact]
        public void ControlChangesAreAnnouncedWhenTheyFlip()
        {
            var events = new List<GameEvent>();
            var state = Field(Hex.Zero, 2, Trooper(1, PlayerId.A, new Hex(1, 0), models: 2));

            Scoring.ScoreRound(state, events);

            var changed = Assert.Single(events.OfType<ObjectiveControlChangedEvent>());
            Assert.Null(changed.From);
            Assert.Equal(PlayerId.A, changed.To);

            var scored = Assert.Single(events.OfType<ObjectiveScoredEvent>());
            Assert.Equal(PlayerId.A, scored.Player);
            Assert.Equal(2, scored.Points);
        }

        [Fact]
        public void NoControlChangeEventWhenTheHolderIsUnchanged()
        {
            var state = Field(Hex.Zero, 2, Trooper(1, PlayerId.A, new Hex(1, 0), models: 2));
            var once = Scoring.ScoreRound(state, new List<GameEvent>());

            var events = new List<GameEvent>();
            Scoring.ScoreRound(once, events);

            Assert.Empty(events.OfType<ObjectiveControlChangedEvent>());
            Assert.Single(events.OfType<ObjectiveScoredEvent>());   // still scores every round
        }

        // ---- the invariant ------------------------------------------------------------

        [Fact]
        public void ProjectedControlMatchesWhatScoringActuallyAwards()
        {
            // THE INVARIANT: what the client is shown mid-round must be what pays out.
            var positions = new[] { new Hex(1, 0), new Hex(-1, 0), new Hex(2, -1), new Hex(5, 0) };
            var engine = new StubEngine(Pack());

            foreach (var a in positions)
            {
                foreach (var b in positions)
                {
                    foreach (int aModels in new[] { 1, 2, 3 })
                    {
                        var state = Field(Hex.Zero, 2,
                            Trooper(1, PlayerId.A, a, aModels),
                            Trooper(2, PlayerId.B, b, 2));

                        var projected = engine.ProjectedControl(state)[Obj];
                        var scored = Scoring.ScoreRound(state, new List<GameEvent>());

                        Assert.Equal(projected, scored.Objectives.First().ControlledBy);

                        int awardedToA = scored.ScoreA - state.ScoreA;
                        int awardedToB = scored.ScoreB - state.ScoreB;

                        if (projected == PlayerId.A) { Assert.Equal(2, awardedToA); Assert.Equal(0, awardedToB); }
                        else if (projected == PlayerId.B) { Assert.Equal(0, awardedToA); Assert.Equal(2, awardedToB); }
                        else { Assert.Equal(0, awardedToA); Assert.Equal(0, awardedToB); }
                    }
                }
            }
        }

        // ---- win conditions --------------------------------------------------------------

        [Fact]
        public void AWipedOutSideLosesImmediatelyWhateverTheScore()
        {
            var state = Field(Hex.Zero, 2,
                    Trooper(1, PlayerId.A, new Hex(1, 0), models: 1),
                    Trooper(2, PlayerId.B, new Hex(-1, 0), models: 1))
                .With(scoreA: 0, scoreB: 11);

            var wiped = state.WithUnit(state.GetUnit(new UnitId(2)).With(
                models: new List<ModelState> { new ModelState(0, isSlain: true) }));

            Assert.True(Scoring.IsMatchOver(wiped, atRoundEnd: false, out var winner));
            Assert.Equal(PlayerId.A, winner);
        }

        [Theory]
        [InlineData(12)]
        [InlineData(13)]
        [InlineData(30)]
        public void ReachingTheThresholdWinsImmediately(int score)
        {
            var state = Field(Hex.Zero, 2,
                    Trooper(1, PlayerId.A, new Hex(1, 0), models: 1),
                    Trooper(2, PlayerId.B, new Hex(-1, 0), models: 1))
                .With(round: 2, scoreA: score);

            Assert.True(Scoring.IsMatchOver(state, atRoundEnd: true, out var winner));
            Assert.Equal(PlayerId.A, winner);
        }

        [Fact]
        public void BelowTheThresholdMidMatchIsNotOver()
        {
            var state = Field(Hex.Zero, 2,
                    Trooper(1, PlayerId.A, new Hex(1, 0), models: 1),
                    Trooper(2, PlayerId.B, new Hex(-1, 0), models: 1))
                .With(round: 2, scoreA: Scoring.PointsToWin - 1);

            Assert.False(Scoring.IsMatchOver(state, atRoundEnd: true, out _));
        }

        [Fact]
        public void TheFinalRoundIsDecidedOnScore()
        {
            var state = Field(Hex.Zero, 2,
                    Trooper(1, PlayerId.A, new Hex(1, 0), models: 1),
                    Trooper(2, PlayerId.B, new Hex(-1, 0), models: 1))
                .With(round: Scoring.FinalRound, scoreA: 4, scoreB: 6);

            Assert.True(Scoring.IsMatchOver(state, atRoundEnd: true, out var winner));
            Assert.Equal(PlayerId.B, winner);
        }

        [Fact]
        public void AnEqualScoreAtTheEndIsADraw()
        {
            // A draw is a real outcome, not a failure to decide.
            var state = Field(Hex.Zero, 2,
                    Trooper(1, PlayerId.A, new Hex(1, 0), models: 1),
                    Trooper(2, PlayerId.B, new Hex(-1, 0), models: 1))
                .With(round: Scoring.FinalRound, scoreA: 5, scoreB: 5);

            Assert.True(Scoring.IsMatchOver(state, atRoundEnd: true, out var winner));
            Assert.Null(winner);

            Assert.Equal("Match over. Draw.", new MatchEndedEvent(winner).Describe());
        }

        [Fact]
        public void AnnihilationOutranksThePointsThreshold()
        {
            // Order matters: being wiped out loses even from 20 points ahead.
            var state = Field(Hex.Zero, 2,
                    Trooper(1, PlayerId.A, new Hex(1, 0), models: 1),
                    Trooper(2, PlayerId.B, new Hex(-1, 0), models: 1))
                .With(scoreA: 20);

            var wiped = state.WithUnit(state.GetUnit(new UnitId(1)).With(
                models: new List<ModelState> { new ModelState(0, isSlain: true) }));

            Assert.True(Scoring.IsMatchOver(wiped, atRoundEnd: true, out var winner));
            Assert.Equal(PlayerId.B, winner);
        }
    }
}
