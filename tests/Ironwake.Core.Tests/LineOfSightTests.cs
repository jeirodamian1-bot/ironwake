using System.Collections.Generic;
using System.Linq;
using Ironwake.Core;
using Xunit;

namespace Ironwake.Core.Tests
{
    /// <summary>
    /// Sight tracing. Each scenario builds the terrain it depends on, so what the test claims
    /// about the board is visible in the test.
    /// </summary>
    public class LineOfSightTests
    {
        private static GameState BoardWith(
            Dictionary<Hex, TerrainKind> terrain = null, int radius = 8,
            params (Hex Pos, PlayerId Owner)[] units)
        {
            var list = new List<UnitState>();
            int id = 1;
            foreach (var u in units)
            {
                list.Add(new UnitState(
                    new UnitId(id++), u.Owner, "test_unit", u.Pos, facing: 0,
                    models: new List<ModelState> { new ModelState(1) },
                    statuses: new List<StatusKind>(),
                    hasActivated: false, actionsRemaining: 0));
            }

            return new GameState(
                round: 1, phase: PhaseKind.Activation,
                activePlayer: PlayerId.A, activeUnit: UnitId.None,
                board: new BoardState(radius, terrain),
                units: list, objectives: new List<ObjectiveState>(),
                scoreA: 0, scoreB: 0, rng: new RngState(1UL), contentVersion: "test");
        }

        private static Dictionary<Hex, TerrainKind> Terrain(params (Hex, TerrainKind)[] entries)
        {
            var map = new Dictionary<Hex, TerrainKind>();
            foreach (var (hex, kind) in entries) map[hex] = kind;
            return map;
        }

        // ---- obscuring blocks --------------------------------------------------

        [Fact]
        public void ObscuringTerrainBetweenTheEndsBlocksAndNamesTheHex()
        {
            var wall = new Hex(1, 0);
            var state = BoardWith(Terrain((wall, TerrainKind.Obscuring)));

            var los = LineOfSight.Trace(state, Hex.Zero, new Hex(2, 0));

            Assert.True(los.IsBlocked);
            Assert.Equal(wall, los.BlockingHex);
            Assert.False(LineOfSight.HasLineOfSight(state, Hex.Zero, new Hex(2, 0)));
        }

        [Fact]
        public void ObscuringOnEitherEndNeverBlocks()
        {
            // Standing in the smoke does not blind you, and does not hide you either.
            var from = Hex.Zero;
            var to = new Hex(3, 0);
            var state = BoardWith(Terrain((from, TerrainKind.Obscuring), (to, TerrainKind.Obscuring)));

            Assert.True(LineOfSight.HasLineOfSight(state, from, to));
            Assert.True(LineOfSight.HasLineOfSight(state, to, from));
        }

        [Fact]
        public void ATargetStandingOnObscuringIsVisibleAndInCover()
        {
            var target = new Hex(3, 0);
            var state = BoardWith(Terrain((target, TerrainKind.Obscuring)));

            var los = LineOfSight.Trace(state, Hex.Zero, target);

            Assert.False(los.IsBlocked);
            Assert.True(los.TargetInCover);
        }

        [Fact]
        public void ATargetStandingOnCoverIsInCover()
        {
            var target = new Hex(3, 0);
            var state = BoardWith(Terrain((target, TerrainKind.Cover)));

            var los = LineOfSight.Trace(state, Hex.Zero, target);

            Assert.False(los.IsBlocked);
            Assert.True(los.TargetInCover);
        }

        [Theory]
        [InlineData(TerrainKind.Open)]
        [InlineData(TerrainKind.Elevated)]
        [InlineData(TerrainKind.Impassable)]
        public void OtherTerrainGrantsNoCover(TerrainKind kind)
        {
            var target = new Hex(3, 0);
            var state = BoardWith(Terrain((target, kind)));

            Assert.False(LineOfSight.Trace(state, Hex.Zero, target).TargetInCover);
        }

        // ---- what does not block ------------------------------------------------

        [Fact]
        public void ImpassableTerrainDoesNotBlockSight()
        {
            // Unwalkable and opaque are different properties: a chasm stops a boot, not an eye.
            var chasm = new Hex(1, 0);
            var state = BoardWith(Terrain((chasm, TerrainKind.Impassable)));

            Assert.True(LineOfSight.HasLineOfSight(state, Hex.Zero, new Hex(2, 0)));
            Assert.False(LineOfSight.BlocksSight(state, chasm));
        }

        [Fact]
        public void ElevatedTerrainDoesNotBlockSight()
        {
            var hill = new Hex(1, 0);
            var state = BoardWith(Terrain((hill, TerrainKind.Elevated)));

            Assert.True(LineOfSight.HasLineOfSight(state, Hex.Zero, new Hex(2, 0)));
        }

        [Fact]
        public void CoverTerrainDoesNotBlockSightThroughIt()
        {
            var state = BoardWith(Terrain((new Hex(1, 0), TerrainKind.Cover)));

            Assert.True(LineOfSight.HasLineOfSight(state, Hex.Zero, new Hex(2, 0)));
        }

        [Fact]
        public void UnitsDoNotBlockSight()
        {
            // Deliberate: unit-blocks-unit is a rules decision nobody has taken yet, so it
            // must not arrive by accident.
            var between = new Hex(1, 0);
            var state = BoardWith(null, 8,
                (Hex.Zero, PlayerId.A), (between, PlayerId.B), (new Hex(2, 0), PlayerId.B));

            Assert.True(LineOfSight.HasLineOfSight(state, Hex.Zero, new Hex(2, 0)));
        }

        // ---- elevation ------------------------------------------------------------

        [Fact]
        public void AShooterOnElevatedSeesOverObscuring()
        {
            var wall = new Hex(1, 0);
            var from = Hex.Zero;
            var to = new Hex(2, 0);

            var blocked = BoardWith(Terrain((wall, TerrainKind.Obscuring)));
            Assert.False(LineOfSight.HasLineOfSight(blocked, from, to));

            // Same shot, same wall, shooter now on high ground.
            var elevated = BoardWith(Terrain((wall, TerrainKind.Obscuring), (from, TerrainKind.Elevated)));

            var los = LineOfSight.Trace(elevated, from, to);
            Assert.False(los.IsBlocked);
            Assert.True(los.ShooterElevated);
        }

        [Fact]
        public void AShooterOnElevatedIgnoresTheTargetsCover()
        {
            var from = Hex.Zero;
            var target = new Hex(3, 0);

            var ground = BoardWith(Terrain((target, TerrainKind.Cover)));
            Assert.True(LineOfSight.Trace(ground, from, target).TargetInCover);

            var high = BoardWith(Terrain((target, TerrainKind.Cover), (from, TerrainKind.Elevated)));
            Assert.False(LineOfSight.Trace(high, from, target).TargetInCover);
        }

        [Fact]
        public void ATargetOnElevatedGetsNoSpecialProtection()
        {
            // Elevation helps the one shooting FROM it, not the one standing ON it.
            var wall = new Hex(1, 0);
            var state = BoardWith(Terrain((wall, TerrainKind.Obscuring), (new Hex(2, 0), TerrainKind.Elevated)));

            Assert.False(LineOfSight.HasLineOfSight(state, Hex.Zero, new Hex(2, 0)));
        }

        // ---- adjacency -------------------------------------------------------------

        [Fact]
        public void AdjacentHexesAlwaysSeeEachOther()
        {
            // Nothing can stand between two neighbours, whatever the terrain says.
            var centre = new Hex(1, -1);

            for (int d = 0; d < 6; d++)
            {
                var neighbour = centre.Neighbour(d);
                var state = BoardWith(Terrain(
                    (centre, TerrainKind.Obscuring), (neighbour, TerrainKind.Obscuring)));

                Assert.True(LineOfSight.HasLineOfSight(state, centre, neighbour),
                    $"{centre} could not see its neighbour {neighbour}");
            }
        }

        [Fact]
        public void AHexAlwaysSeesItself()
        {
            var state = BoardWith(Terrain((Hex.Zero, TerrainKind.Obscuring)));

            Assert.True(LineOfSight.HasLineOfSight(state, Hex.Zero, Hex.Zero));
        }

        // ---- the edge-tie ruling -----------------------------------------------------

        [Fact]
        public void SightIsBlockedOnlyWhenBOTHCandidateLinesAreBlocked()
        {
            // A line along a hex edge has two equally valid tracks. Blocking one of them must
            // not deny the shot — the shooter gets the benefit of an ambiguous angle.
            var from = Hex.Zero;
            var to = new Hex(2, -1);   // distance 2, runs between two hexes

            var bothTracks = from.LineTo(to, LineTieBreak.Positive)
                .Concat(from.LineTo(to, LineTieBreak.Negative))
                .Where(h => h != from && h != to)
                .Distinct()
                .ToList();

            // The geometry this test depends on: the two nudges disagree about the middle hex.
            Assert.True(bothTracks.Count > 1,
                "expected an ambiguous line with two candidate middle hexes");

            foreach (var single in bothTracks)
            {
                var state = BoardWith(Terrain((single, TerrainKind.Obscuring)));
                Assert.True(LineOfSight.HasLineOfSight(state, from, to),
                    $"blocking only {single} should leave the alternate track open");
            }

            // Block every candidate and the shot is genuinely denied.
            var all = BoardWith(bothTracks.ToDictionary(h => h, _ => TerrainKind.Obscuring));
            Assert.False(LineOfSight.HasLineOfSight(all, from, to));
        }

        [Fact]
        public void TracingIsStableAcrossRepeatedCalls()
        {
            var state = BoardWith(Terrain(
                (new Hex(1, 0), TerrainKind.Obscuring), (new Hex(1, -1), TerrainKind.Obscuring)));

            var reference = LineOfSight.Trace(state, Hex.Zero, new Hex(3, -1));
            for (int i = 0; i < 100; i++)
            {
                var again = LineOfSight.Trace(state, Hex.Zero, new Hex(3, -1));
                Assert.Equal(reference.IsBlocked, again.IsBlocked);
                Assert.Equal(reference.BlockingHex, again.BlockingHex);
                Assert.Equal(reference.TargetInCover, again.TargetInCover);
            }
        }

        // ---- symmetry ------------------------------------------------------------------

        [Fact]
        public void SightIsSymmetricExceptWhereExactlyOneEndIsElevated()
        {
            // If a can see b, b can see a — with one deliberate exception. A shooter on high
            // ground sees over Obscuring, so an elevated hex sees out of a pocket it cannot be
            // seen into. That asymmetry is a rule, so it is asserted rather than excused.
            var content = TestContent.ForSampleGame();
            var state = SampleGame.Create(content, 1UL);
            var hexes = state.Board.AllHexes().ToList();

            int symmetric = 0;
            int asymmetric = 0;

            foreach (var a in hexes)
            {
                foreach (var b in hexes)
                {
                    bool aSeesB = LineOfSight.HasLineOfSight(state, a, b);
                    bool bSeesA = LineOfSight.HasLineOfSight(state, b, a);

                    bool aHigh = state.Board.TerrainAt(a) == TerrainKind.Elevated;
                    bool bHigh = state.Board.TerrainAt(b) == TerrainKind.Elevated;
                    bool exactlyOneHigh = aHigh ^ bHigh;

                    if (aSeesB == bSeesA) { symmetric++; continue; }

                    asymmetric++;
                    Assert.True(exactlyOneHigh,
                        $"{a} and {b} disagree about sight but neither is the elevated end");

                    // And the elevated end is always the one that can see.
                    Assert.True(aHigh ? aSeesB : bSeesA,
                        $"the elevated end of {a}/{b} should be the one with sight");
                }
            }

            Assert.True(symmetric > 1000, $"only {symmetric} pairs were compared");
            Assert.True(asymmetric > 0,
                "no asymmetric pair was found — the elevated exception was never exercised");
        }

        [Fact]
        public void CoverIsNotSymmetric()
        {
            // Cover belongs to whoever is being shot at, so it flips with the direction.
            var sheltered = new Hex(2, 0);
            var state = BoardWith(Terrain((sheltered, TerrainKind.Cover)));

            Assert.True(LineOfSight.Trace(state, Hex.Zero, sheltered).TargetInCover);
            Assert.False(LineOfSight.Trace(state, sheltered, Hex.Zero).TargetInCover);
        }

        // ---- the bulk query --------------------------------------------------------------

        [Fact]
        public void VisibleFromExcludesWhatIsBlockedAndTheOriginItself()
        {
            var state = BoardWith(Terrain((new Hex(1, 0), TerrainKind.Obscuring)));

            var visible = LineOfSight.VisibleFrom(state, Hex.Zero, 4);

            Assert.DoesNotContain(Hex.Zero, visible);
            Assert.Contains(new Hex(1, 0), visible);      // the wall itself is an end, so visible
            Assert.DoesNotContain(new Hex(3, 0), visible); // straight behind it
            Assert.All(visible, h => Assert.True(LineOfSight.HasLineOfSight(state, Hex.Zero, h)));
        }
    }
}
