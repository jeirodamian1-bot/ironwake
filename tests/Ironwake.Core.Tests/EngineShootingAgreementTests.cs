using System.Collections.Generic;
using System.Linq;
using Ironwake.Core;
using Xunit;

namespace Ironwake.Core.Tests
{
    /// <summary>
    /// Shooting must not contradict itself either: anything LegalActions offers has to survive
    /// Validate, and the line of sight the client is shown has to be the one the engine used.
    /// </summary>
    public class EngineShootingAgreementTests
    {
        private static readonly IContentPack Content = TestContent.ForSampleGame();

        private static GameState Activated(GameState state, UnitState unit) =>
            state.With(activePlayer: unit.Owner)
                 .WithUnit(unit.With(actionsRemaining: 2))
                 .With(activeUnit: unit.Id);

        [Fact]
        public void EveryShotLegalActionsOffersSurvivesValidate_ThroughAWholeMatch()
        {
            // THE INVARIANT, shooting half. Checked for both players at every step.
            var engine = new StubEngine(Content);
            var state = SampleGame.Create(Content, 777UL);

            int shotsChecked = 0;
            int guard = 0;

            while (state.Phase != PhaseKind.Complete && guard++ < 500)
            {
                foreach (var player in new[] { PlayerId.A, PlayerId.B })
                {
                    foreach (var shot in engine.LegalActions(state, player).OfType<ShootAt>())
                    {
                        var result = engine.Validate(state, shot);
                        Assert.True(result.IsLegal,
                            $"LegalActions offered a shot at {shot.Target} but Validate refused it: {result}");
                        shotsChecked++;
                    }
                }

                var legal = engine.LegalActions(state, state.ActivePlayer);
                if (legal.Count == 0) break;

                var outcome = engine.Execute(state, MatchPolicy.Pick(legal));
                state = outcome.NextState;
                if (outcome.IsTerminal) break;
            }

            // Non-vacuous: if no shot were ever offered this test would prove nothing. The
            // bar is a floor, not a target — the harness policy now prefers closing to melee,
            // so a match trades fewer volleys than it used to.
            Assert.True(shotsChecked > 10, $"only {shotsChecked} shots were checked");
        }

        [Fact]
        public void LegalActionsNeverOffersAShotWithoutLineOfSight()
        {
            var engine = new StubEngine(Content);
            var board = SampleGame.Create(Content, 5UL);

            int checkedShots = 0;

            foreach (var unit in board.Units)
            {
                var state = Activated(board, unit);

                foreach (var shot in engine.LegalActions(state, unit.Owner).OfType<ShootAt>())
                {
                    var shooter = state.GetUnit(shot.Unit);
                    var target = state.GetUnit(shot.Target);

                    Assert.True(LineOfSight.HasLineOfSight(state, shooter.Position, target.Position),
                        $"offered a shot from {shooter.Position} to {target.Position} with no sight");
                    checkedShots++;
                }
            }

            Assert.True(checkedShots > 0, "no shots were offered at all");
        }

        [Fact]
        public void CheckLineOfSightAgreesWithWhatValidateDecides()
        {
            var engine = new StubEngine(Content);
            var board = SampleGame.Create(Content, 5UL);

            foreach (var shooter in board.Units)
            {
                var state = Activated(board, shooter);

                foreach (var target in board.Units.Where(u => u.Owner != shooter.Owner))
                {
                    var los = engine.CheckLineOfSight(state, shooter.Id, target.Id);
                    var check = engine.Validate(state,
                        new ShootAt(shooter.Owner, shooter.Id, target.Id, "w"));

                    // A blocked trace must be exactly why a shot is refused — never some
                    // other reason, and never a shot that is somehow still allowed.
                    if (check.ReasonCode == ReasonCodes.NoLineOfSight)
                        Assert.True(los.IsBlocked, "refused for no LOS, but the trace was clear");

                    if (check.IsLegal)
                        Assert.False(los.IsBlocked, "the shot was legal but the trace said blocked");
                }
            }
        }

        [Fact]
        public void CheckLineOfSightIsBlockedForAUnitThatDoesNotExist()
        {
            var engine = new StubEngine(Content);
            var board = SampleGame.Create(Content, 5UL);

            var los = engine.CheckLineOfSight(board, board.Units[0].Id, new UnitId(999));

            Assert.True(los.IsBlocked);
            Assert.Null(los.BlockingHex);
        }

        // ---- the refusal ------------------------------------------------------------

        [Fact]
        public void AShotThroughObscuringIsRefusedAndNamesTheBlockingHex()
        {
            var wall = new Hex(1, 0);
            var state = TwoUnits(Hex.Zero, new Hex(2, 0), terrain: (wall, TerrainKind.Obscuring));
            var engine = new StubEngine(TestContent.Basic());

            var result = engine.Validate(state, new ShootAt(PlayerId.A, new UnitId(1), new UnitId(2), "w"));

            Assert.False(result.IsLegal);
            Assert.Equal(ReasonCodes.NoLineOfSight, result.ReasonCode);
            Assert.Contains(wall.ToString(), result.Detail);
        }

        [Fact]
        public void TheSameShotFromElevatedGroundIsAllowed()
        {
            var wall = new Hex(1, 0);
            var engine = new StubEngine(TestContent.Basic());

            var blocked = TwoUnits(Hex.Zero, new Hex(2, 0), (wall, TerrainKind.Obscuring));
            Assert.Equal(ReasonCodes.NoLineOfSight,
                engine.Validate(blocked, new ShootAt(PlayerId.A, new UnitId(1), new UnitId(2), "w")).ReasonCode);

            var high = TwoUnits(Hex.Zero, new Hex(2, 0),
                (wall, TerrainKind.Obscuring), (Hex.Zero, TerrainKind.Elevated));

            Assert.True(engine.Validate(high,
                new ShootAt(PlayerId.A, new UnitId(1), new UnitId(2), "w")).IsLegal);
        }

        // ---- cover changes the dice ---------------------------------------------------

        [Fact]
        public void CoverMakesTheTargetHarderToHit()
        {
            // Identical seed, identical shooter, identical dice — the only difference is what
            // the target is standing on.
            var open = Fire(TerrainKind.Open);
            var cover = Fire(TerrainKind.Cover);

            // Same dice were rolled, so any change in hits comes from the target number.
            Assert.Equal(open.Roll.Results, cover.Roll.Results);
            Assert.Equal(open.Roll.FinalTarget + 1, cover.Roll.FinalTarget);
            Assert.True(cover.Roll.Successes < open.Roll.Successes,
                $"cover {cover.Roll.Successes} hits vs open {open.Roll.Successes} — cover did nothing");
        }

        [Fact]
        public void TheDiceEventCarriesTheModifierAsData()
        {
            // The number must never shift silently, and the reason must be readable without
            // parsing prose — a client renders these as chips.
            var open = Fire(TerrainKind.Open);
            var cover = Fire(TerrainKind.Cover);

            Assert.Empty(open.Roll.Modifiers);

            var modifier = Assert.Single(cover.Roll.Modifiers);
            Assert.Equal(ModifierSource.Cover, modifier.Source);
            Assert.Equal(-1, modifier.Value);

            // Base and final are both on the event, so the shift is inspectable.
            Assert.Equal(open.Roll.BaseTarget, cover.Roll.BaseTarget);
            Assert.Equal(cover.Roll.BaseTarget + 1, cover.Roll.FinalTarget);

            // And the prose still reads for a human.
            Assert.Contains("cover", cover.Roll.Describe());
        }

        [Fact]
        public void EveryRollNamesWhoRolledIt()
        {
            // The attribution gap: a log line used to say "to-hit (4+)" with no way to tell
            // whose dice they were without inferring it from the next event.
            var shot = Fire(TerrainKind.Open);

            Assert.Equal(new UnitId(1), shot.Roll.Roller);
            Assert.Equal(new UnitId(2), shot.Roll.Target);
            Assert.Equal(RollKind.ToHit, shot.Roll.RollKind);

            // The saving unit is the one that rolls its own save.
            Assert.Equal(new UnitId(2), shot.Save.Roller);
            Assert.Equal(new UnitId(1), shot.Save.Target);
            Assert.Equal(RollKind.Save, shot.Save.RollKind);
        }

        [Fact]
        public void ATargetOnObscuringGetsCoverToo()
        {
            var obscured = Fire(TerrainKind.Obscuring);
            var open = Fire(TerrainKind.Open);

            Assert.Equal(open.Roll.FinalTarget + 1, obscured.Roll.FinalTarget);
            Assert.Contains(obscured.Roll.Modifiers, m => m.Source == ModifierSource.Cover);
        }

        [Fact]
        public void AnElevatedShooterIgnoresTheTargetsCover()
        {
            var fromTheGround = Fire(TerrainKind.Cover);
            var fromTheHill = Fire(TerrainKind.Cover, shooterTerrain: TerrainKind.Elevated);
            var open = Fire(TerrainKind.Open);

            Assert.Equal(open.Roll.FinalTarget, fromTheHill.Roll.FinalTarget);
            Assert.Empty(fromTheHill.Roll.Modifiers);
            Assert.Equal(open.Roll.FinalTarget + 1, fromTheGround.Roll.FinalTarget);
        }

        // ---- scenario helpers -----------------------------------------------------------

        private static GameState TwoUnits(Hex shooter, Hex target, params (Hex, TerrainKind)[] terrain)
        {
            var map = new Dictionary<Hex, TerrainKind>();
            foreach (var (hex, kind) in terrain) map[hex] = kind;

            var units = new List<UnitState>
            {
                Unit(1, PlayerId.A, shooter, models: 1),
                Unit(2, PlayerId.B, target, models: 1),
            };

            return new GameState(
                round: 1, phase: PhaseKind.Activation,
                activePlayer: PlayerId.A, activeUnit: new UnitId(1),
                board: new BoardState(8, map), units: units,
                objectives: new List<ObjectiveState>(),
                scoreA: 0, scoreB: 0, rng: new RngState(4242UL), contentVersion: "test");
        }

        private static UnitState Unit(int id, PlayerId owner, Hex pos, int models) =>
            new UnitState(
                new UnitId(id), owner, "test_unit", pos, facing: 0,
                models: Enumerable.Range(0, models).Select(_ => new ModelState(1)).ToList(),
                statuses: new List<StatusKind>(),
                hasActivated: false, actionsRemaining: 2);

        private sealed class Shot
        {
            /// <summary>The to-hit roll.</summary>
            public DiceRolledEvent Roll;

            /// <summary>The target's save roll.</summary>
            public DiceRolledEvent Save;

            public IReadOnlyList<GameEvent> Events;
        }

        /// <summary>
        /// Fires one identical volley at a target standing on the given terrain. Ten models
        /// means thirty dice, enough that a one-point shift in the target number shows.
        /// </summary>
        private static Shot Fire(TerrainKind targetTerrain, TerrainKind? shooterTerrain = null)
        {
            var shooterHex = Hex.Zero;
            var targetHex = new Hex(3, 0);

            var map = new Dictionary<Hex, TerrainKind> { { targetHex, targetTerrain } };
            if (shooterTerrain.HasValue) map[shooterHex] = shooterTerrain.Value;

            var units = new List<UnitState>
            {
                Unit(1, PlayerId.A, shooterHex, models: 10),
                Unit(2, PlayerId.B, targetHex, models: 20),
            };

            var state = new GameState(
                round: 1, phase: PhaseKind.Activation,
                activePlayer: PlayerId.A, activeUnit: new UnitId(1),
                board: new BoardState(8, map), units: units,
                objectives: new List<ObjectiveState>(),
                scoreA: 0, scoreB: 0, rng: new RngState(20250808UL), contentVersion: "test");

            var engine = new StubEngine(TestContent.Basic());
            var action = new ShootAt(PlayerId.A, new UnitId(1), new UnitId(2), "test_weapon");

            Assert.True(engine.Validate(state, action).IsLegal);
            var result = engine.Execute(state, action);

            var rolls = result.Events.OfType<DiceRolledEvent>().ToList();

            return new Shot
            {
                Roll = rolls.First(e => e.RollKind == RollKind.ToHit),
                Save = rolls.First(e => e.RollKind == RollKind.Save),
                Events = result.Events,
            };
        }
    }
}
