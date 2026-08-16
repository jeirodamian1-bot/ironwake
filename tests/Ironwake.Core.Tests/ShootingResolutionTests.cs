using System.Collections.Generic;
using System.Linq;
using Ironwake.Core;
using Xunit;

namespace Ironwake.Core.Tests
{
    /// <summary>
    /// Shooting resolved from real content values: attacks, wound table, armour piercing and
    /// weapon damage. Every number here comes from a pack, none from a constant in the engine.
    /// </summary>
    public class ShootingResolutionTests
    {
        private const string Shooter = "shooter";
        private const string Victim = "victim";

        /// <summary>A pack built to order, so each test states the statline it depends on.</summary>
        private static IContentPack Pack(
            int power = 4, int armourPiercing = 0, int damage = 1, int attacks = 3, int range = 60,
            int accuracy = 4, int targetSave = 4, int targetResilience = 4, int targetWounds = 1)
        {
            var weapon = new WeaponDefinition(
                "gun", "Gun", range, attacks, power, armourPiercing, damage);
            var melee = new WeaponDefinition("maul", "Maul", 0, 4, 5, 1, 2);

            var shooter = new UnitDefinition(
                Shooter, "f", "Shooter", 50, 1,
                new Statline(40, accuracy, 4, 4, 4, 1, 6), new[] { "gun" });

            var meleeOnly = new UnitDefinition(
                "brawler", "f", "Brawler", 50, 1,
                new Statline(40, accuracy, 4, 4, 4, 1, 6), new[] { "maul" });

            var unarmed = new UnitDefinition(
                "unarmed", "f", "Unarmed", 50, 1,
                new Statline(40, accuracy, 4, 4, 4, 1, 6));

            var victim = new UnitDefinition(
                Victim, "f", "Victim", 50, 1,
                new Statline(40, 4, 4, targetResilience, targetSave, targetWounds, 6), new[] { "gun" });

            return new TestContentPack(
                new[] { shooter, victim, meleeOnly, unarmed },
                new[] { weapon, melee },
                new[] { new FactionDefinition("f", "F") });
        }

        private static UnitState Unit(int id, PlayerId owner, string definition, Hex pos, int models, int wounds = 1) =>
            new UnitState(
                new UnitId(id), owner, definition, pos, facing: 0,
                models: Enumerable.Range(0, models).Select(_ => new ModelState(wounds)).ToList(),
                statuses: new List<StatusKind>(),
                hasActivated: false, actionsRemaining: 2);

        private static GameState Field(string shooterDef = Shooter, int shooterModels = 10,
                                       int victimModels = 20, int victimWounds = 1)
        {
            var units = new List<UnitState>
            {
                Unit(1, PlayerId.A, shooterDef, Hex.Zero, shooterModels),
                Unit(2, PlayerId.B, Victim, new Hex(3, 0), victimModels, victimWounds),
            };

            return new GameState(
                round: 1, phase: PhaseKind.Activation,
                activePlayer: PlayerId.A, activeUnit: new UnitId(1),
                board: new BoardState(8), units: units,
                objectives: new List<ObjectiveState>(),
                scoreA: 0, scoreB: 0, rng: new RngState(20250808UL), contentVersion: "test");
        }

        private static IReadOnlyList<GameEvent> Fire(IContentPack pack, GameState state)
        {
            var engine = new StubEngine(pack);
            var action = new ShootAt(PlayerId.A, new UnitId(1), new UnitId(2), "gun");
            Assert.True(engine.Validate(state, action).IsLegal);
            return engine.Execute(state, action).Events;
        }

        private static DiceRolledEvent RollOf(IReadOnlyList<GameEvent> events, RollKind kind) =>
            events.OfType<DiceRolledEvent>().First(e => e.RollKind == kind);

        // ---- the wound roll now comes from the table ------------------------------

        [Theory]
        [InlineData(8, 4, 2)]    // exactly double
        [InlineData(5, 4, 3)]
        [InlineData(4, 4, 4)]    // equal
        [InlineData(3, 4, 5)]
        [InlineData(2, 4, 6)]    // exactly half
        public void TheWoundRollUsesPowerAgainstResilience(int power, int resilience, int expected)
        {
            var events = Fire(Pack(power: power, targetResilience: resilience), Field());

            var wound = RollOf(events, RollKind.ToWound);

            Assert.Equal(expected, wound.BaseTarget);
            Assert.Equal(expected, wound.FinalTarget);
            Assert.Equal(Wounding.TargetFor(power, resilience), wound.FinalTarget);
        }

        [Fact]
        public void TheFlatFourPlusIsGone()
        {
            // The stub used a hardcoded 4+ regardless of the statlines. Two different
            // matchups must now produce two different wound rolls.
            var weak = RollOf(Fire(Pack(power: 2, targetResilience: 6), Field()), RollKind.ToWound);
            var strong = RollOf(Fire(Pack(power: 9, targetResilience: 3), Field()), RollKind.ToWound);

            Assert.Equal(6, weak.FinalTarget);
            Assert.Equal(2, strong.FinalTarget);
        }

        // ---- armour piercing --------------------------------------------------------

        [Fact]
        public void ArmourPiercingWorsensTheSaveAndSaysSo()
        {
            var events = Fire(Pack(armourPiercing: 1, targetSave: 4), Field());

            var save = RollOf(events, RollKind.Save);

            Assert.Equal(4, save.BaseTarget);
            Assert.Equal(5, save.FinalTarget);

            var modifier = Assert.Single(save.Modifiers);
            Assert.Equal(ModifierSource.ArmourPiercing, modifier.Source);
            Assert.Equal(-1, modifier.Value);
            Assert.Contains("AP", save.Describe());
        }

        [Fact]
        public void NoArmourPiercingLeavesTheSaveUntouched()
        {
            var save = RollOf(Fire(Pack(armourPiercing: 0, targetSave: 4), Field()), RollKind.Save);

            Assert.Empty(save.Modifiers);
            Assert.Equal(save.BaseTarget, save.FinalTarget);
        }

        [Fact]
        public void HeavyArmourPiercingIsStillCappedAtSix()
        {
            var save = RollOf(Fire(Pack(armourPiercing: 5, targetSave: 4), Field()), RollKind.Save);

            Assert.Equal(6, save.FinalTarget);
        }

        [Fact]
        public void AnUnarmouredTargetRollsNoSaveAtAll()
        {
            // Content writes Save 7 to mean no armour. That is not a hard roll, it is the
            // absence of one, so no dice are thrown.
            var save = RollOf(Fire(Pack(targetSave: 7), Field()), RollKind.Save);

            Assert.Empty(save.Results);
            Assert.Equal(0, save.Successes);
            Assert.Contains("cannot", save.Describe());
        }

        // ---- damage -----------------------------------------------------------------

        [Fact]
        public void EachUnsavedWoundDealsTheWeaponsDamage()
        {
            // Damage 2 against one-wound models: each unsaved wound kills exactly one model,
            // the excess is lost rather than spilling onto the next.
            var events = Fire(Pack(damage: 2, targetSave: 7), Field(victimModels: 20));

            var attack = events.OfType<AttackResolvedEvent>().Single();
            int unsaved = attack.Wounds - attack.Saved;

            Assert.True(unsaved > 0, "the volley did nothing, so this proves nothing");
            Assert.Equal(unsaved * 2, attack.DamageDealt);
            Assert.Equal(unsaved, events.OfType<ModelSlainEvent>().Count());
        }

        [Fact]
        public void DamageTwoKillsATwoWoundModelInOneHit()
        {
            var events = Fire(Pack(damage: 2, targetSave: 7), Field(victimModels: 20, victimWounds: 2));

            var attack = events.OfType<AttackResolvedEvent>().Single();
            int unsaved = attack.Wounds - attack.Saved;

            Assert.True(unsaved > 0);
            Assert.Equal(unsaved, events.OfType<ModelSlainEvent>().Count());
        }

        [Fact]
        public void DamageOneLeavesATwoWoundModelStanding()
        {
            var events = Fire(Pack(damage: 1, targetSave: 7), Field(victimModels: 20, victimWounds: 2));

            var attack = events.OfType<AttackResolvedEvent>().Single();
            int unsaved = attack.Wounds - attack.Saved;

            Assert.True(unsaved > 0);
            // Half as many kills, rounded down, because each model soaks two wounds.
            Assert.True(events.OfType<ModelSlainEvent>().Count() <= unsaved / 2 + 1);
            Assert.Equal(unsaved, attack.DamageDealt);
        }

        // ---- attacks come from the weapon ---------------------------------------------

        [Theory]
        [InlineData(1)]
        [InlineData(3)]
        [InlineData(5)]
        public void TheDicePoolIsWeaponAttacksTimesModels(int attacks)
        {
            var events = Fire(Pack(attacks: attacks), Field(shooterModels: 4));

            Assert.Equal(attacks * 4, RollOf(events, RollKind.ToHit).Results.Length);
        }

        // ---- melee weapons cannot shoot -------------------------------------------------

        [Fact]
        public void ARangeZeroWeaponCannotBeFired()
        {
            var engine = new StubEngine(Pack());
            var state = Field(shooterDef: "brawler");

            var result = engine.Validate(state, new ShootAt(PlayerId.A, new UnitId(1), new UnitId(2), "maul"));

            Assert.False(result.IsLegal);
            Assert.Equal(ReasonCodes.WeaponIsMelee, result.ReasonCode);
            Assert.Contains("Maul", result.Detail);
        }

        [Fact]
        public void AUnitCarryingNothingCannotShoot()
        {
            var engine = new StubEngine(Pack());
            var state = Field(shooterDef: "unarmed");

            var result = engine.Validate(state, new ShootAt(PlayerId.A, new UnitId(1), new UnitId(2), "none"));

            Assert.False(result.IsLegal);
            Assert.Equal(ReasonCodes.NoWeapon, result.ReasonCode);
        }

        [Fact]
        public void AMeleeUnitIsNeverOfferedAShot()
        {
            var engine = new StubEngine(Pack());
            var state = Field(shooterDef: "brawler");

            Assert.Empty(engine.LegalActions(state, PlayerId.A).OfType<ShootAt>());
        }

        [Fact]
        public void RangeComesFromContentNotAConstant()
        {
            var engine = new StubEngine(Pack(range: 20));   // 2 hexes
            var state = Field();                            // target is 3 hexes away

            var result = engine.Validate(state, new ShootAt(PlayerId.A, new UnitId(1), new UnitId(2), "gun"));

            Assert.False(result.IsLegal);
            Assert.Equal(ReasonCodes.OutOfRange, result.ReasonCode);

            // Same board, longer weapon.
            Assert.True(new StubEngine(Pack(range: 40))
                .Validate(state, new ShootAt(PlayerId.A, new UnitId(1), new UnitId(2), "gun")).IsLegal);
        }
    }

    /// <summary>
    /// The wound table against the content that actually ships. These fail if a balance pass
    /// silently changes a headline matchup.
    /// </summary>
    public class StarterMatchupTests
    {
        private static readonly IContentPack Content = TestContent.ForSampleGame();

        [Fact]
        public void AshguardLineholderShootingCinderkinRaider()
        {
            // ash_carbine Power 4 vs raider Resilience 3 -> Power > Resilience -> 3+.
            var weapon = Content.GetWeapon("ash_carbine");
            var target = Content.GetUnit("cinderkin_raider");

            Assert.Equal(4, weapon.Power);
            Assert.Equal(3, target.Stats.Resilience);
            Assert.Equal(3, Wounding.TargetFor(weapon.Power, target.Stats.Resilience));
        }

        [Fact]
        public void CinderkinRaiderShootingAshguardLineholder()
        {
            // cinder_spitter Power 3 vs lineholder Resilience 5 -> less than, more than half -> 5+.
            var weapon = Content.GetWeapon("cinder_spitter");
            var target = Content.GetUnit("ashguard_lineholder");

            Assert.Equal(3, weapon.Power);
            Assert.Equal(5, target.Stats.Resilience);
            Assert.Equal(5, Wounding.TargetFor(weapon.Power, target.Stats.Resilience));
        }

        [Fact]
        public void TheMatchupIsAsymmetricInTheAshguardsFavour()
        {
            // The whole point of the two factions feeling different: the tough faction is
            // harder to hurt than the fragile one, in both directions.
            int ashguardShooting = Wounding.TargetFor(
                Content.GetWeapon("ash_carbine").Power,
                Content.GetUnit("cinderkin_raider").Stats.Resilience);

            int cinderkinShooting = Wounding.TargetFor(
                Content.GetWeapon("cinder_spitter").Power,
                Content.GetUnit("ashguard_lineholder").Stats.Resilience);

            Assert.True(ashguardShooting < cinderkinShooting,
                $"ashguard wound on {ashguardShooting}+, cinderkin on {cinderkinShooting}+ — " +
                "the durability difference has been flattened");
        }

        [Fact]
        public void EveryStarterWeaponWoundsEveryStarterUnitOnARollableNumber()
        {
            foreach (var unit in Content.AllUnits)
            {
                foreach (var weaponId in unit.WeaponIds)
                {
                    var weapon = Content.GetWeapon(weaponId);
                    foreach (var victim in Content.AllUnits)
                    {
                        int target = Wounding.TargetFor(weapon.Power, victim.Stats.Resilience);
                        Assert.InRange(target, 2, 6);
                    }
                }
            }
        }
    }
}
