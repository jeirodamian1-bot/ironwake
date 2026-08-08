using System;

namespace Ironwake.Core
{
    /// <summary>
    /// The single place tabletop measurements become board distances.
    ///
    /// Content authors distances in tenths of an inch (an int, because rules maths never
    /// uses floats). The board thinks in whole hexes. Every conversion between the two goes
    /// through here — do NOT scatter "/ 10" through the codebase, or retuning hex scale
    /// later means hunting every divide in the engine and getting one of them wrong.
    /// </summary>
    public static class Measure
    {
        /// <summary>
        /// Board scale. One hex is one inch, and one inch is ten tenths.
        /// Change this constant to retune the whole game's scale.
        /// </summary>
        public const int TenthsPerHex = 10;

        /// <summary>
        /// Whole hexes covered by a distance in tenths of an inch, rounded down —
        /// a unit cannot move a fraction of a hex, so 45 tenths is 4 hexes, not 4.5.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">If <paramref name="tenths"/> is negative.</exception>
        public static int TenthsToHexes(int tenths)
        {
            if (tenths < 0)
                throw new ArgumentOutOfRangeException(
                    nameof(tenths), tenths, "Distance in tenths cannot be negative.");
            return tenths / TenthsPerHex;
        }

        /// <summary>Tenths of an inch spanned by a whole number of hexes.</summary>
        /// <exception cref="ArgumentOutOfRangeException">If <paramref name="hexes"/> is negative.</exception>
        public static int HexesToTenths(int hexes)
        {
            if (hexes < 0)
                throw new ArgumentOutOfRangeException(
                    nameof(hexes), hexes, "Distance in hexes cannot be negative.");
            return hexes * TenthsPerHex;
        }
    }
}
