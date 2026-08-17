using System.Collections.Generic;
using System.Linq;
using Ironwake.Core;
using Xunit;

namespace Ironwake.Core.Tests
{
    /// <summary>
    /// Round-end morale, and the Shaken status it applies.
    /// </summary>
    public class MoraleTests
    {
        private static IContentPack Pack(int nerve = 6)
        {
            var gun = new WeaponDefinition("gun", "Gun", 60, 2, 4, 0, 1);
            var unit = new UnitDefinition("trooper", "f", "Trooper", 50, 5,
                new Statline(40, 4, 4, 4, 4, 1, nerve), new[] { "gun" });

            return new TestContentPack(
                new[] { unit }, new[] { gun }, new[] { new FactionDefinition("f", "F") });
        }

        private static UnitState Trooper(int id, PlayerId owner, Hex at, int lost = 0, int models = 5) =>
            new UnitState(new UnitId(id), owner, "trooper", at, 0,
                Enumerable.Range(0, models).Select(_ => new ModelState(1)).ToList(),
                new List<StatusKind>(), hasActivated: true, actionsRemaining: 0,
                modelsLostThisRound: lost);

        private static GameState Field(params UnitState[] units) =>
            new GameState(
                round: 1, phase: PhaseKind.Activation,
                activePlayer: PlayerId.A, activeUnit: UnitId.None,
                board: new BoardState(8), units: units.ToList(),
                objectives: new List<ObjectiveState>(),
                scoreA: 0, scoreB: 0, rng: new RngState(4242UL), contentVersion: "test");

        // ---- the rule itself, without dice -----------------------------------------

        [Theory]
        [InlineData(1, 0, 6, true)]    // no losses, trivially under
        [InlineData(6, 0, 6, true)]    // exactly Nerve passes
        [InlineData(3, 3, 6, true)]    // exactly Nerve with losses
        [InlineData(4, 3, 6, false)]   // one over fails
        [InlineData(6, 1, 6, false)]
        [InlineData(1, 6, 6, false)]
        public void OverNerveFails(int die, int lost, int nerve, bool expected)
        {
            // "Over Nerve fails" — equal to Nerve is a pass.
            Assert.Equal(expected, Morale.Passes(die, lost, nerve));
        }

        [Fact]
        public void EnoughLossesAlwaysFailWhateverTheDieShows()
        {
            // Lose more than your Nerve and no roll can save you.
            for (int die = 1; die <= 6; die++)
                Assert.False(Morale.Passes(die, 6, 6));
        }

        [Fact]
        public void NoLossesMeansNoTest()
        {
            var untouched = Trooper(1, PlayerId.A, Hex.Zero, lost: 0);
            var bloodied = Trooper(2, PlayerId.B, new Hex(3, 0), lost: 2);

            Assert.False(Morale.MustTest(untouched));
            Assert.True(Morale.MustTest(bloodied));
        }

        // ---- the round-end step ------------------------------------------------------

        [Fact]
        public void OnlyUnitsThatLostModelsRollAtAll()
        {
            var events = new List<GameEvent>();
            var state = Field(
                Trooper(1, PlayerId.A, Hex.Zero, lost: 0),
                Trooper(2, PlayerId.B, new Hex(3, 0), lost: 2));

            Morale.Resolve(state, Pack(), new Rng(new RngState(1UL)), events);

            var rolls = events.OfType<DiceRolledEvent>().Where(e => e.RollKind == RollKind.Morale).ToList();

            Assert.Single(rolls);
            Assert.Equal(new UnitId(2), rolls[0].Roller);
        }

        [Fact]
        public void CatastrophicLossesAlwaysApplyShaken()
        {
            // Nerve 4, six models lost: every possible die fails.
            var events = new List<GameEvent>();
            var state = Field(Trooper(1, PlayerId.A, Hex.Zero, lost: 6, models: 10));

            var after = Morale.Resolve(state, Pack(nerve: 4), new Rng(new RngState(9UL)), events);

            Assert.True(after.GetUnit(new UnitId(1)).HasStatus(StatusKind.Shaken));
            Assert.Contains(events.OfType<StatusAppliedEvent>(),
                e => e.Status == StatusKind.Shaken && e.Unit == new UnitId(1));
        }

        [Fact]
        public void ATrivialLossWithHighNerveNeverShakes()
        {
            // Nerve 7, one model lost: even a 6 totals 7, which is not over Nerve.
            var events = new List<GameEvent>();
            var state = Field(Trooper(1, PlayerId.A, Hex.Zero, lost: 1));

            var after = Morale.Resolve(state, Pack(nerve: 7), new Rng(new RngState(3UL)), events);

            Assert.False(after.GetUnit(new UnitId(1)).HasStatus(StatusKind.Shaken));
        }

        [Fact]
        public void TheLossCounterResetsAfterTesting()
        {
            var events = new List<GameEvent>();
            var state = Field(Trooper(1, PlayerId.A, Hex.Zero, lost: 3));

            var after = Morale.Resolve(state, Pack(), new Rng(new RngState(1UL)), events);

            Assert.Equal(0, after.GetUnit(new UnitId(1)).ModelsLostThisRound);
        }

        [Fact]
        public void TheMoraleRollIsEmittedWithItsOwnRollKind()
        {
            var events = new List<GameEvent>();
            var state = Field(Trooper(1, PlayerId.A, Hex.Zero, lost: 2));

            Morale.Resolve(state, Pack(nerve: 5), new Rng(new RngState(1UL)), events);

            var roll = Assert.Single(events.OfType<DiceRolledEvent>());
            Assert.Equal(RollKind.Morale, roll.RollKind);
            Assert.Equal(5, roll.BaseTarget);        // Nerve, carried for display
            Assert.Single(roll.Results);             // one die, not a pool
            Assert.Contains("morale", roll.Describe());
        }

        // ---- Shaken's life cycle -------------------------------------------------------

        [Fact]
        public void ShakenClearsAfterOneRound()
        {
            // Applied at the end of round N, it survives round N+1 and lifts at the end of it.
            var pack = Pack();
            var events = new List<GameEvent>();

            var shaken = Trooper(1, PlayerId.A, Hex.Zero, lost: 0).WithStatus(StatusKind.Shaken);
            var state = Field(shaken);

            var after = Morale.Resolve(state, pack, new Rng(new RngState(1UL)), events);

            Assert.False(after.GetUnit(new UnitId(1)).HasStatus(StatusKind.Shaken));
        }

        [Fact]
        public void AUnitThatKeepsBleedingStaysShaken()
        {
            // Last round's Shaken clears, then a fresh failure re-applies it — so a unit
            // taking losses every round never actually recovers.
            var pack = Pack(nerve: 3);
            var events = new List<GameEvent>();

            var shaken = Trooper(1, PlayerId.A, Hex.Zero, lost: 5, models: 10)
                .WithStatus(StatusKind.Shaken);

            var after = Morale.Resolve(Field(shaken), pack, new Rng(new RngState(1UL)), events);

            Assert.True(after.GetUnit(new UnitId(1)).HasStatus(StatusKind.Shaken));
        }

        [Fact]
        public void ShakenIsMinusOneToHit()
        {
            var gun = new WeaponDefinition("gun", "Gun", 60, 3, 4, 0, 1);
            var def = new UnitDefinition("trooper", "f", "Trooper", 50, 5,
                new Statline(40, 4, 4, 4, 4, 1, 6), new[] { "gun" });
            var content = new TestContentPack(
                new[] { def }, new[] { gun }, new[] { new FactionDefinition("f", "F") });

            UnitState Shooter(bool shaken) =>
                new UnitState(new UnitId(1), PlayerId.A, "trooper", Hex.Zero, 0,
                    Enumerable.Range(0, 5).Select(_ => new ModelState(1)).ToList(),
                    shaken ? new List<StatusKind> { StatusKind.Shaken } : new List<StatusKind>(),
                    false, 2);

            GameState Board(bool shaken) => new GameState(
                1, PhaseKind.Activation, PlayerId.A, new UnitId(1),
                new BoardState(8),
                new List<UnitState> { Shooter(shaken), Trooper(2, PlayerId.B, new Hex(3, 0)) },
                new List<ObjectiveState>(), 0, 0, new RngState(77UL), "test");

            var engine = new RulesEngine(content);
            var shot = new ShootAt(PlayerId.A, new UnitId(1), new UnitId(2), "gun");

            var steady = engine.Execute(Board(false), shot).Events
                .OfType<DiceRolledEvent>().First(e => e.RollKind == RollKind.ToHit);
            var rattled = engine.Execute(Board(true), shot).Events
                .OfType<DiceRolledEvent>().First(e => e.RollKind == RollKind.ToHit);

            Assert.Empty(steady.Modifiers);
            Assert.Equal(steady.FinalTarget + 1, rattled.FinalTarget);

            var modifier = Assert.Single(rattled.Modifiers);
            Assert.Equal(ModifierSource.Shaken, modifier.Source);
            Assert.Equal(-1, modifier.Value);
        }
    }
}
