using System;
using Ironwake.Core;
using Xunit;

namespace Ironwake.Core.Tests
{
    /// <summary>
    /// The tenths-of-an-inch to hex conversion. It lives in one place precisely so that
    /// retuning board scale is a one-line change; these tests pin its behaviour, including
    /// what happens to distances that do not divide evenly.
    /// </summary>
    public class MeasureTests
    {
        [Fact]
        public void OneHexIsTenTenths()
        {
            Assert.Equal(10, Measure.TenthsPerHex);
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(10, 1)]
        [InlineData(40, 4)]
        [InlineData(60, 6)]
        [InlineData(100, 10)]
        public void RoundValuesConvertExactly(int tenths, int expectedHexes)
        {
            Assert.Equal(expectedHexes, Measure.TenthsToHexes(tenths));
        }

        [Theory]
        // A unit cannot move a fraction of a hex, so partial hexes are dropped.
        [InlineData(45, 4)]
        [InlineData(49, 4)]
        [InlineData(41, 4)]
        [InlineData(9, 0)]
        [InlineData(1, 0)]
        [InlineData(55, 5)]
        [InlineData(99, 9)]
        public void NonRoundValuesRoundDown(int tenths, int expectedHexes)
        {
            Assert.Equal(expectedHexes, Measure.TenthsToHexes(tenths));
        }

        [Fact]
        public void RoundingDownNeverGrantsAnExtraHex()
        {
            // Property form of the above: converting back can only ever lose distance.
            for (int tenths = 0; tenths <= 200; tenths++)
            {
                int hexes = Measure.TenthsToHexes(tenths);
                Assert.True(Measure.HexesToTenths(hexes) <= tenths,
                    $"{tenths} tenths became {hexes} hexes, which is further than authored");
                Assert.True(Measure.HexesToTenths(hexes + 1) > tenths,
                    $"{tenths} tenths should not have been {hexes + 1} hexes");
            }
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(1, 10)]
        [InlineData(4, 40)]
        [InlineData(7, 70)]
        public void HexesConvertBackToTenths(int hexes, int expectedTenths)
        {
            Assert.Equal(expectedTenths, Measure.HexesToTenths(hexes));
        }

        [Fact]
        public void WholeHexValuesRoundTrip()
        {
            for (int hexes = 0; hexes <= 20; hexes++)
                Assert.Equal(hexes, Measure.TenthsToHexes(Measure.HexesToTenths(hexes)));
        }

        [Fact]
        public void NegativeDistancesAreRejected()
        {
            // Truncation toward zero would silently mis-handle these, so refuse instead.
            Assert.Throws<ArgumentOutOfRangeException>(() => Measure.TenthsToHexes(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => Measure.HexesToTenths(-1));
        }

        [Fact]
        public void StatlineExposesMoveInHexesThroughTheSameConversion()
        {
            var stats = new Statline(move: 45, accuracy: 4, melee: 4, resilience: 5,
                                     save: 5, wounds: 1, nerve: 6);

            Assert.Equal(Measure.TenthsToHexes(45), stats.MoveInHexes);
            Assert.Equal(4, stats.MoveInHexes);
        }

        [Fact]
        public void WeaponExposesRangeInHexesThroughTheSameConversion()
        {
            var weapon = new WeaponDefinition("w", "W", range: 65, attacks: 1, power: 4,
                                              armourPiercing: 0, damage: 1);

            Assert.Equal(Measure.TenthsToHexes(65), weapon.RangeInHexes);
            Assert.Equal(6, weapon.RangeInHexes);
        }
    }

    /// <summary>The content types themselves: immutable, and defensive about their lists.</summary>
    public class ContentTypeTests
    {
        [Fact]
        public void UnitDefinitionCopiesItsListsSoCallersCannotMutateIt()
        {
            var weapons = new List<string> { "w1" };
            var def = new UnitDefinition(
                "u", "f", "U", 10, 1,
                new Statline(40, 4, 4, 5, 5, 1, 6), weapons);

            weapons.Add("w2");

            Assert.Single(def.WeaponIds);
            Assert.Equal("w1", def.WeaponIds[0]);
        }

        [Fact]
        public void NullListsBecomeEmptyNotNull()
        {
            var def = new UnitDefinition("u", "f", "U", 10, 1, new Statline(40, 4, 4, 5, 5, 1, 6));

            Assert.Empty(def.WeaponIds);
            Assert.Empty(def.AbilityIds);
            Assert.Empty(def.Keywords);
        }

        [Fact]
        public void MissingIdThrowsContentNotFoundNamingTheId()
        {
            var pack = TestContent.Basic();

            var ex = Assert.Throws<ContentNotFoundException>(() => pack.GetUnit("no_such_unit"));

            Assert.Equal("no_such_unit", ex.Id);
            Assert.Contains("no_such_unit", ex.Message);
        }

        [Fact]
        public void TryGetUnitReportsMissesWithoutThrowing()
        {
            var pack = TestContent.Basic();

            Assert.False(pack.TryGetUnit("no_such_unit", out var missing));
            Assert.Null(missing);

            Assert.True(pack.TryGetUnit("test_unit", out var found));
            Assert.Equal("test_unit", found.Id);
        }
    }
}
