using System;
using System.Linq;
using Ironwake.Core;
using Xunit;

namespace Ironwake.Content.Tests
{
    /// <summary>
    /// Guards the content that actually ships. These fail the moment authored content and
    /// the validator disagree, which is the point — content bugs should be build failures,
    /// not something discovered mid-match.
    /// </summary>
    public class StarterPackTests
    {
        private static IContentPack Pack() => StarterPack.Load();

        [Fact]
        public void TheStarterPackLoads()
        {
            var pack = Pack();

            Assert.Equal("starter-0.1", pack.Version);
            Assert.NotEmpty(pack.AllUnits);
        }

        [Fact]
        public void ItHasTwoFactionsOfFiveUnitsEach()
        {
            var pack = (JsonContentPack)Pack();

            Assert.Equal(2, pack.AllFactions.Count);
            Assert.Equal(10, pack.AllUnits.Count);

            foreach (var faction in pack.AllFactions)
                Assert.Equal(5, faction.UnitIds.Count);
        }

        [Fact]
        public void EveryStarterUnitLoadsAndRoundTrips()
        {
            var pack = Pack();

            foreach (var unit in pack.AllUnits)
            {
                // Fetching by id must return the very same definition the list exposed.
                var fetched = pack.GetUnit(unit.Id);
                Assert.Same(unit, fetched);

                Assert.False(string.IsNullOrWhiteSpace(unit.DisplayName), $"{unit.Id} has no display name");
                Assert.True(unit.Points > 0, $"{unit.Id} has no points cost");
                Assert.InRange(unit.ModelCount, 1, 20);
                Assert.NotNull(unit.Stats);
                Assert.True(unit.Stats.Move > 0, $"{unit.Id} cannot move");

                Assert.True(pack.TryGetUnit(unit.Id, out var viaTry));
                Assert.Same(unit, viaTry);
            }
        }

        [Fact]
        public void EveryUnitBelongsToARealFactionThatClaimsItBack()
        {
            var pack = Pack();

            foreach (var unit in pack.AllUnits)
            {
                var faction = pack.GetFaction(unit.FactionId);   // throws if missing
                Assert.Contains(unit.Id, faction.UnitIds);
            }
        }

        [Fact]
        public void EveryWeaponReferenceResolves()
        {
            var pack = Pack();

            foreach (var unit in pack.AllUnits)
            {
                Assert.NotEmpty(unit.WeaponIds);
                foreach (var weaponId in unit.WeaponIds)
                    Assert.NotNull(pack.GetWeapon(weaponId));    // throws if missing
            }
        }

        [Fact]
        public void ThereAreEightWeapons()
        {
            // Six originals plus cinder_hurler and anvil_pistol, added so the two melee
            // specialists are not inert while melee remains unimplemented.
            Assert.Equal(8, ((JsonContentPack)Pack()).AllWeapons.Count);
        }

        [Fact]
        public void EveryUnitsPrimaryWeaponCanActuallyBeFired()
        {
            // A unit whose first weapon has range 0 cannot shoot, and melee does not exist —
            // it would be able to do nothing at all. This is what caught ashguard_anvilborn.
            var pack = (JsonContentPack)Pack();

            foreach (var unit in pack.AllUnits)
            {
                var primary = pack.GetWeapon(unit.WeaponIds[0]);
                Assert.True(primary.Range > 0,
                    $"{unit.Id}'s primary weapon {primary.Id} is melee, leaving it inert");
            }
        }

        // ---- deterministic ordering -------------------------------------------

        [Fact]
        public void AllUnitsIsSortedById()
        {
            var ids = Pack().AllUnits.Select(u => u.Id).ToList();

            Assert.Equal(ids.OrderBy(i => i, StringComparer.Ordinal).ToList(), ids);
        }

        [Fact]
        public void AllUnitsOrderingIsStableAcrossLoads()
        {
            // Two independent loads must agree. A raw Dictionary enumeration would not
            // reliably fail this, which is exactly why the loader sorts explicitly.
            var first = Pack().AllUnits.Select(u => u.Id).ToList();
            var second = Pack().AllUnits.Select(u => u.Id).ToList();
            var third = Pack().AllUnits.Select(u => u.Id).ToList();

            Assert.Equal(first, second);
            Assert.Equal(first, third);
        }

        [Fact]
        public void FactionUnitListsAreSortedToo()
        {
            var pack = (JsonContentPack)Pack();

            foreach (var faction in pack.AllFactions)
            {
                Assert.Equal(
                    faction.UnitIds.OrderBy(i => i, StringComparer.Ordinal).ToList(),
                    faction.UnitIds.ToList());
            }
        }

        // ---- missing ids -------------------------------------------------------

        [Fact]
        public void MissingUnitIdThrowsContentNotFoundNamingTheId()
        {
            var ex = Assert.Throws<ContentNotFoundException>(() => Pack().GetUnit("ashguard_nonexistent"));

            Assert.Equal("ashguard_nonexistent", ex.Id);
            Assert.Equal("unit", ex.Kind);
            Assert.Contains("ashguard_nonexistent", ex.Message);
        }

        [Fact]
        public void MissingWeaponIdThrowsContentNotFoundNamingTheId()
        {
            var ex = Assert.Throws<ContentNotFoundException>(() => Pack().GetWeapon("plasma_nonsense"));

            Assert.Equal("plasma_nonsense", ex.Id);
            Assert.Equal("weapon", ex.Kind);
            Assert.Contains("plasma_nonsense", ex.Message);
        }

        [Fact]
        public void MissingFactionIdThrowsContentNotFoundNamingTheId()
        {
            var ex = Assert.Throws<ContentNotFoundException>(() => Pack().GetFaction("no_such_faction"));

            Assert.Equal("no_such_faction", ex.Id);
            Assert.Equal("faction", ex.Kind);
            Assert.Contains("no_such_faction", ex.Message);
        }

        [Fact]
        public void LookupsNeverReturnNullInsteadOfThrowing()
        {
            var pack = Pack();

            Assert.Throws<ContentNotFoundException>(() => pack.GetUnit(null));
            Assert.Throws<ContentNotFoundException>(() => pack.GetUnit(""));
        }

        // ---- the units SampleGame depends on -----------------------------------

        [Theory]
        [InlineData("ashguard_lineholder")]
        [InlineData("ashguard_warden")]
        [InlineData("cinderkin_raider")]
        [InlineData("cinderkin_brute")]
        public void TheUnitsSampleGameAsksForExist(string id)
        {
            // SampleGame names these directly; losing one breaks the harness and the client's
            // first-run experience, so pin them.
            Assert.NotNull(Pack().GetUnit(id));
        }

        [Fact]
        public void TheSampleGameBoardBuildsFromTheStarterPack()
        {
            var pack = Pack();

            var state = SampleGame.Create(pack);

            Assert.Equal(6, state.Units.Count);
            Assert.Equal(pack.Version, state.ContentVersion);
            Assert.All(state.Units, u => Assert.True(u.IsAlive));
        }

        [Fact]
        public void AStubMatchPlaysToCompletionOnRealContent()
        {
            // The Core suite proves this against a hand-built pack; this proves the authored
            // numbers are playable too — e.g. that nothing has a zero-hex move.
            var pack = Pack();
            IGameEngine engine = new StubEngine(pack);
            var state = SampleGame.Create(pack, 777UL);

            int guard = 0;
            while (state.Phase != PhaseKind.Complete && guard++ < 500)
            {
                var legal = engine.LegalActions(state, state.ActivePlayer);
                if (legal.Count == 0) break;

                // Same policy as the console harness. Activating explicitly matters: the
                // fallback would otherwise be PassActivation, and the match would never start.
                var choice = legal.FirstOrDefault(a => a is ShootAt)
                          ?? legal.FirstOrDefault(a => a is ActivateUnit)
                          ?? legal.OfType<MoveUnit>().OrderByDescending(m => m.Path.Count).FirstOrDefault()
                          ?? legal[legal.Count - 1];

                var result = engine.Execute(state, choice);
                state = result.NextState;
                if (result.IsTerminal) break;
            }

            Assert.Equal(PhaseKind.Complete, state.Phase);
            Assert.True(state.Rng.Consumed > 0, "the match finished without rolling a single die");
        }

        // ---- faction feel ------------------------------------------------------

        [Fact]
        public void AshguardAreSlowerAndTougherThanCinderkin()
        {
            // The two factions are supposed to feel different. This is a design assertion,
            // not a balance one: it fails if a retune accidentally flattens them together.
            var pack = (JsonContentPack)Pack();

            var ashguard = pack.AllUnits.Where(u => u.FactionId == "ashguard").ToList();
            var cinderkin = pack.AllUnits.Where(u => u.FactionId == "cinderkin").ToList();

            Assert.True(ashguard.Average(u => u.Stats.Move) < cinderkin.Average(u => u.Stats.Move),
                "Ashguard should be the slower faction");
            Assert.True(ashguard.Average(u => u.Stats.Resilience) > cinderkin.Average(u => u.Stats.Resilience),
                "Ashguard should be the tougher faction");

            // Lower Save is better, so the tougher faction should have the lower average.
            Assert.True(ashguard.Average(u => u.Stats.Save) < cinderkin.Average(u => u.Stats.Save),
                "Ashguard should have the better armour");
        }
    }
}
