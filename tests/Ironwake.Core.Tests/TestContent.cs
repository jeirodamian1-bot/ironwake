using System;
using System.Collections.Generic;
using System.Linq;
using Ironwake.Core;

namespace Ironwake.Core.Tests
{
    /// <summary>
    /// A hand-built <see cref="IContentPack"/>. Core's tests deliberately do NOT reference
    /// Ironwake.Content — if they did, a JSON or file-system problem could fail the engine
    /// suite, and the whole point of the split is that Core needs neither.
    /// </summary>
    internal sealed class TestContentPack : IContentPack
    {
        private readonly Dictionary<string, UnitDefinition> _units;
        private readonly Dictionary<string, WeaponDefinition> _weapons;
        private readonly Dictionary<string, FactionDefinition> _factions;

        public string Version { get; }
        public IReadOnlyList<UnitDefinition> AllUnits { get; }

        public TestContentPack(
            IEnumerable<UnitDefinition> units,
            IEnumerable<WeaponDefinition> weapons,
            IEnumerable<FactionDefinition> factions,
            string version = "test-pack")
        {
            Version = version;
            _units = units.ToDictionary(u => u.Id, StringComparer.Ordinal);
            _weapons = weapons.ToDictionary(w => w.Id, StringComparer.Ordinal);
            _factions = factions.ToDictionary(f => f.Id, StringComparer.Ordinal);
            AllUnits = _units.Values.OrderBy(u => u.Id, StringComparer.Ordinal).ToList();
        }

        public UnitDefinition GetUnit(string id) =>
            _units.TryGetValue(id ?? string.Empty, out var u)
                ? u : throw new ContentNotFoundException("unit", id);

        public WeaponDefinition GetWeapon(string id) =>
            _weapons.TryGetValue(id ?? string.Empty, out var w)
                ? w : throw new ContentNotFoundException("weapon", id);

        public FactionDefinition GetFaction(string id) =>
            _factions.TryGetValue(id ?? string.Empty, out var f)
                ? f : throw new ContentNotFoundException("faction", id);

        public bool TryGetUnit(string id, out UnitDefinition unit) =>
            _units.TryGetValue(id ?? string.Empty, out unit);
    }

    /// <summary>Ready-made packs for the engine tests.</summary>
    internal static class TestContent
    {
        /// <summary>Move 40 tenths = 4 hexes. Range 60 tenths = 6 hexes.</summary>
        public const int TestMoveTenths = 40;
        public const int TestRangeTenths = 60;

        /// <summary>
        /// A single generic unit, deliberately statted to the stub engine's original
        /// hardcoded numbers (4-hex move, 6-hex range, 3 attacks, hits on 4+, saves on 5+)
        /// so the engine tests keep asserting the same boundaries as before content landed.
        /// </summary>
        public static IContentPack Basic()
        {
            var weapon = new WeaponDefinition(
                "test_weapon", "Test Weapon", TestRangeTenths,
                attacks: 3, power: 4, armourPiercing: 0, damage: 1);

            var unit = new UnitDefinition(
                "test_unit", "test_faction", "Test Unit", points: 50, modelCount: 1,
                stats: new Statline(TestMoveTenths, accuracy: 4, melee: 4, resilience: 5,
                                    save: 5, wounds: 1, nerve: 6),
                weaponIds: new[] { "test_weapon" });

            return new TestContentPack(
                new[] { unit },
                new[] { weapon },
                new[] { new FactionDefinition("test_faction", "Test Faction") });
        }

        /// <summary>
        /// Definitions for the ids <see cref="SampleGame"/> asks for, mirroring the starter
        /// pack's values. Lets the determinism tests play a whole match with no JSON in sight.
        /// </summary>
        public static IContentPack ForSampleGame()
        {
            // Mirrors the shipped starter pack. Ironwake.Core.Tests deliberately does not
            // reference Ironwake.Content, so these are hand-copied — keep them in step when
            // the pack is retuned, or the engine suite starts testing a fiction.
            var weapons = new[]
            {
                new WeaponDefinition("ash_carbine", "Ash Carbine", 60, 2, 4, 0, 1),
                new WeaponDefinition("warden_maul", "Warden's Maul", 0, 3, 5, 1, 2),
                new WeaponDefinition("cinder_spitter", "Cinder Spitter", 50, 2, 4, 0, 1),
                new WeaponDefinition("cinder_hurler", "Cinder Hurler", 40, 2, 5, 1, 2),
                new WeaponDefinition("brute_cleaver", "Brute Cleaver", 0, 4, 5, 1, 2),
            };

            var units = new[]
            {
                new UnitDefinition("ashguard_lineholder", "ashguard", "Lineholders", 95, 5,
                    new Statline(40, 4, 4, 5, 4, 1, 6), new[] { "ash_carbine" }),
                new UnitDefinition("ashguard_warden", "ashguard", "Warden", 90, 1,
                    new Statline(40, 3, 3, 6, 3, 3, 8), new[] { "ash_carbine", "warden_maul" }),
                new UnitDefinition("cinderkin_raider", "cinderkin", "Raiders", 65, 5,
                    new Statline(60, 4, 4, 4, 6, 1, 5), new[] { "cinder_spitter" }),
                new UnitDefinition("cinderkin_brute", "cinderkin", "Brute", 80, 1,
                    new Statline(50, 4, 3, 5, 5, 3, 6), new[] { "cinder_hurler", "brute_cleaver" }),
            };

            var factions = new[]
            {
                new FactionDefinition("ashguard", "The Ashguard"),
                new FactionDefinition("cinderkin", "The Cinderkin"),
            };

            return new TestContentPack(units, weapons, factions);
        }
    }
}
