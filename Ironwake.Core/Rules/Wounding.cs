using System;

namespace Ironwake.Core
{
    /// <summary>
    /// The wound table: how hard a weapon's Power finds a target's Resilience.
    ///
    /// <code>
    ///   Power >= 2x Resilience  ->  2+
    ///   Power >  Resilience     ->  3+
    ///   Power == Resilience     ->  4+
    ///   Power &lt;  Resilience     ->  5+
    ///   Power &lt;= Resilience / 2  ->  6+
    /// </code>
    ///
    /// This retires the flat 4+ the stub engine used while the rule did not exist. Power and
    /// Resilience have been authored in content since the content layer landed; until now
    /// nothing read them.
    /// </summary>
    public static class Wounding
    {
        /// <summary>
        /// The roll needed to wound. Integer maths throughout — no division is performed on
        /// the way to a comparison.
        /// </summary>
        /// <param name="power">The weapon's Power. Must be positive.</param>
        /// <param name="resilience">The target's Resilience. Must be positive.</param>
        public static int TargetFor(int power, int resilience)
        {
            if (power < 1)
                throw new ArgumentOutOfRangeException(nameof(power), power, "Power must be positive.");
            if (resilience < 1)
                throw new ArgumentOutOfRangeException(
                    nameof(resilience), resilience, "Resilience must be positive.");

            // The bands overlap, so they are tested strongest-first. "Exactly double" belongs
            // to the 2+ band and "exactly half" to the 6+ band — both boundaries are inclusive
            // at the extreme end, which is where an off-by-one would otherwise hide.

            // Power >= 2x Resilience. Multiplication, not division, so nothing truncates.
            if (power >= resilience * 2) return 2;

            if (power > resilience) return 3;
            if (power == resilience) return 4;

            // Power <= Resilience / 2, expressed as 2*Power <= Resilience so that an odd
            // Resilience does not silently round. With Resilience 5, Power 2 qualifies
            // (4 <= 5) but Power 3 does not (6 > 5) — integer division would have made
            // Resilience / 2 equal 2 and reached the same answer here by luck, not by rule.
            if (power * 2 <= resilience) return 6;

            // Power < Resilience but more than half of it.
            return 5;
        }

        /// <summary>
        /// Human-readable form of the comparison, for logs and tooltips:
        /// "Power 4 vs Resilience 5 — wounds on 5+".
        /// </summary>
        public static string Explain(int power, int resilience) =>
            $"Power {power} vs Resilience {resilience} — wounds on {TargetFor(power, resilience)}+";
    }
}
