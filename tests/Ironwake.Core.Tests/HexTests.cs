using System.Collections.Generic;
using System.Linq;
using Ironwake.Core;
using Xunit;

namespace Ironwake.Core.Tests
{
    /// <summary>
    /// Hex is the coordinate system the whole project rests on. If these break,
    /// every distance, range check and line-of-sight test downstream is wrong.
    /// </summary>
    public class HexDistanceTests
    {
        [Fact]
        public void DistanceToSelf_IsZero()
        {
            Assert.Equal(0, Hex.Zero.DistanceTo(Hex.Zero));

            // not just at the origin
            foreach (var h in Hex.Zero.WithinRange(5))
                Assert.Equal(0, h.DistanceTo(h));
        }

        [Fact]
        public void DistanceToEachNeighbour_IsOne()
        {
            for (int d = 0; d < 6; d++)
                Assert.Equal(1, Hex.Zero.DistanceTo(Hex.Zero.Neighbour(d)));
        }

        [Fact]
        public void DistanceToEachNeighbour_IsOne_AwayFromOrigin()
        {
            var centre = new Hex(-3, 2);
            for (int d = 0; d < 6; d++)
                Assert.Equal(1, centre.DistanceTo(centre.Neighbour(d)));
        }

        [Fact]
        public void EveryHexHasExactlySixNeighbours()
        {
            var centre = new Hex(2, -1);
            var neighbours = Enumerable.Range(0, 6).Select(centre.Neighbour).ToList();
            Assert.Equal(6, neighbours.Distinct().Count());
            Assert.DoesNotContain(centre, neighbours);
        }

        [Theory]
        // straight along each axis
        [InlineData(0, 0, 3, 0, 3)]
        [InlineData(0, 0, 0, 3, 3)]
        [InlineData(0, 0, -3, 0, 3)]
        [InlineData(0, 0, 0, -3, 3)]
        // along the third (S) axis: q and r move together in opposite directions
        [InlineData(0, 0, 3, -3, 3)]
        [InlineData(0, 0, -3, 3, 3)]
        // diagonals and mixed
        [InlineData(0, 0, 2, -1, 2)]
        [InlineData(0, 0, 1, 1, 2)]
        [InlineData(0, 0, -2, -2, 4)]
        [InlineData(-4, 1, 1, 1, 5)]
        [InlineData(-4, 2, 3, -1, 7)]
        [InlineData(2, -3, -2, 3, 6)]
        public void KnownPairs_HaveExpectedDistance(int q1, int r1, int q2, int r2, int expected)
        {
            var a = new Hex(q1, r1);
            var b = new Hex(q2, r2);
            Assert.Equal(expected, a.DistanceTo(b));
        }

        [Fact]
        public void Distance_IsSymmetric()
        {
            var hexes = Hex.Zero.WithinRange(4).ToList();
            foreach (var a in hexes)
                foreach (var b in hexes)
                    Assert.Equal(a.DistanceTo(b), b.DistanceTo(a));
        }

        [Fact]
        public void WithinRange_MatchesDistance()
        {
            // WithinRange(n) must contain exactly the hexes at distance <= n.
            const int radius = 5;
            var within = Hex.Zero.WithinRange(radius).ToList();

            Assert.All(within, h => Assert.True(Hex.Zero.DistanceTo(h) <= radius));

            // 1 + 3*n*(n+1) is the closed form for a hex disc of radius n
            Assert.Equal(1 + 3 * radius * (radius + 1), within.Count);
            Assert.Equal(within.Count, within.Distinct().Count());
        }

        [Fact]
        public void CubeCoordinates_AlwaysSumToZero()
        {
            foreach (var h in Hex.Zero.WithinRange(5))
                Assert.Equal(0, h.Q + h.R + h.S);
        }
    }

    public class HexPixelRoundTripTests
    {
        public static IEnumerable<object[]> Sizes =>
            new[] { new object[] { 1.0 }, new object[] { 0.5 }, new object[] { 10.0 }, new object[] { 64.0 } };

        [Theory]
        [MemberData(nameof(Sizes))]
        public void ToPixelThenFromPixel_ReturnsOriginalHex_ForEveryHexWithinRadius5(double size)
        {
            foreach (var original in Hex.Zero.WithinRange(5))
            {
                original.ToPixel(size, out double x, out double y);
                var roundTripped = Hex.FromPixel(x, y, size);

                Assert.True(
                    original == roundTripped,
                    $"size {size}: {original} -> pixel ({x},{y}) -> {roundTripped}");
            }
        }

        [Fact]
        public void ToPixel_PlacesOriginAtZero()
        {
            Hex.Zero.ToPixel(10.0, out double x, out double y);
            Assert.Equal(0.0, x, 9);
            Assert.Equal(0.0, y, 9);
        }

        [Fact]
        public void DistinctHexes_MapToDistinctPixels()
        {
            var seen = new HashSet<(double, double)>();
            foreach (var h in Hex.Zero.WithinRange(5))
            {
                h.ToPixel(10.0, out double x, out double y);
                Assert.True(seen.Add((x, y)), $"{h} collided with an earlier hex at ({x},{y})");
            }
        }
    }

    public class HexLineTests
    {
        /// <summary>Every ordered pair of hexes on a radius-5 board.</summary>
        public static IEnumerable<object[]> AllPairsWithinRadius5()
        {
            var hexes = Hex.Zero.WithinRange(5).ToList();
            foreach (var a in hexes)
                foreach (var b in hexes)
                    yield return new object[] { a.Q, a.R, b.Q, b.R };
        }

        [Fact]
        public void LineTo_EndpointsAreCorrect()
        {
            foreach (var a in Hex.Zero.WithinRange(5))
            {
                foreach (var b in Hex.Zero.WithinRange(5))
                {
                    var line = a.LineTo(b);
                    Assert.Equal(a, line[0]);
                    Assert.Equal(b, line[line.Count - 1]);
                }
            }
        }

        [Fact]
        public void LineTo_LengthEqualsDistancePlusOne()
        {
            foreach (var a in Hex.Zero.WithinRange(5))
            {
                foreach (var b in Hex.Zero.WithinRange(5))
                {
                    var line = a.LineTo(b);
                    Assert.Equal(a.DistanceTo(b) + 1, line.Count);
                }
            }
        }

        [Fact]
        public void LineTo_IsContiguous_EveryStepIsOneHex()
        {
            foreach (var a in Hex.Zero.WithinRange(5))
            {
                foreach (var b in Hex.Zero.WithinRange(5))
                {
                    var line = a.LineTo(b);
                    for (int i = 1; i < line.Count; i++)
                    {
                        Assert.True(
                            line[i - 1].DistanceTo(line[i]) == 1,
                            $"{a}->{b}: step {i} from {line[i - 1]} to {line[i]} is not adjacent");
                    }
                }
            }
        }

        [Fact]
        public void LineTo_NeverRevisitsAHex()
        {
            foreach (var a in Hex.Zero.WithinRange(5))
            {
                foreach (var b in Hex.Zero.WithinRange(5))
                {
                    var line = a.LineTo(b);
                    Assert.Equal(line.Count, line.Distinct().Count());
                }
            }
        }

        [Fact]
        public void LineTo_Self_IsSingleHex()
        {
            var h = new Hex(2, -1);
            var line = h.LineTo(h);
            Assert.Single(line);
            Assert.Equal(h, line[0]);
        }

        [Fact]
        public void LineTo_Neighbour_IsExactlyBothHexes()
        {
            var a = new Hex(1, 1);
            for (int d = 0; d < 6; d++)
            {
                var b = a.Neighbour(d);
                var line = a.LineTo(b);
                Assert.Equal(2, line.Count);
                Assert.Equal(a, line[0]);
                Assert.Equal(b, line[1]);
            }
        }

        [Fact]
        public void LineTo_AlongAnAxis_IsTheStraightRun()
        {
            var line = new Hex(0, 0).LineTo(new Hex(3, 0));
            Assert.Equal(
                new[] { new Hex(0, 0), new Hex(1, 0), new Hex(2, 0), new Hex(3, 0) },
                line);
        }
    }
}
