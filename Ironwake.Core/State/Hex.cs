using System;
using System.Collections.Generic;

namespace Ironwake.Core
{
    /// <summary>
    /// Which way a line running exactly along a hex edge should resolve.
    ///
    /// An enum rather than a raw epsilon so that callers never handle a double — floating
    /// point stays sealed inside <see cref="Hex"/>, where it is the one permitted exception.
    /// </summary>
    public enum LineTieBreak
    {
        /// <summary>The historical default, matching <see cref="Hex.LineTo(Hex)"/>.</summary>
        Positive = 0,

        /// <summary>The opposite side of an ambiguous edge.</summary>
        Negative = 1,
    }

    /// <summary>
    /// Axial hex coordinate (pointy-top layout).
    /// This is THE coordinate system for the whole project.
    /// The client converts screen taps to Hex and sends Hex to the engine.
    /// The client must never invent its own tile coordinates.
    /// </summary>
    public readonly struct Hex : IEquatable<Hex>
    {
        public int Q { get; }
        public int R { get; }

        public Hex(int q, int r) { Q = q; R = r; }

        /// <summary>Third cube coordinate, derived. Always Q + R + S == 0.</summary>
        public int S => -Q - R;

        public static readonly Hex Zero = new Hex(0, 0);

        /// <summary>The six axial direction vectors, in clockwise order from East.</summary>
        public static readonly Hex[] Directions =
        {
            new Hex( 1,  0), // 0 - E
            new Hex( 1, -1), // 1 - NE
            new Hex( 0, -1), // 2 - NW
            new Hex(-1,  0), // 3 - W
            new Hex(-1,  1), // 4 - SW
            new Hex( 0,  1), // 5 - SE
        };

        public static Hex operator +(Hex a, Hex b) => new Hex(a.Q + b.Q, a.R + b.R);
        public static Hex operator -(Hex a, Hex b) => new Hex(a.Q - b.Q, a.R - b.R);
        public static Hex operator *(Hex a, int k) => new Hex(a.Q * k, a.R * k);

        public Hex Neighbour(int direction) => this + Directions[((direction % 6) + 6) % 6];

        /// <summary>Hex distance in tiles. Never use Euclidean distance for rules.</summary>
        public int DistanceTo(Hex other)
        {
            int dq = Math.Abs(Q - other.Q);
            int dr = Math.Abs(R - other.R);
            int ds = Math.Abs(S - other.S);
            return (dq + dr + ds) / 2;
        }

        /// <summary>All hexes within <paramref name="radius"/> tiles, including this one.</summary>
        public IEnumerable<Hex> WithinRange(int radius)
        {
            for (int dq = -radius; dq <= radius; dq++)
            {
                int lo = Math.Max(-radius, -dq - radius);
                int hi = Math.Min(radius, -dq + radius);
                for (int dr = lo; dr <= hi; dr++)
                    yield return new Hex(Q + dq, R + dr);
            }
        }

        /// <summary>
        /// Hexes forming a straight line from this hex to <paramref name="other"/>, inclusive.
        /// Used for line-of-sight checks. The epsilon nudge breaks edge ties consistently.
        /// </summary>
        /// <remarks>
        /// NOTE: this is the one place doubles appear in rules-adjacent code. Values here are
        /// small integers and Math.Round is well-defined, so it is safe in practice — but if a
        /// replay ever desyncs between platforms, check here first. An integer supercover
        /// line algorithm is the fallback if it becomes a problem.
        /// </remarks>
        public IReadOnlyList<Hex> LineTo(Hex other) => LineTo(other, LineTieBreak.Positive);

        /// <summary>
        /// The nudge used to break edge ties. Small enough not to disturb an unambiguous line,
        /// large enough to dominate floating point noise. Private: the epsilon is nobody
        /// else's business, and keeping it here is what stops doubles leaking into rules code.
        /// </summary>
        private const double TieBreakEpsilon = 1e-6;

        /// <summary>
        /// As <see cref="LineTo(Hex)"/>, but choosing which way an ambiguous line resolves.
        /// </summary>
        /// <param name="tieBreak">
        /// A line running exactly along a hex edge is genuinely ambiguous — two different hex
        /// sequences are equally correct — and this decides which one you get. Callers that
        /// must not depend on an arbitrary choice (line of sight, for one) trace it both ways
        /// and combine the answers rather than pretending one side is authoritative.
        /// </param>
        public IReadOnlyList<Hex> LineTo(Hex other, LineTieBreak tieBreak)
        {
            int n = DistanceTo(other);
            var result = new List<Hex>(n + 1);
            if (n == 0) { result.Add(this); return result; }

            double nudge = tieBreak == LineTieBreak.Positive ? TieBreakEpsilon : -TieBreakEpsilon;

            for (int i = 0; i <= n; i++)
            {
                double t = (double)i / n;
                double q = Q + (other.Q - Q) * t + nudge;
                double r = R + (other.R - R) * t + nudge;
                result.Add(RoundToHex(q, r));
            }
            return result;
        }

        private static Hex RoundToHex(double q, double r)
        {
            double s = -q - r;
            int rq = (int)Math.Round(q);
            int rr = (int)Math.Round(r);
            int rs = (int)Math.Round(s);

            double dq = Math.Abs(rq - q);
            double dr = Math.Abs(rr - r);
            double ds = Math.Abs(rs - s);

            if (dq > dr && dq > ds) rq = -rr - rs;
            else if (dr > ds) rr = -rq - rs;

            return new Hex(rq, rr);
        }

        // ---- CLIENT HELPERS -------------------------------------------------
        // These are the ONLY place floating point is allowed to touch hexes.
        // They are for rendering and input only. Never use them in rules code.

        /// <summary>Hex centre in world units, pointy-top layout. Rendering only.</summary>
        public void ToPixel(double size, out double x, out double y)
        {
            x = size * (Math.Sqrt(3.0) * Q + Math.Sqrt(3.0) / 2.0 * R);
            y = size * (3.0 / 2.0 * R);
        }

        /// <summary>Convert a world position back to the containing hex. Input only.</summary>
        public static Hex FromPixel(double x, double y, double size)
        {
            double q = (Math.Sqrt(3.0) / 3.0 * x - 1.0 / 3.0 * y) / size;
            double r = (2.0 / 3.0 * y) / size;
            return RoundToHex(q, r);
        }

        // ---- EQUALITY -------------------------------------------------------

        public bool Equals(Hex other) => Q == other.Q && R == other.R;
        public override bool Equals(object obj) => obj is Hex h && Equals(h);
        public override int GetHashCode() => unchecked((Q * 397) ^ R);
        public static bool operator ==(Hex a, Hex b) => a.Equals(b);
        public static bool operator !=(Hex a, Hex b) => !a.Equals(b);
        public override string ToString() => $"({Q},{R})";
    }
}
