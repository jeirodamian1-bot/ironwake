using System;
using System.Collections.Generic;

namespace Ironwake.Core
{
    /// <summary>Where a charge can end, and how it got there.</summary>
    public sealed class ChargeApproach
    {
        /// <summary>True if a legal path to a hex adjacent to the target exists.</summary>
        public bool IsPossible { get; }

        /// <summary>The hex the charger ends on. Meaningless when <see cref="IsPossible"/> is false.</summary>
        public Hex Destination { get; }

        /// <summary>Full path including the starting hex, as <see cref="Movement.FindPath"/> returned it.</summary>
        public IReadOnlyList<Hex> Path { get; }

        private ChargeApproach(bool possible, Hex destination, IReadOnlyList<Hex> path)
        {
            IsPossible = possible;
            Destination = destination;
            Path = path ?? Array.Empty<Hex>();
        }

        public static readonly ChargeApproach None =
            new ChargeApproach(false, Hex.Zero, Array.Empty<Hex>());

        public static ChargeApproach To(Hex destination, IReadOnlyList<Hex> path) =>
            new ChargeApproach(true, destination, path);
    }

    /// <summary>
    /// Charging, fighting, and being stuck in combat.
    ///
    /// Shaped like <see cref="Movement"/> and <see cref="LineOfSight"/>: one predicate each,
    /// shared by validation, LegalActions and the client-facing queries, so they cannot drift.
    /// Pathfinding is NOT reimplemented here — a charge is a move that happens to end next to
    /// somebody, so it goes through <see cref="Movement.FindPath"/> like any other move.
    /// </summary>
    public static class Melee
    {
        /// <summary>Two units are in melee when they stand on adjacent hexes.</summary>
        public static bool AreAdjacent(UnitState a, UnitState b) =>
            a != null && b != null && a.Position.DistanceTo(b.Position) == 1;

        /// <summary>True if any living enemy of <paramref name="unit"/> stands adjacent to it.</summary>
        public static bool HasAdjacentEnemy(GameState state, UnitState unit)
        {
            if (unit == null || !unit.IsAlive) return false;

            foreach (var other in state.Units)
            {
                if (!other.IsAlive || other.Owner == unit.Owner) continue;
                if (AreAdjacent(unit, other)) return true;
            }
            return false;
        }

        /// <summary>
        /// Where a charge from <paramref name="charger"/> onto <paramref name="target"/> would
        /// end, or <see cref="ChargeApproach.None"/> if it cannot reach.
        ///
        /// DETERMINISM: the target's six neighbours are considered in <see cref="Hex.Directions"/>
        /// order (E, NE, NW, W, SW, SE) and the SHORTEST reachable path wins; ties go to the
        /// lowest direction index. So two runs always pick the same approach hex, and a client
        /// previewing a charge draws the route the engine will actually take.
        /// </summary>
        public static ChargeApproach FindApproach(
            GameState state, UnitId charger, UnitId target, int allowanceHexes)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));

            var mover = state.GetUnit(charger);
            var victim = state.GetUnit(target);
            if (mover == null || victim == null || !victim.IsAlive) return ChargeApproach.None;

            ChargeApproach best = ChargeApproach.None;
            int bestLength = int.MaxValue;

            for (int direction = 0; direction < 6; direction++)
            {
                var candidate = victim.Position.Neighbour(direction);

                // Already standing there: no approach needed, but a charge still has to move,
                // so an adjacent unit fights rather than charges.
                if (candidate == mover.Position) continue;

                if (Movement.BlockingReason(state, charger, candidate) != HexBlock.None) continue;

                var path = Movement.FindPath(state, charger, candidate, allowanceHexes);
                if (path.Count < 2) continue;

                if (path.Count < bestLength)
                {
                    bestLength = path.Count;
                    best = ChargeApproach.To(candidate, path);
                }
            }

            return best;
        }

        /// <summary>
        /// The unit's melee weapon: the first it carries with no range. Null if it has none,
        /// which means it cannot Fight — though it may still charge, see the ruling on
        /// <see cref="StubEngine"/>'s charge handling.
        /// </summary>
        public static WeaponDefinition MeleeWeaponOf(IContentPack content, UnitState unit)
        {
            if (content == null || unit == null) return null;

            var definition = content.GetUnit(unit.DefinitionId);
            foreach (var weaponId in definition.WeaponIds)
            {
                var weapon = content.GetWeapon(weaponId);
                if (weapon.Range <= 0) return weapon;
            }
            return null;
        }

        /// <summary>
        /// Recomputes <see cref="StatusKind.Engaged"/> for every living unit from adjacency.
        ///
        /// Engagement is derived, never accumulated: a unit is Engaged exactly while an enemy
        /// stands next to it. Running this after anything that moves or kills a unit is what
        /// makes "Engaged clears when no enemy is adjacent" true without anyone remembering to
        /// clear it. Dead units lose the status so a corpse never pins anybody.
        /// </summary>
        public static GameState RefreshEngagement(GameState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));

            var updated = new List<UnitState>(state.Units.Count);
            bool changed = false;

            foreach (var unit in state.Units)
            {
                bool shouldBeEngaged = unit.IsAlive && HasAdjacentEnemy(state, unit);
                bool isEngaged = unit.HasStatus(StatusKind.Engaged);

                if (shouldBeEngaged == isEngaged) { updated.Add(unit); continue; }

                updated.Add(shouldBeEngaged
                    ? unit.WithStatus(StatusKind.Engaged)
                    : unit.WithoutStatus(StatusKind.Engaged));
                changed = true;
            }

            return changed ? state.With(units: updated) : state;
        }

        /// <summary>
        /// Cover does NOT apply in melee.
        ///
        /// A RULING, not an oversight: cover represents shooting at someone behind a wall.
        /// Once you are close enough to swing at them, the wall is not between you — you are
        /// both standing in it. Terrain that matters in melee (difficult ground, elevation
        /// advantage) is a separate rule nobody has written yet.
        /// </summary>
        public const bool CoverAppliesInMelee = false;
    }
}
