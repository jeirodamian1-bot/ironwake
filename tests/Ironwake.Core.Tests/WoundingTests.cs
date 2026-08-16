using System;
using System.Collections.Generic;
using System.Linq;
using Ironwake.Core;
using Xunit;

namespace Ironwake.Core.Tests
{
    /// <summary>
    /// The wound table. The bands overlap, so the boundaries are where an off-by-one hides —
    /// each one is pinned from both sides.
    /// </summary>
    public class WoundingTests
    {
        [Theory]
        // Power >= 2x Resilience -> 2+
        [InlineData(10, 5, 2)]
        [InlineData(12, 5, 2)]
        // Power > Resilience -> 3+
        [InlineData(6, 5, 3)]
        [InlineData(9, 5, 3)]
        // Power == Resilience -> 4+
        [InlineData(5, 5, 4)]
        // Power < Resilience but more than half -> 5+
        [InlineData(4, 5, 5)]
        [InlineData(3, 5, 5)]
        // Power <= Resilience / 2 -> 6+
        [InlineData(2, 5, 6)]
        [InlineData(1, 5, 6)]
        public void TheTableReadsAsWritten(int power, int resilience, int expected)
        {
            Assert.Equal(expected, Wounding.TargetFor(power, resilience));
        }

        // ---- the two boundaries that hide off-by-ones ---------------------------

        [Theory]
        [InlineData(2, 1)]    // exactly double
        [InlineData(4, 2)]
        [InlineData(8, 4)]
        [InlineData(10, 5)]
        [InlineData(20, 10)]
        public void ExactlyDoubleWoundsOnTwo(int power, int resilience)
        {
            // The "2x" band is inclusive at its lower edge: exactly double is already 2+.
            Assert.Equal(2, Wounding.TargetFor(power, resilience));

            // One point short of double drops to 3+ — but only where 3+ exists at all. At
            // Resilience 1, double is 2 and one short is 1, which is EQUAL rather than
            // merely greater, so it lands on 4+. See TheBandsCollapseAtLowResilience.
            if (power - 1 > resilience)
                Assert.Equal(3, Wounding.TargetFor(power - 1, resilience));
        }

        [Theory]
        [InlineData(1, 2)]    // exactly half
        [InlineData(2, 4)]
        [InlineData(3, 6)]
        [InlineData(5, 10)]
        public void ExactlyHalfWoundsOnSix(int power, int resilience)
        {
            // The "half" band is inclusive at its upper edge: exactly half is already 6+.
            Assert.Equal(6, Wounding.TargetFor(power, resilience));

            // One point above half rises to 5+ — but only where 5+ exists. At Resilience 2,
            // half is 1 and one above is 2, which is EQUAL rather than merely less, so it
            // lands on 4+. See TheBandsCollapseAtLowResilience.
            if (power + 1 < resilience)
                Assert.Equal(5, Wounding.TargetFor(power + 1, resilience));
        }

        [Fact]
        public void TheBandsCollapseAtLowResilience()
        {
            // A real property of the table, asserted rather than left to be discovered: when
            // Resilience is very small the bands sit adjacent and some become unreachable.
            // Nothing is wrong with this, but a balance pass should know it.

            // Resilience 1: half rounds below 1 and double is 2, so only 4+ and 2+ exist.
            Assert.Equal(4, Wounding.TargetFor(1, 1));
            Assert.Equal(2, Wounding.TargetFor(2, 1));
            var againstOne = Enumerable.Range(1, 20).Select(p => Wounding.TargetFor(p, 1)).Distinct();
            Assert.Equal(new[] { 4, 2 }, againstOne);

            // Resilience 2: 5+ is unreachable — half is 1 (6+) and the next step up is equal (4+).
            var againstTwo = Enumerable.Range(1, 20).Select(p => Wounding.TargetFor(p, 2)).Distinct().OrderBy(t => t);
            Assert.Equal(new[] { 2, 3, 4, 6 }, againstTwo);

            // From Resilience 3 upward every band is reachable.
            for (int resilience = 3; resilience <= 10; resilience++)
            {
                var reachable = Enumerable.Range(1, 40)
                    .Select(p => Wounding.TargetFor(p, resilience))
                    .Distinct().OrderBy(t => t);
                Assert.Equal(new[] { 2, 3, 4, 5, 6 }, reachable);
            }
        }

        [Fact]
        public void OddResilienceHalvesWithoutTruncating()
        {
            // Resilience 5: half is 2.5, so Power 2 qualifies for 6+ and Power 3 does not.
            // Integer division would compute 5/2 = 2 and reach the same answer by luck; the
            // implementation multiplies instead, so this stays right for every odd value.
            Assert.Equal(6, Wounding.TargetFor(2, 5));
            Assert.Equal(5, Wounding.TargetFor(3, 5));

            Assert.Equal(6, Wounding.TargetFor(3, 7));
            Assert.Equal(5, Wounding.TargetFor(4, 7));

            Assert.Equal(6, Wounding.TargetFor(4, 9));
            Assert.Equal(5, Wounding.TargetFor(5, 9));
        }

        // ---- the ends of the legal stat range ------------------------------------

        [Theory]
        [InlineData(1, 4)]    // equal
        [InlineData(2, 2)]    // double
        [InlineData(10, 2)]
        public void ResilienceOfOne(int power, int expected)
        {
            Assert.Equal(expected, Wounding.TargetFor(power, 1));
        }

        [Theory]
        [InlineData(1, 6)]
        [InlineData(5, 6)]    // exactly half
        [InlineData(6, 5)]
        [InlineData(10, 4)]   // equal
        [InlineData(11, 3)]
        [InlineData(20, 2)]   // exactly double
        public void ResilienceOfTen(int power, int expected)
        {
            Assert.Equal(expected, Wounding.TargetFor(power, 10));
        }

        [Fact]
        public void EveryResultStaysInsideTheRollableRange()
        {
            for (int power = 1; power <= 20; power++)
                for (int resilience = 1; resilience <= 10; resilience++)
                    Assert.InRange(Wounding.TargetFor(power, resilience), 2, 6);
        }

        [Fact]
        public void MorePowerIsNeverWorse()
        {
            // Monotonicity: raising Power can only ever make wounding easier or equal.
            for (int resilience = 1; resilience <= 10; resilience++)
            {
                for (int power = 1; power < 20; power++)
                {
                    Assert.True(
                        Wounding.TargetFor(power + 1, resilience) <= Wounding.TargetFor(power, resilience),
                        $"Power {power + 1} was worse than {power} against Resilience {resilience}");
                }
            }
        }

        [Fact]
        public void MoreResilienceIsNeverWorse()
        {
            for (int power = 1; power <= 20; power++)
            {
                for (int resilience = 1; resilience < 10; resilience++)
                {
                    Assert.True(
                        Wounding.TargetFor(power, resilience + 1) >= Wounding.TargetFor(power, resilience),
                        $"Resilience {resilience + 1} was worse than {resilience} against Power {power}");
                }
            }
        }

        [Theory]
        [InlineData(0, 5)]
        [InlineData(-1, 5)]
        [InlineData(5, 0)]
        [InlineData(5, -3)]
        public void NonPositiveStatsAreRejected(int power, int resilience)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => Wounding.TargetFor(power, resilience));
        }
    }

    /// <summary>Modifier stacking and the cap.</summary>
    public class ModifierTests
    {
        private static IReadOnlyList<RollModifier> Repeat(int count, int value) =>
            Enumerable.Range(0, count)
                      .Select(_ => new RollModifier(ModifierSource.Ability, value))
                      .ToList();

        [Fact]
        public void NoModifiersLeavesTheTargetAlone()
        {
            Assert.Equal(4, Modifiers.FinalTarget(4, Modifiers.None));
            Assert.Equal(4, Modifiers.FinalTarget(4, null));
        }

        [Fact]
        public void ANegativeModifierRaisesTheTargetNumber()
        {
            // Sign convention: -1 to the ROLL means the number needed goes UP.
            var worse = new[] { new RollModifier(ModifierSource.Cover, -1) };

            Assert.Equal(5, Modifiers.FinalTarget(4, worse));
        }

        [Fact]
        public void APositiveModifierLowersTheTargetNumber()
        {
            var better = new[] { new RollModifier(ModifierSource.Elevated, 1) };

            Assert.Equal(3, Modifiers.FinalTarget(4, better));
        }

        [Fact]
        public void FiveStackedPenaltiesStillFloorAtSix()
        {
            // The cap is a ruling: no pile of penalties makes a roll futile.
            Assert.Equal(6, Modifiers.FinalTarget(4, Repeat(5, -1)));
            Assert.Equal(6, Modifiers.FinalTarget(6, Repeat(5, -1)));
            Assert.Equal(6, Modifiers.FinalTarget(2, Repeat(20, -1)));
        }

        [Fact]
        public void FiveStackedBonusesStillCapAtTwo()
        {
            // And no pile of bonuses makes it automatic. A natural 1 still fails on a 2+.
            Assert.Equal(2, Modifiers.FinalTarget(4, Repeat(5, 1)));
            Assert.Equal(2, Modifiers.FinalTarget(2, Repeat(5, 1)));
            Assert.Equal(2, Modifiers.FinalTarget(6, Repeat(20, 1)));
        }

        [Fact]
        public void MixedModifiersSumBeforeTheCapApplies()
        {
            var mixed = new[]
            {
                new RollModifier(ModifierSource.Cover, -1),
                new RollModifier(ModifierSource.Elevated, 1),
                new RollModifier(ModifierSource.Moved, -1),
            };

            // Net -1 on a 4+ is a 5+.
            Assert.Equal(5, Modifiers.FinalTarget(4, mixed));
            Assert.Equal(-1, Modifiers.NetValue(mixed));
        }

        [Fact]
        public void OrderOfModifiersCannotChangeTheResult()
        {
            // Summation is commutative on purpose: an order-dependent stack is exactly the
            // kind of rule two implementations get subtly different.
            var modifiers = new[]
            {
                new RollModifier(ModifierSource.Cover, -1),
                new RollModifier(ModifierSource.ArmourPiercing, -2),
                new RollModifier(ModifierSource.Ability, 1),
            };

            int expected = Modifiers.FinalTarget(4, modifiers);

            foreach (var permutation in Permutations(modifiers.ToList()))
                Assert.Equal(expected, Modifiers.FinalTarget(4, permutation));
        }

        [Fact]
        public void AnImpossibleBaseTargetIsNeverRescued()
        {
            // Content writes 7 to mean "no save at all". A bonus must not turn that into
            // real armour — it is not a hard roll, it is the absence of one.
            Assert.True(Modifiers.IsImpossible(7));
            Assert.Equal(7, Modifiers.FinalTarget(7, Repeat(5, 1)));
            Assert.Equal(7, Modifiers.FinalTarget(7, Modifiers.None));

            Assert.False(Modifiers.IsImpossible(6));
        }

        [Fact]
        public void ModifiersDescribeThemselvesForTheLog()
        {
            var cover = new RollModifier(ModifierSource.Cover, -1);
            Assert.Equal("cover -1", cover.ToString());

            var ability = new RollModifier(ModifierSource.Ability, 1, "Marksman");
            Assert.Equal("ability +1 (Marksman)", ability.ToString());

            Assert.Equal("cover -1, ability +1 (Marksman)",
                Modifiers.Describe(new[] { cover, ability }));

            Assert.Equal(string.Empty, Modifiers.Describe(Modifiers.None));
        }

        private static IEnumerable<List<RollModifier>> Permutations(List<RollModifier> items)
        {
            if (items.Count <= 1) { yield return items; yield break; }

            for (int i = 0; i < items.Count; i++)
            {
                var rest = new List<RollModifier>(items);
                rest.RemoveAt(i);
                foreach (var tail in Permutations(rest))
                {
                    var result = new List<RollModifier> { items[i] };
                    result.AddRange(tail);
                    yield return result;
                }
            }
        }
    }
}
