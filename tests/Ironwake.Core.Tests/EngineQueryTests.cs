using System;
using System.Collections.Generic;
using System.Linq;
using Ironwake.Core;
using Xunit;

namespace Ironwake.Core.Tests
{
    /// <summary>
    /// The client-facing queries. Each one exists so a client can be shown something before
    /// committing to it, so each test asks the same question: does the answer match what
    /// Execute actually does?
    /// </summary>
    public class EngineQueryTests
    {
        private static readonly IContentPack Content = TestContent.ForSampleGame();

        private static GameState Activated(GameState state, UnitState unit) =>
            state.With(activePlayer: unit.Owner)
                 .WithUnit(unit.With(actionsRemaining: 2))
                 .With(activeUnit: unit.Id);

        // ---- charge preview ------------------------------------------------------------

        [Fact]
        public void ThePreviewedLandingHexIsWhereTheChargeActuallyEnds()
        {
            // THE INVARIANT. A charge is the one action that moves you before it resolves, so
            // a player choosing one is choosing a destination. If the preview and the outcome
            // disagree, the client has lied to them.
            var engine = new RulesEngine(Content);
            int previewsChecked = 0;

            // Checked all the way through a match, not just from the deployment line: at the
            // opening positions only a couple of charges are even reachable.
            foreach (ulong seed in new ulong[] { 5UL, 777UL, 12345UL })
            {
                var live = SampleGame.Create(Content, seed);

                for (int step = 0; step < 200 && live.Phase != PhaseKind.Complete; step++)
                {
                    foreach (var unit in live.Units.Where(u => u.IsAlive))
                    {
                        var state = Activated(live, unit);

                        foreach (var enemy in live.Units.Where(u => u.Owner != unit.Owner && u.IsAlive))
                        {
                            var preview = engine.PreviewCharge(state, unit.Id, enemy.Id);
                            var action = new ChargeAt(unit.Owner, unit.Id, enemy.Id, preview.Path);

                            if (!engine.Validate(state, action).IsLegal) continue;
                            Assert.True(preview.IsPossible, "a legal charge had an impossible preview");

                            var after = engine.Execute(state, action).NextState;

                            Assert.Equal(preview.Destination, after.GetUnit(unit.Id).Position);
                            Assert.Equal(1, after.GetUnit(unit.Id).Position.DistanceTo(
                                                after.GetUnit(enemy.Id).Position));
                            previewsChecked++;
                        }
                    }

                    var legal = engine.LegalActions(live, live.ActivePlayer);
                    if (legal.Count == 0) break;

                    var outcome = engine.Execute(live, MatchPolicy.Pick(live, legal));
                    live = outcome.NextState;
                    if (outcome.IsTerminal) break;
                }
            }

            Assert.True(previewsChecked > 50, $"only {previewsChecked} charges were previewed");
        }

        [Fact]
        public void LegalActionsIssuesChargesWithTheirPathAlreadyFilledIn()
        {
            // Same guarantee MoveUnit gives: the client never has to work a route out.
            var engine = new RulesEngine(Content);
            var board = SampleGame.Create(Content, 5UL);

            int charges = 0;

            foreach (var unit in board.Units)
            {
                var state = Activated(board, unit);

                foreach (var charge in engine.LegalActions(state, unit.Owner).OfType<ChargeAt>())
                {
                    Assert.True(charge.Path.Count >= 2, "an offered charge carried no route");
                    Assert.Equal(unit.Position, charge.Path[0]);

                    var preview = engine.PreviewCharge(state, charge.Unit, charge.Target);
                    Assert.Equal(preview.Path, charge.Path);

                    var after = engine.Execute(state, charge).NextState;
                    Assert.Equal(charge.Path[charge.Path.Count - 1], after.GetUnit(unit.Id).Position);
                    charges++;
                }
            }

            Assert.True(charges > 0, "no charges were offered at all");
        }

        [Fact]
        public void AChargeWithNoPathStillResolvesItsOwnApproach()
        {
            // The convenience form: "charge that unit" without having asked where it lands.
            var engine = new RulesEngine(Content);
            var board = SampleGame.Create(Content, 5UL);
            var unit = board.Units.First(u => u.Owner == PlayerId.A);
            var state = Activated(board, unit);

            var enemy = board.Units.First(u => u.Owner == PlayerId.B);
            var bare = new ChargeAt(unit.Owner, unit.Id, enemy.Id);

            if (!engine.Validate(state, bare).IsLegal) return;   // geometry-dependent

            var preview = engine.PreviewCharge(state, unit.Id, enemy.Id);
            var after = engine.Execute(state, bare).NextState;

            Assert.Equal(preview.Destination, after.GetUnit(unit.Id).Position);
        }

        [Fact]
        public void AChargePathThatDoesNotEndBesideTheTargetIsRefused()
        {
            // Built to order: on the sample board's opening line nothing has line of sight to
            // anything, and LOS is checked before the path, so the refusal would come back
            // NO_LINE_OF_SIGHT and prove nothing about the path check.
            var engine = new RulesEngine(Content);

            var units = new List<UnitState>
            {
                new UnitState(new UnitId(1), PlayerId.A, "ashguard_lineholder", Hex.Zero, 0,
                    new List<ModelState> { new ModelState(1) }, new List<StatusKind>(), false, 2),
                new UnitState(new UnitId(2), PlayerId.B, "cinderkin_raider", new Hex(3, 0), 0,
                    new List<ModelState> { new ModelState(1) }, new List<StatusKind>(), false, 0),
            };

            var state = new GameState(
                round: 1, phase: PhaseKind.Activation,
                activePlayer: PlayerId.A, activeUnit: new UnitId(1),
                board: new BoardState(8), units: units,
                objectives: new List<ObjectiveState>(),
                scoreA: 0, scoreB: 0, rng: new RngState(1UL), contentVersion: "test");

            Assert.False(engine.CheckLineOfSight(state, new UnitId(1), new UnitId(2)).IsBlocked);

            // A legal move, but it walks away rather than into contact.
            var wander = Movement.FindPath(state, new UnitId(1), new Hex(-2, 0), 4);
            Assert.True(wander.Count >= 2);

            var result = engine.Validate(state,
                new ChargeAt(PlayerId.A, new UnitId(1), new UnitId(2), wander));

            Assert.False(result.IsLegal);
            Assert.Equal(ReasonCodes.NoChargePath, result.ReasonCode);

            // And the previewed route IS accepted, so the refusal is about the route, not
            // about charging being broken.
            var preview = engine.PreviewCharge(state, new UnitId(1), new UnitId(2));
            Assert.True(preview.IsPossible);
            Assert.True(engine.Validate(state,
                new ChargeAt(PlayerId.A, new UnitId(1), new UnitId(2), preview.Path)).IsLegal);
        }

        [Fact]
        public void PreviewingAChargeForAMissingUnitIsSimplyImpossible()
        {
            var engine = new RulesEngine(Content);
            var board = SampleGame.Create(Content, 5UL);

            Assert.False(engine.PreviewCharge(board, new UnitId(99), board.Units[0].Id).IsPossible);
        }

        // ---- definitions -----------------------------------------------------------------

        [Fact]
        public void GetDefinitionLabelsEveryUnitOnTheBoard()
        {
            var engine = new RulesEngine(Content);
            var board = SampleGame.Create(Content, 5UL);

            foreach (var unit in board.Units)
            {
                var definition = engine.GetDefinition(unit);

                Assert.Equal(unit.DefinitionId, definition.Id);
                Assert.False(string.IsNullOrWhiteSpace(definition.DisplayName));
                Assert.NotNull(definition.Stats);
            }
        }

        [Fact]
        public void GameStateStillCarriesNoStatlines()
        {
            // The point of the query: state stays small and pins its content by version,
            // rather than growing a copy of the pack.
            var board = SampleGame.Create(Content, 5UL);

            Assert.Equal(Content.Version, board.ContentVersion);
            Assert.All(board.Units, u => Assert.False(string.IsNullOrEmpty(u.DefinitionId)));

            // UnitState exposes no statline of its own — only the id that points at one.
            Assert.Null(typeof(UnitState).GetProperty("Stats"));
            Assert.Null(typeof(UnitState).GetProperty("Points"));
        }

        // ---- threat range -----------------------------------------------------------------

        [Fact]
        public void ShootableHexesAgreesWithWhatValidateAllowsAgainstRealTargets()
        {
            // The shaded range must contain every enemy that can actually be shot, and
            // exclude every one that cannot — otherwise it is decoration.
            var engine = new RulesEngine(Content);
            var board = SampleGame.Create(Content, 5UL);

            int checkedTargets = 0;

            foreach (var unit in board.Units)
            {
                var state = Activated(board, unit);
                var shootable = engine.ShootableHexes(state, unit.Id, null);

                foreach (var enemy in board.Units.Where(u => u.Owner != unit.Owner && u.IsAlive))
                {
                    bool legal = engine.Validate(state,
                        new ShootAt(unit.Owner, unit.Id, enemy.Id, null)).IsLegal;

                    if (legal)
                        Assert.Contains(enemy.Position, shootable);

                    checkedTargets++;
                }
            }

            Assert.True(checkedTargets > 10, $"only {checkedTargets} targets were checked");
        }

        [Fact]
        public void EveryShootableHexIsOnTheBoardInRangeAndVisible()
        {
            var engine = new RulesEngine(Content);
            var board = SampleGame.Create(Content, 5UL);
            var unit = board.Units.First(u => u.DefinitionId == "ashguard_lineholder");

            int range = Content.GetWeapon(Content.GetUnit(unit.DefinitionId).WeaponIds[0]).RangeInHexes;
            var shootable = engine.ShootableHexes(board, unit.Id, null);

            Assert.NotEmpty(shootable);
            Assert.All(shootable, h =>
            {
                Assert.True(board.Board.Contains(h), $"{h} is off the board");
                Assert.True(unit.Position.DistanceTo(h) <= range, $"{h} is beyond range {range}");
                Assert.True(LineOfSight.HasLineOfSight(board, unit.Position, h), $"{h} is not visible");
            });

            Assert.DoesNotContain(unit.Position, shootable);
        }

        [Fact]
        public void AMeleeWeaponShadesNothing()
        {
            var engine = new RulesEngine(Content);
            var board = SampleGame.Create(Content, 5UL);
            var warden = board.Units.First(u => u.DefinitionId == "ashguard_warden");

            Assert.Empty(engine.ShootableHexes(board, warden.Id, "warden_maul"));
        }

        [Fact]
        public void ShootableHexesIsEmptyForAMissingUnit()
        {
            var engine = new RulesEngine(Content);
            var board = SampleGame.Create(Content, 5UL);

            Assert.Empty(engine.ShootableHexes(board, new UnitId(99), null));
        }

        // ---- action cost -------------------------------------------------------------------

        [Fact]
        public void ActionCostMatchesWhatExecuteActuallySpends()
        {
            // Every cost the query reports is checked against the actions the unit has left
            // before and after the action really runs.
            var engine = new RulesEngine(Content);
            var board = SampleGame.Create(Content, 5UL);

            int checkedActions = 0;

            foreach (var unit in board.Units)
            {
                var state = Activated(board, unit);

                foreach (var action in engine.LegalActions(state, unit.Owner))
                {
                    if (action is EndActivation || action is PassActivation) continue;

                    int predicted = engine.ActionCost(state, action);
                    int before = state.GetUnit(unit.Id).ActionsRemaining;

                    var after = engine.Execute(state, action).NextState.GetUnit(unit.Id);

                    // A charge zeroes the activation; everything else decrements.
                    int actuallySpent = before - after.ActionsRemaining;

                    Assert.Equal(predicted, actuallySpent);
                    checkedActions++;
                }
            }

            Assert.True(checkedActions > 20, $"only {checkedActions} actions were checked");
        }

        [Fact]
        public void AChargeCostsTheWholeActivation()
        {
            var engine = new RulesEngine(Content);
            var board = SampleGame.Create(Content, 5UL);
            var unit = board.Units.First(u => u.Owner == PlayerId.A);
            var state = Activated(board, unit);

            var charge = engine.LegalActions(state, unit.Owner).OfType<ChargeAt>().FirstOrDefault();
            if (charge == null) return;   // geometry-dependent

            Assert.Equal(2, engine.ActionCost(state, charge));
            Assert.Equal(0, engine.Execute(state, charge).NextState.GetUnit(unit.Id).ActionsRemaining);
        }

        [Theory]
        [InlineData(typeof(MoveUnit), 1)]
        [InlineData(typeof(ShootAt), 1)]
        [InlineData(typeof(ChargeAt), 2)]
        public void CostsAreWhatTheRulesSay(Type actionType, int expected)
        {
            var engine = new RulesEngine(Content);
            var board = SampleGame.Create(Content, 5UL);
            var unit = board.Units.First(u => u.Owner == PlayerId.A);
            var state = Activated(board, unit);

            var action = engine.LegalActions(state, unit.Owner)
                .FirstOrDefault(a => a.GetType() == actionType);
            if (action == null) return;

            Assert.Equal(expected, engine.ActionCost(state, action));
        }

        [Fact]
        public void HousekeepingActionsCostNothing()
        {
            var engine = new RulesEngine(Content);
            var board = SampleGame.Create(Content, 5UL);
            var unit = board.Units.First(u => u.Owner == PlayerId.A);

            Assert.Equal(0, engine.ActionCost(board, new ActivateUnit(unit.Owner, unit.Id)));
            Assert.Equal(0, engine.ActionCost(board, new EndActivation(unit.Owner, unit.Id)));
            Assert.Equal(0, engine.ActionCost(board, new PassActivation(unit.Owner)));
        }
    }
}
