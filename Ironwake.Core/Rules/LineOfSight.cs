using System;
using System.Collections.Generic;

namespace Ironwake.Core
{
    /// <summary>
    /// The outcome of tracing sight between two hexes: whether it is blocked, what blocked it,
    /// and whether the target has cover.
    /// </summary>
    public sealed class LosResult
    {
        /// <summary>True if nothing can be shot along this line.</summary>
        public bool IsBlocked { get; }

        /// <summary>The hex that stopped the trace, or null when sight is clear.</summary>
        public Hex? BlockingHex { get; }

        /// <summary>
        /// True if the target benefits from cover. Standing on Cover or Obscuring grants it —
        /// but a shooter on Elevated is looking down and ignores it.
        /// </summary>
        public bool TargetInCover { get; }

        /// <summary>True if the shooter is on Elevated ground, which is why the other two read as they do.</summary>
        public bool ShooterElevated { get; }

        private LosResult(bool isBlocked, Hex? blockingHex, bool targetInCover, bool shooterElevated)
        {
            IsBlocked = isBlocked;
            BlockingHex = blockingHex;
            TargetInCover = targetInCover;
            ShooterElevated = shooterElevated;
        }

        public static LosResult Clear(bool targetInCover, bool shooterElevated) =>
            new LosResult(false, null, targetInCover, shooterElevated);

        public static LosResult Blocked(Hex blockingHex, bool targetInCover, bool shooterElevated) =>
            new LosResult(true, blockingHex, targetInCover, shooterElevated);

        /// <summary>No line at all — a unit that does not exist cannot see or be seen.</summary>
        public static readonly LosResult NoSuchUnit = new LosResult(true, null, false, false);

        public override string ToString() =>
            IsBlocked
                ? $"Blocked{(BlockingHex.HasValue ? " at " + BlockingHex.Value : string.Empty)}"
                : $"Clear{(TargetInCover ? " (target in cover)" : string.Empty)}";
    }

    /// <summary>
    /// Whether one hex can see another, and whether the far end counts as covered.
    ///
    /// Shaped like <see cref="Movement"/> on purpose: one predicate that validation,
    /// LegalActions and the client-facing query all call, so they cannot drift apart.
    ///
    /// The rules, deliberately narrow for now:
    ///   - Only Obscuring terrain blocks, and only strictly BETWEEN the two ends.
    ///   - Neither end ever blocks. A unit standing in Obscuring is visible, and in cover.
    ///   - A shooter on Elevated sees over Obscuring entirely, and ignores the target's cover.
    ///   - Impassable does NOT block sight. Unwalkable and opaque are different properties.
    ///   - Units do NOT block sight. Making them block is a separate decision, not an
    ///     assumption to bake in silently here.
    /// </summary>
    public static class LineOfSight
    {
        /// <summary>
        /// Trace sight from one hex to another.
        ///
        /// A line running exactly along a hex edge passes through two equally valid sequences
        /// of hexes, and <see cref="Hex.LineTo(Hex)"/> resolves that with an epsilon nudge —
        /// so sight would depend on an arbitrary tie-break. Instead both candidate lines are
        /// traced, and sight counts as blocked only when BOTH are blocked.
        ///
        /// That is a deliberate ruling, not an implementation detail: LOS is generous, and on
        /// a genuinely ambiguous edge the shooter gets the benefit of the doubt. The reverse
        /// convention (blocked if either line is blocked) would be equally consistent; this one
        /// was chosen because a player who can see a clean line to the target and is told "no
        /// line of sight" feels cheated, whereas the opposite reads as a lucky angle.
        /// </summary>
        public static LosResult Trace(GameState state, Hex from, Hex to)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));

            bool shooterElevated = state.Board.TerrainAt(from) == TerrainKind.Elevated;

            // Cover is a property of where the target stands, regardless of what is in between.
            bool targetInCover = !shooterElevated && GrantsCover(state.Board.TerrainAt(to));

            // Nothing can stand between a hex and itself, or between two neighbours.
            if (from == to || from.DistanceTo(to) <= 1)
                return LosResult.Clear(targetInCover, shooterElevated);

            // An elevated shooter sees over everything, so there is nothing to trace.
            if (shooterElevated)
                return LosResult.Clear(targetInCover, shooterElevated);

            var primary = FirstBlocker(state, from, to, LineTieBreak.Positive);
            if (!primary.HasValue)
                return LosResult.Clear(targetInCover, shooterElevated);

            var alternate = FirstBlocker(state, from, to, LineTieBreak.Negative);
            if (!alternate.HasValue)
                return LosResult.Clear(targetInCover, shooterElevated);

            // Both candidate lines are blocked. Report the primary line's blocker so the
            // answer is stable and matches what Hex.LineTo would draw.
            return LosResult.Blocked(primary.Value, targetInCover, shooterElevated);
        }

        /// <summary>Convenience for callers that only care whether the shot is possible.</summary>
        public static bool HasLineOfSight(GameState state, Hex from, Hex to) =>
            !Trace(state, from, to).IsBlocked;

        /// <summary>
        /// The first hex that stops sight along one candidate line, or null if it is clear.
        /// </summary>
        private static Hex? FirstBlocker(GameState state, Hex from, Hex to, LineTieBreak tieBreak)
        {
            var line = from.LineTo(to, tieBreak);

            // Skip both ends: the shooter's own hex and the target's never block.
            for (int i = 1; i < line.Count - 1; i++)
            {
                var hex = line[i];
                if (hex == from || hex == to) continue;
                if (BlocksSight(state, hex)) return hex;
            }

            return null;
        }

        /// <summary>
        /// The single blocking rule. Only Obscuring stops sight.
        ///
        /// Impassable is deliberately absent — a chasm or a wall of spikes is unwalkable but
        /// perfectly transparent. Elevated is absent for the same reason: high ground is
        /// something to see FROM, never something that blocks. Units are absent because
        /// unit-blocks-unit is a rules decision nobody has made yet.
        /// </summary>
        public static bool BlocksSight(GameState state, Hex hex) =>
            state.Board.TerrainAt(hex) == TerrainKind.Obscuring;

        /// <summary>Terrain that shelters whoever stands on it.</summary>
        public static bool GrantsCover(TerrainKind terrain) =>
            terrain == TerrainKind.Cover || terrain == TerrainKind.Obscuring;

        /// <summary>
        /// Every hex visible from a given hex within a range. Offered for the same reason as
        /// <see cref="Movement.ReachableFrom"/>: so the client can shade a firing arc with one
        /// call instead of asking about each hex in turn.
        /// </summary>
        /// <remarks>Enumeration order of the result is not meaningful — sort before rendering.</remarks>
        public static IReadOnlyList<Hex> VisibleFrom(GameState state, Hex from, int rangeHexes)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));

            var visible = new List<Hex>();
            foreach (var hex in from.WithinRange(rangeHexes))
            {
                if (hex == from) continue;
                if (!state.Board.Contains(hex)) continue;
                if (HasLineOfSight(state, from, hex)) visible.Add(hex);
            }
            return visible;
        }
    }
}
