using System.Linq;
using Ironwake.Core;
using Xunit;

namespace Ironwake.Core.Tests
{
    /// <summary>
    /// Determinism is the guarantee the whole architecture rests on: server authority,
    /// replays and balance simulation all assume the same RngState reproduces the same dice.
    /// </summary>
    public class RngReproducibilityTests
    {
        [Fact]
        public void SameRngState_ProducesIdenticalRollSequences()
        {
            var state = new RngState(12345UL);

            var first = new Rng(state).RollD6(500);
            var second = new Rng(state).RollD6(500);

            Assert.Equal(first, second);
        }

        [Fact]
        public void SameRngState_ProducesIdenticalSequences_WhateverTheDrawPattern()
        {
            // Drawing 100 dice one at a time must match drawing them in one batch.
            var state = new RngState(999UL);

            var oneAtATime = new Rng(state);
            var singles = Enumerable.Range(0, 100).Select(_ => oneAtATime.D6()).ToArray();

            var batched = new Rng(state).RollD6(100);

            Assert.Equal(batched, singles);
        }

        [Theory]
        [InlineData(0UL)]
        [InlineData(1UL)]
        [InlineData(777UL)]
        [InlineData(12345UL)]
        [InlineData(ulong.MaxValue)]
        public void SameSeed_ProducesIdenticalSequences_AcrossSeeds(ulong seed)
        {
            var a = new Rng(new RngState(seed)).RollD6(200);
            var b = new Rng(new RngState(seed)).RollD6(200);
            Assert.Equal(a, b);
        }

        [Fact]
        public void DifferentSeeds_ProduceDifferentSequences()
        {
            var a = new Rng(new RngState(1UL)).RollD6(200);
            var b = new Rng(new RngState(2UL)).RollD6(200);
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void ResumingFromConsumed_ContinuesTheSameStream()
        {
            // This is what makes replay work: state is stored as (seed, consumed) and the
            // stream is rebuilt from it, so a resumed run must match an uninterrupted one.
            var start = new RngState(4242UL);

            var uninterrupted = new Rng(start).RollD6(20);

            var firstRng = new Rng(start);
            var firstHalf = firstRng.RollD6(8);
            var resumed = new Rng(firstRng.Checkpoint(start));
            var secondHalf = resumed.RollD6(12);

            Assert.Equal(uninterrupted, firstHalf.Concat(secondHalf).ToArray());
        }

        [Fact]
        public void Checkpoint_TracksTheNumberOfDrawsAndKeepsTheSeed()
        {
            var start = new RngState(77UL, consumed: 5);
            var rng = new Rng(start);
            rng.RollD6(11);

            var check = rng.Checkpoint(start);

            Assert.Equal(11, rng.Drawn);
            Assert.Equal(77UL, check.Seed);
            Assert.Equal(16, check.Consumed);
        }

        [Fact]
        public void D6_AlwaysLandsBetweenOneAndSix()
        {
            var rolls = new Rng(new RngState(31337UL)).RollD6(20000);
            Assert.All(rolls, r => Assert.InRange(r, 1, 6));
        }

        [Fact]
        public void D6_ProducesEveryFace()
        {
            var rolls = new Rng(new RngState(8080UL)).RollD6(1000);
            Assert.Equal(6, rolls.Distinct().Count());
        }

        [Fact]
        public void RollD6_Zero_ReturnsEmptyAndDrawsNothing()
        {
            var rng = new Rng(new RngState(5UL));
            Assert.Empty(rng.RollD6(0));
            Assert.Equal(0, rng.Drawn);
        }
    }

    public class RngCountSuccessesTests
    {
        [Fact]
        public void NaturalOne_AlwaysFails_EvenWhenTargetIsOne()
        {
            // Target 1 would otherwise mean "every roll succeeds". Natural 1 still fails.
            Assert.Equal(0, Rng.CountSuccesses(new[] { 1, 1, 1, 1 }, 1));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        public void NaturalOne_AlwaysFails_AtEveryTarget(int target)
        {
            Assert.Equal(0, Rng.CountSuccesses(new[] { 1 }, target));
        }

        [Fact]
        public void NaturalOne_DoesNotSuppressOtherSuccessesInTheSamePool()
        {
            // 1 fails; 5 and 6 pass a 4+ ; 2 and 3 fail on their own merits.
            Assert.Equal(2, Rng.CountSuccesses(new[] { 1, 2, 3, 5, 6 }, 4));
        }

        [Theory]
        [InlineData(new[] { 1, 2, 3, 4, 5, 6 }, 4, 3)]   // 4,5,6 pass
        [InlineData(new[] { 1, 2, 3, 4, 5, 6 }, 2, 5)]   // 2..6 pass, 1 fails
        [InlineData(new[] { 1, 2, 3, 4, 5, 6 }, 6, 1)]   // only 6
        [InlineData(new[] { 6, 6, 6 }, 6, 3)]
        [InlineData(new[] { 2, 2, 2 }, 5, 0)]
        public void CountsDiceThatMeetOrBeatTheTarget(int[] rolls, int target, int expected)
        {
            Assert.Equal(expected, Rng.CountSuccesses(rolls, target));
        }

        [Fact]
        public void EmptyPool_ScoresZero()
        {
            Assert.Equal(0, Rng.CountSuccesses(new int[0], 4));
        }
    }
}
