using System.Collections.Generic;
using System.Linq;
using Ironwake.Core;
using Xunit;

namespace Ironwake.Core.Tests
{
    /// <summary>
    /// The engine must not contradict itself: anything it offers or advertises as possible
    /// has to survive its own validation.
    ///
    /// This is the class that would have caught the LineTo defect. LegalActions used to build
    /// paths with a straight line that ignores terrain and occupancy, so it handed the client
    /// moves that ValidateMove then refused.
    /// </summary>
    public class EngineMovementAgreementTests
    {
        private static readonly IContentPack Content = TestContent.ForSampleGame();

        /// <summary>Puts <paramref name="unit"/> mid-activation so movement can be validated.</summary>
        private static GameState Activated(GameState state, UnitState unit) =>
            state.With(activePlayer: unit.Owner)
                 .WithUnit(unit.With(actionsRemaining: 2))
                 .With(activeUnit: unit.Id);

        [Fact]
        public void EveryReachableHexIsReallyReachable_ForAllSixSampleUnits()
        {
            // THE INVARIANT. For every hex ReachableHexes advertises, walking FindPath's route
            // to it must validate as Legal. If these two ever disagree the client highlights
            // a hex, the player taps it, and the engine refuses the move.
            var engine = new RulesEngine(Content);
            var board = SampleGame.Create(Content, 5UL);

            int hexesChecked = 0;

            foreach (var unit in board.Units)
            {
                var state = Activated(board, unit);
                int allowance = Content.GetUnit(unit.DefinitionId).Stats.MoveInHexes;

                var reachable = engine.ReachableHexes(state, unit.Id);
                Assert.NotEmpty(reachable);

                foreach (var hex in reachable.Keys.OrderBy(h => h.Q).ThenBy(h => h.R))
                {
                    var path = Movement.FindPath(state, unit.Id, hex, allowance);

                    Assert.True(path.Count >= 2,
                        $"{unit.Id} ({unit.DefinitionId}): {hex} is reachable but has no path");

                    var result = engine.Validate(state, new MoveUnit(unit.Owner, unit.Id, path));

                    Assert.True(result.IsLegal,
                        $"{unit.Id} ({unit.DefinitionId}) advertised {hex} as reachable, " +
                        $"but moving there was refused: {result}");

                    hexesChecked++;
                }
            }

            // Six units on a radius-5 board with terrain: guards against the loop above
            // silently checking nothing.
            Assert.True(hexesChecked > 100, $"only {hexesChecked} hexes were checked");
        }

        [Fact]
        public void ReachableHexesAgreesWithWhatLegalActionsOffers()
        {
            var engine = new RulesEngine(Content);
            var board = SampleGame.Create(Content, 5UL);

            foreach (var unit in board.Units)
            {
                var state = Activated(board, unit);

                var reachable = engine.ReachableHexes(state, unit.Id).Keys
                    .OrderBy(h => h.Q).ThenBy(h => h.R).ToList();

                var offeredDestinations = engine.LegalActions(state, unit.Owner)
                    .OfType<MoveUnit>()
                    .Select(m => m.Path[m.Path.Count - 1])
                    .OrderBy(h => h.Q).ThenBy(h => h.R)
                    .ToList();

                Assert.Equal(reachable, offeredDestinations);
            }
        }

        [Fact]
        public void EveryActionLegalActionsOffersSurvivesValidate_ThroughAWholeMatch()
        {
            // Checked for BOTH players at every step, not just the one to act, so a bug in
            // the idle player's action list cannot hide.
            var engine = new RulesEngine(Content);
            var state = SampleGame.Create(Content, 777UL);

            int actionsChecked = 0;
            int steps = 0;
            int guard = 0;

            while (state.Phase != PhaseKind.Complete && guard++ < 500)
            {
                foreach (var player in new[] { PlayerId.A, PlayerId.B })
                {
                    foreach (var action in engine.LegalActions(state, player))
                    {
                        var result = engine.Validate(state, action);
                        Assert.True(result.IsLegal,
                            $"LegalActions offered {action} to {player} but Validate refused it: {result}");
                        actionsChecked++;
                    }
                }

                var legal = engine.LegalActions(state, state.ActivePlayer);
                if (legal.Count == 0) break;

                var outcome = engine.Execute(state, MatchPolicy.Pick(legal));
                state = outcome.NextState;
                steps++;
                if (outcome.IsTerminal) break;
            }

            Assert.True(steps > 5, $"the match only advanced {steps} steps");
            Assert.True(actionsChecked > 500, $"only {actionsChecked} actions were checked");
        }

        [Fact]
        public void EveryChargeAndFightLegalActionsOffersSurvivesValidate_ThroughAWholeMatch()
        {
            // THE INVARIANT for melee. Checked for both players at every step, so an offer
            // the engine would then refuse cannot hide in the idle player's list.
            var engine = new RulesEngine(Content);
            var state = SampleGame.Create(Content, 777UL);

            int chargesChecked = 0, fightsChecked = 0;
            int guard = 0;

            while (state.Phase != PhaseKind.Complete && guard++ < 500)
            {
                foreach (var player in new[] { PlayerId.A, PlayerId.B })
                {
                    foreach (var action in engine.LegalActions(state, player))
                    {
                        if (!(action is ChargeAt) && !(action is FightUnit)) continue;

                        var result = engine.Validate(state, action);
                        Assert.True(result.IsLegal,
                            $"LegalActions offered {action.Kind} to {player} but Validate refused it: {result}");

                        if (action is ChargeAt) chargesChecked++; else fightsChecked++;
                    }
                }

                var legal = engine.LegalActions(state, state.ActivePlayer);
                if (legal.Count == 0) break;

                var outcome = engine.Execute(state, MatchPolicy.Pick(legal));
                state = outcome.NextState;
                if (outcome.IsTerminal) break;
            }

            Assert.True(chargesChecked > 20, $"only {chargesChecked} charges were checked");
            Assert.True(fightsChecked > 0, $"only {fightsChecked} fights were checked");
        }

        [Fact]
        public void AChargeOfferedIsAChargeThatCanActuallyBeMade()
        {
            // Every offered charge must have a real approach, and taking it must land the
            // unit adjacent to its target.
            var engine = new RulesEngine(Content);
            var board = SampleGame.Create(Content, 5UL);

            int checkedCharges = 0;

            foreach (var unit in board.Units)
            {
                var state = Activated(board, unit);

                foreach (var charge in engine.LegalActions(state, unit.Owner).OfType<ChargeAt>())
                {
                    int allowance = Content.GetUnit(unit.DefinitionId).Stats.MoveInHexes;
                    var approach = Melee.FindApproach(state, charge.Unit, charge.Target, allowance);

                    Assert.True(approach.IsPossible, $"{charge.Unit} was offered a charge with no approach");

                    var after = engine.Execute(state, charge).NextState;
                    Assert.Equal(1,
                        after.GetUnit(charge.Unit).Position.DistanceTo(after.GetUnit(charge.Target).Position));

                    checkedCharges++;
                }
            }

            Assert.True(checkedCharges > 0, "no charges were offered at all");
        }

        [Fact]
        public void LegalActionsNeverOffersAPathThroughTerrainOrUnits()
        {
            var engine = new RulesEngine(Content);
            var board = SampleGame.Create(Content, 5UL);

            foreach (var unit in board.Units)
            {
                var state = Activated(board, unit);

                foreach (var move in engine.LegalActions(state, unit.Owner).OfType<MoveUnit>())
                {
                    Assert.Equal(unit.Position, move.Path[0]);

                    for (int i = 1; i < move.Path.Count; i++)
                    {
                        Assert.Equal(1, move.Path[i - 1].DistanceTo(move.Path[i]));
                        Assert.Equal(HexBlock.None, Movement.BlockingReason(state, unit.Id, move.Path[i]));
                    }
                }
            }
        }

        [Fact]
        public void LegalActionsOrderIsStableAcrossCalls()
        {
            // The console harness and any AI pick from this list positionally, so its order
            // has to be reproducible — it is built from a Dictionary and must be sorted.
            var engine = new RulesEngine(Content);
            var board = SampleGame.Create(Content, 5UL);
            var state = Activated(board, board.Units[0]);

            var first = engine.LegalActions(state, state.ActivePlayer).Select(Describe).ToList();

            for (int i = 0; i < 25; i++)
                Assert.Equal(first, engine.LegalActions(state, state.ActivePlayer).Select(Describe).ToList());
        }

        [Fact]
        public void ReachableHexesIsEmptyForAMissingOrDestroyedUnit()
        {
            var engine = new RulesEngine(Content);
            var board = SampleGame.Create(Content, 5UL);

            Assert.Empty(engine.ReachableHexes(board, new UnitId(999)));

            var casualty = board.Units[0];
            var wiped = board.WithUnit(casualty.With(
                models: casualty.Models.Select(_ => new ModelState(0, isSlain: true)).ToList()));

            Assert.Empty(engine.ReachableHexes(wiped, casualty.Id));
        }

        [Fact]
        public void AFasterUnitReachesMoreHexesThanASlowerOneFromTheSamePlace()
        {
            var engine = new RulesEngine(Content);
            var board = SampleGame.Create(Content, 5UL);

            // cinderkin_raider moves 60 tenths (6 hexes); ashguard_lineholder moves 40 (4).
            var fast = board.Units.First(u => u.DefinitionId == "cinderkin_raider");
            var slow = board.Units.First(u => u.DefinitionId == "ashguard_lineholder");

            var spot = new Hex(0, -1);
            var fastFrom = engine.ReachableHexes(Activated(board.WithUnit(fast.With(position: spot)), fast.With(position: spot)), fast.Id);
            var slowFrom = engine.ReachableHexes(Activated(board.WithUnit(slow.With(position: spot)), slow.With(position: spot)), slow.Id);

            Assert.True(fastFrom.Count > slowFrom.Count,
                $"raider reached {fastFrom.Count}, lineholder reached {slowFrom.Count}");
        }

        private static string Describe(GameAction action) =>
            action is MoveUnit m
                ? "Move:" + string.Join(">", m.Path)
                : action.Kind + ":" + action;
    }
}
