using System;

namespace Ironwake.Core
{
    /// <summary>
    /// The RNG position, carried inside GameState so state stays a plain serialisable value.
    /// Dice are reconstructed from (Seed, Consumed) on every execution, which is what makes
    /// replays reproduce exactly.
    ///
    /// NEVER use System.Random or UnityEngine.Random anywhere in Ironwake.Core.
    /// </summary>
    public sealed class RngState
    {
        public ulong Seed { get; }
        public int Consumed { get; }

        public RngState(ulong seed, int consumed = 0)
        {
            Seed = seed;
            Consumed = consumed;
        }

        public RngState Advance(int by) => new RngState(Seed, Consumed + by);
        public override string ToString() => $"seed:{Seed:X} used:{Consumed}";
    }

    /// <summary>
    /// SplitMix64. Chosen because it is tiny, has no state beyond a counter, and is
    /// contractually stable — unlike the BCL's generator, whose algorithm has changed
    /// between runtime versions and would silently break stored replays.
    /// </summary>
    public sealed class Rng
    {
        private ulong _counter;
        private readonly ulong _seed;
        private int _drawn;

        public Rng(RngState state)
        {
            _seed = state.Seed;
            _counter = state.Seed + (ulong)state.Consumed * 0x9E3779B97F4A7C15UL;
            _drawn = 0;
        }

        public int Drawn => _drawn;

        /// <summary>State to store back into GameState after execution.</summary>
        public RngState Checkpoint(RngState original) => original.Advance(_drawn);

        private ulong NextRaw()
        {
            _drawn++;
            _counter += 0x9E3779B97F4A7C15UL;
            ulong z = _counter;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        /// <summary>Uniform in [minInclusive, maxExclusive).</summary>
        public int NextInt(int minInclusive, int maxExclusive)
        {
            if (maxExclusive <= minInclusive)
                throw new ArgumentException("maxExclusive must exceed minInclusive");
            ulong range = (ulong)(maxExclusive - minInclusive);
            return minInclusive + (int)(NextRaw() % range);
        }

        /// <summary>One six-sided die, 1-6.</summary>
        public int D6() => NextInt(1, 7);

        /// <summary>Roll <paramref name="count"/> dice. Order is stable.</summary>
        public int[] RollD6(int count)
        {
            var results = new int[count];
            for (int i = 0; i < count; i++) results[i] = D6();
            return results;
        }

        /// <summary>Count how many dice met or beat the target number.</summary>
        public static int CountSuccesses(int[] rolls, int target)
        {
            int n = 0;
            foreach (var r in rolls)
            {
                if (r == 1) continue;          // natural 1 always fails
                if (r >= target) n++;
            }
            return n;
        }
    }
}
