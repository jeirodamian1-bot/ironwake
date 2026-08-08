using System;
using System.Collections.Generic;

namespace Ironwake.Core
{
    /// <summary>
    /// A unit's statline. Every value is an <see cref="int"/> — no floats in rules maths.
    ///
    /// Roll-target stats (Accuracy, Melee, Save) are "X+" target numbers: lower is better,
    /// and 7 conventionally means "cannot" (no save, cannot hit).
    /// </summary>
    public sealed class Statline
    {
        /// <summary>Movement allowance in tenths of an inch. Convert with <see cref="Measure"/>.</summary>
        public int Move { get; }

        /// <summary>Ranged to-hit target, "X+".</summary>
        public int Accuracy { get; }

        /// <summary>Melee to-hit target, "X+".</summary>
        public int Melee { get; }

        /// <summary>How hard the unit is to wound. Higher is tougher.</summary>
        public int Resilience { get; }

        /// <summary>Armour save target, "X+". 7 means no save.</summary>
        public int Save { get; }

        /// <summary>Wounds per model.</summary>
        public int Wounds { get; }

        /// <summary>Morale. Higher holds the line longer.</summary>
        public int Nerve { get; }

        public Statline(int move, int accuracy, int melee, int resilience, int save, int wounds, int nerve)
        {
            Move = move;
            Accuracy = accuracy;
            Melee = melee;
            Resilience = resilience;
            Save = save;
            Wounds = wounds;
            Nerve = nerve;
        }

        /// <summary>
        /// Movement allowance in whole hexes. Routes through <see cref="Measure.TenthsToHexes"/>
        /// so the tenths-per-hex conversion stays in exactly one place.
        /// </summary>
        public int MoveInHexes => Measure.TenthsToHexes(Move);
    }

    /// <summary>
    /// One unit entry from the content pack. Immutable: the pack is loaded once and shared.
    /// The engine holds no statlines of its own — it reads them from here.
    /// </summary>
    public sealed class UnitDefinition
    {
        public string Id { get; }
        public string FactionId { get; }
        public string DisplayName { get; }

        /// <summary>Points cost for list building.</summary>
        public int Points { get; }

        /// <summary>How many models the unit fields at full strength.</summary>
        public int ModelCount { get; }

        public Statline Stats { get; }

        /// <summary>Weapons this unit carries, in authored order. The first is its primary.</summary>
        public IReadOnlyList<string> WeaponIds { get; }

        public IReadOnlyList<string> AbilityIds { get; }

        /// <summary>Free-form tags rules can key off, e.g. "infantry", "elite".</summary>
        public IReadOnlyList<string> Keywords { get; }

        public UnitDefinition(
            string id, string factionId, string displayName, int points, int modelCount,
            Statline stats,
            IReadOnlyList<string> weaponIds = null,
            IReadOnlyList<string> abilityIds = null,
            IReadOnlyList<string> keywords = null)
        {
            Id = id;
            FactionId = factionId;
            DisplayName = displayName;
            Points = points;
            ModelCount = modelCount;
            Stats = stats;
            WeaponIds = Freeze(weaponIds);
            AbilityIds = Freeze(abilityIds);
            Keywords = Freeze(keywords);
        }

        /// <summary>Copy on the way in so a caller holding the original list cannot mutate us.</summary>
        internal static IReadOnlyList<string> Freeze(IReadOnlyList<string> source)
        {
            if (source == null || source.Count == 0) return Array.Empty<string>();
            var copy = new string[source.Count];
            for (int i = 0; i < source.Count; i++) copy[i] = source[i];
            return copy;
        }

        public override string ToString() => $"{Id} ({DisplayName})";
    }

    /// <summary>One weapon entry from the content pack.</summary>
    public sealed class WeaponDefinition
    {
        public string Id { get; }
        public string DisplayName { get; }

        /// <summary>Range in tenths of an inch. Convert with <see cref="Measure"/>.</summary>
        public int Range { get; }

        /// <summary>Attacks generated per model carrying this weapon.</summary>
        public int Attacks { get; }

        /// <summary>Wounding power. Higher wounds more easily.</summary>
        public int Power { get; }

        /// <summary>How much of the target's save this weapon negates.</summary>
        public int ArmourPiercing { get; }

        /// <summary>Wounds inflicted per unsaved hit.</summary>
        public int Damage { get; }

        public IReadOnlyList<string> Keywords { get; }

        public WeaponDefinition(
            string id, string displayName, int range, int attacks, int power,
            int armourPiercing, int damage, IReadOnlyList<string> keywords = null)
        {
            Id = id;
            DisplayName = displayName;
            Range = range;
            Attacks = attacks;
            Power = power;
            ArmourPiercing = armourPiercing;
            Damage = damage;
            Keywords = UnitDefinition.Freeze(keywords);
        }

        /// <summary>Range in whole hexes, via the single conversion constant.</summary>
        public int RangeInHexes => Measure.TenthsToHexes(Range);

        public override string ToString() => $"{Id} ({DisplayName})";
    }

    /// <summary>One faction entry from the content pack.</summary>
    public sealed class FactionDefinition
    {
        public string Id { get; }
        public string DisplayName { get; }

        /// <summary>Units belonging to this faction, sorted by id when built by a loader.</summary>
        public IReadOnlyList<string> UnitIds { get; }

        public FactionDefinition(string id, string displayName, IReadOnlyList<string> unitIds = null)
        {
            Id = id;
            DisplayName = displayName;
            UnitIds = UnitDefinition.Freeze(unitIds);
        }

        public override string ToString() => $"{Id} ({DisplayName})";
    }
}
