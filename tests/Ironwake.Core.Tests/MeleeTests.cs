using System.Collections.Generic;
using System.Linq;
using Ironwake.Core;
using Xunit;

namespace Ironwake.Core.Tests
{
    /// <summary>
    /// Charging, fighting and engagement. Scenarios are built to order so each test states
    /// the geometry and statlines it depends on.
    /// </summary>
    public class MeleeTests
    {
        private const string Brawler = "brawler";   // carries a melee weapon
        private const string Gunner = "gunner";     // ranged only

        private static IContentPack Pack(int melee = 3, int accuracy = 5, int move = 40)
        {
            var gun = new WeaponDefinition("gun", "Gun", 60, 2, 4, 0, 1);
            var maul = new WeaponDefinition("maul", "Maul", 0, 3, 5, 1, 2);

            var brawler = new UnitDefinition(Brawler, "f", "Brawler", 50, 1,
                new Statline(move, accuracy, melee, 4, 4, 1, 6), new[] { "gun", "maul" });
            var gunner = new UnitDefinition(Gunner, "f", "Gunner", 50, 1,
                new Statline(move, accuracy, melee, 4, 4, 1, 6), new[] { "gun" });

            return new TestContentPack(
                new[] { brawler, gunner }, new[] { gun, maul },
                new[] { new FactionDefinition("f", "F") });
        }

        private static UnitState Unit(int id, PlayerId owner, string def, Hex at,
                                      int models = 1, params StatusKind[] statuses) =>
            new UnitState(new UnitId(id), owner, def, at, 0,
                Enumerable.Range(0, models).Select(_ => new ModelState(1)).ToList(),
                statuses.ToList(), false, 2);

        private static GameState Field(
            IEnumerable<UnitState> units,
            Dictionary<Hex, TerrainKind> terrain = null, int radius = 8) =>
            new GameState(
                round: 1, phase: PhaseKind.Activation,
                activePlayer: PlayerId.A, activeUnit: new UnitId(1),
                board: new BoardState(radius, terrain),
                units: units.ToList(), objectives: new List<ObjectiveState>(),
                scoreA: 0, scoreB: 0, rng: new RngState(4242UL), contentVersion: "test");

        private static GameState TwoUnits(Hex a, Hex b, string aDef = Brawler,
                                          Dictionary<Hex, TerrainKind> terrain = null,
                                          params StatusKind[] aStatuses) =>
            Field(new[]
            {
                Unit(1, PlayerId.A, aDef, a, 1, aStatuses),
                Unit(2, PlayerId.B, Brawler, b),
            }, terrain);

        private static ChargeAt Charge() => new ChargeAt(PlayerId.A, new UnitId(1), new UnitId(2));
        private static FightUnit Fight() => new FightUnit(PlayerId.A, new UnitId(1), new UnitId(2));

        // ---- charge ---------------------------------------------------------------

        [Fact]
        public void AChargeIsLegalWhenAPathToAnAdjacentHexExists()
        {
            var engine = new RulesEngine(Pack());
            var state = TwoUnits(Hex.Zero, new Hex(3, 0));   // 4-hex move, target 3 away

            Assert.True(engine.Validate(state, Charge()).IsLegal);
        }

        [Fact]
        public void AChargeBeyondTheMoveAllowanceIsRefused()
        {
            var engine = new RulesEngine(Pack(move: 20));     // 2 hexes
            var state = TwoUnits(Hex.Zero, new Hex(5, 0));   // adjacent hex is 4 away

            var result = engine.Validate(state, Charge());

            Assert.False(result.IsLegal);
            Assert.Equal(ReasonCodes.NoChargePath, result.ReasonCode);
        }

        [Fact]
        public void AChargeWithNoPathToAnyAdjacentHexIsRefused()
        {
            // Wall the target in completely: it can be seen, but not reached.
            var target = new Hex(3, 0);
            var terrain = new Dictionary<Hex, TerrainKind>();
            for (int d = 0; d < 6; d++) terrain[target.Neighbour(d)] = TerrainKind.Impassable;

            var engine = new RulesEngine(Pack());
            var state = TwoUnits(Hex.Zero, target, Brawler, terrain);

            var result = engine.Validate(state, Charge());

            Assert.False(result.IsLegal);
            Assert.Equal(ReasonCodes.NoChargePath, result.ReasonCode);
        }

        [Fact]
        public void AShakenUnitCannotCharge()
        {
            var engine = new RulesEngine(Pack());
            var state = TwoUnits(Hex.Zero, new Hex(3, 0), Brawler, null, StatusKind.Shaken);

            var result = engine.Validate(state, Charge());

            Assert.False(result.IsLegal);
            Assert.Equal(ReasonCodes.UnitShaken, result.ReasonCode);
        }

        [Fact]
        public void AChargeWithoutLineOfSightIsRefused()
        {
            var terrain = new Dictionary<Hex, TerrainKind> { { new Hex(1, 0), TerrainKind.Obscuring } };
            var engine = new RulesEngine(Pack());
            var state = TwoUnits(Hex.Zero, new Hex(2, 0), Brawler, terrain);

            var result = engine.Validate(state, Charge());

            Assert.False(result.IsLegal);
            Assert.Equal(ReasonCodes.NoLineOfSight, result.ReasonCode);
        }

        [Fact]
        public void AChargeMovesTheUnitAdjacentAndEngagesBothSides()
        {
            var engine = new RulesEngine(Pack());
            var state = TwoUnits(Hex.Zero, new Hex(3, 0));

            var result = engine.Execute(state, Charge());
            var charger = result.NextState.GetUnit(new UnitId(1));
            var target = result.NextState.GetUnit(new UnitId(2));

            Assert.Equal(1, charger.Position.DistanceTo(target.Position));
            Assert.True(charger.HasStatus(StatusKind.Engaged));
            Assert.True(target.HasStatus(StatusKind.Engaged));

            // The approach is emitted so a client can animate the run in.
            var moved = Assert.Single(result.Events.OfType<UnitMovedEvent>());
            Assert.Equal(Hex.Zero, moved.Path[0]);
            Assert.Equal(charger.Position, moved.Path[moved.Path.Count - 1]);
        }

        [Fact]
        public void AChargeSpendsTheWholeActivationAndFightsForFree()
        {
            var engine = new RulesEngine(Pack());
            var state = TwoUnits(Hex.Zero, new Hex(3, 0));

            var result = engine.Execute(state, Charge());

            Assert.Equal(0, result.NextState.GetUnit(new UnitId(1)).ActionsRemaining);
            // The free fight happened: dice were rolled on arrival.
            Assert.Contains(result.Events.OfType<DiceRolledEvent>(), e => e.RollKind == RollKind.ToHit);
        }

        [Fact]
        public void AUnitWithOneActionLeftCannotStartACharge()
        {
            var engine = new RulesEngine(Pack());
            var state = TwoUnits(Hex.Zero, new Hex(3, 0));
            state = state.WithUnit(state.GetUnit(new UnitId(1)).With(actionsRemaining: 1));

            var result = engine.Validate(state, Charge());

            Assert.False(result.IsLegal);
            Assert.Equal(ReasonCodes.NoActionsRemaining, result.ReasonCode);
        }

        [Fact]
        public void TheApproachHexIsChosenDeterministically()
        {
            var state = TwoUnits(Hex.Zero, new Hex(3, 0));

            var first = Melee.FindApproach(state, new UnitId(1), new UnitId(2), 4);
            Assert.True(first.IsPossible);

            for (int i = 0; i < 50; i++)
            {
                var again = Melee.FindApproach(state, new UnitId(1), new UnitId(2), 4);
                Assert.Equal(first.Destination, again.Destination);
                Assert.Equal(first.Path, again.Path);
            }
        }

        // ---- fight -----------------------------------------------------------------

        [Fact]
        public void FightingRequiresAdjacency()
        {
            var engine = new RulesEngine(Pack());
            var state = TwoUnits(Hex.Zero, new Hex(3, 0));

            var result = engine.Validate(state, Fight());

            Assert.False(result.IsLegal);
            Assert.Equal(ReasonCodes.NotAdjacent, result.ReasonCode);
        }

        [Fact]
        public void FightingRequiresAMeleeWeapon()
        {
            var engine = new RulesEngine(Pack());
            var state = TwoUnits(Hex.Zero, new Hex(1, 0), Gunner);

            var result = engine.Validate(state, Fight());

            Assert.False(result.IsLegal);
            Assert.Equal(ReasonCodes.NoMeleeWeapon, result.ReasonCode);
        }

        [Theory]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(5)]
        public void MeleeUsesTheMeleeStatNotAccuracy(int meleeStat)
        {
            // Accuracy is pinned well away from Melee so a mix-up cannot pass by coincidence.
            var engine = new RulesEngine(Pack(melee: meleeStat, accuracy: 6));
            var state = TwoUnits(Hex.Zero, new Hex(1, 0));

            var toHit = engine.Execute(state, Fight()).Events
                .OfType<DiceRolledEvent>().First(e => e.RollKind == RollKind.ToHit);

            Assert.Equal(meleeStat, toHit.BaseTarget);
            Assert.NotEqual(6, toHit.BaseTarget);
        }

        [Fact]
        public void CoverDoesNotApplyInMelee()
        {
            // A ruling: once you are close enough to swing, the wall is not between you.
            var targetHex = new Hex(1, 0);
            var terrain = new Dictionary<Hex, TerrainKind> { { targetHex, TerrainKind.Cover } };

            var engine = new RulesEngine(Pack());
            var state = TwoUnits(Hex.Zero, targetHex, Brawler, terrain);

            var toHit = engine.Execute(state, Fight()).Events
                .OfType<DiceRolledEvent>().First(e => e.RollKind == RollKind.ToHit);

            Assert.DoesNotContain(toHit.Modifiers, m => m.Source == ModifierSource.Cover);
            Assert.False(Melee.CoverAppliesInMelee);
        }

        [Fact]
        public void MeleeWoundsAndSavesExactlyAsShootingDoes()
        {
            var engine = new RulesEngine(Pack());
            var state = TwoUnits(Hex.Zero, new Hex(1, 0));

            var rolls = engine.Execute(state, Fight()).Events.OfType<DiceRolledEvent>().ToList();
            var wound = rolls.First(e => e.RollKind == RollKind.ToWound);
            var save = rolls.First(e => e.RollKind == RollKind.Save);

            // maul Power 5 against Resilience 4, and Save 4 worsened by AP 1.
            Assert.Equal(Wounding.TargetFor(5, 4), wound.FinalTarget);
            Assert.Equal(5, save.FinalTarget);
            Assert.Contains(save.Modifiers, m => m.Source == ModifierSource.ArmourPiercing);
        }

        // ---- engagement --------------------------------------------------------------

        [Fact]
        public void AnEngagedUnitCannotShoot()
        {
            var engine = new RulesEngine(Pack());
            var state = TwoUnits(Hex.Zero, new Hex(1, 0));
            state = Melee.RefreshEngagement(state);

            Assert.True(state.GetUnit(new UnitId(1)).HasStatus(StatusKind.Engaged));

            var result = engine.Validate(state,
                new ShootAt(PlayerId.A, new UnitId(1), new UnitId(2), "gun"));

            Assert.False(result.IsLegal);
            Assert.Equal(ReasonCodes.UnitEngaged, result.ReasonCode);
        }

        [Fact]
        public void AnEngagedUnitMayStillWalkAway()
        {
            // Deferred, not decided: no free attacks and no zone of control yet.
            var engine = new RulesEngine(Pack());
            var state = Melee.RefreshEngagement(TwoUnits(Hex.Zero, new Hex(1, 0)));

            var away = Movement.FindPath(state, new UnitId(1), new Hex(-2, 0), 4);

            Assert.True(engine.Validate(state, new MoveUnit(PlayerId.A, new UnitId(1), away)).IsLegal);
        }

        [Fact]
        public void EngagementClearsWhenNoEnemyIsAdjacent()
        {
            var engine = new RulesEngine(Pack());
            var state = Melee.RefreshEngagement(TwoUnits(Hex.Zero, new Hex(1, 0)));
            Assert.True(state.GetUnit(new UnitId(1)).HasStatus(StatusKind.Engaged));

            var away = Movement.FindPath(state, new UnitId(1), new Hex(-3, 0), 4);
            var after = engine.Execute(state, new MoveUnit(PlayerId.A, new UnitId(1), away)).NextState;

            Assert.False(after.GetUnit(new UnitId(1)).HasStatus(StatusKind.Engaged));
            Assert.False(after.GetUnit(new UnitId(2)).HasStatus(StatusKind.Engaged));
        }

        [Fact]
        public void ADestroyedUnitPinsNobody()
        {
            var state = Melee.RefreshEngagement(TwoUnits(Hex.Zero, new Hex(1, 0)));
            Assert.True(state.GetUnit(new UnitId(1)).HasStatus(StatusKind.Engaged));

            var corpse = state.GetUnit(new UnitId(2));
            var wiped = state.WithUnit(corpse.With(
                models: new List<ModelState> { new ModelState(0, isSlain: true) }));

            var after = Melee.RefreshEngagement(wiped);

            Assert.False(after.GetUnit(new UnitId(1)).HasStatus(StatusKind.Engaged));
        }

        [Fact]
        public void FriendlyUnitsStandingNextToEachOtherAreNotEngaged()
        {
            var state = Melee.RefreshEngagement(Field(new[]
            {
                Unit(1, PlayerId.A, Brawler, Hex.Zero),
                Unit(2, PlayerId.A, Brawler, new Hex(1, 0)),
            }));

            Assert.False(state.GetUnit(new UnitId(1)).HasStatus(StatusKind.Engaged));
        }
    }
}
