using System.Collections.Generic;
using System.Linq;
using Ironwake.Core;
using Xunit;

namespace Ironwake.Core.Tests
{
    /// <summary>
    /// Pathfinding and reachability. The scenarios build their own board and units so each
    /// test states exactly the geometry it depends on.
    /// </summary>
    public class MovementTests
    {
        private const string MoverDef = "mover";
        private const string BlockerDef = "blocker";
        private static readonly UnitId Mover = new UnitId(1);

        /// <summary>Hexes in a disc of radius n, excluding the centre: the reachable count on open ground.</summary>
        private static int OpenGroundCount(int radius) => 3 * radius * (radius + 1);

        private static IContentPack PackWithMove(int moveTenths)
        {
            var weapon = new WeaponDefinition("w", "W", 60, 2, 4, 0, 1);
            var mover = new UnitDefinition(
                MoverDef, "f", "Mover", 50, 1,
                new Statline(moveTenths, 4, 4, 5, 5, 1, 6), new[] { "w" });
            var blocker = new UnitDefinition(
                BlockerDef, "f", "Blocker", 50, 1,
                new Statline(40, 4, 4, 5, 5, 1, 6), new[] { "w" });

            return new TestContentPack(
                new[] { mover, blocker }, new[] { weapon },
                new[] { new FactionDefinition("f", "F") });
        }

        private static UnitState Unit(int id, PlayerId owner, string definition, Hex pos) =>
            new UnitState(
                new UnitId(id), owner, definition, pos, facing: 0,
                models: new List<ModelState> { new ModelState(1) },
                statuses: new List<StatusKind>(),
                hasActivated: false, actionsRemaining: 0);

        /// <summary>Mover is unit 1 at <paramref name="moverPos"/>; every other entry becomes a blocker.</summary>
        private static GameState Board(
            int radius, Hex moverPos,
            Dictionary<Hex, TerrainKind> terrain = null,
            params (Hex Pos, PlayerId Owner)[] others)
        {
            var units = new List<UnitState> { Unit(1, PlayerId.A, MoverDef, moverPos) };
            int id = 2;
            foreach (var other in others)
                units.Add(Unit(id++, other.Owner, BlockerDef, other.Pos));

            return new GameState(
                round: 1, phase: PhaseKind.Activation,
                activePlayer: PlayerId.A, activeUnit: UnitId.None,
                board: new BoardState(radius, terrain),
                units: units, objectives: new List<ObjectiveState>(),
                scoreA: 0, scoreB: 0, rng: new RngState(1UL), contentVersion: "test");
        }

        // ---- reachable set size on open ground --------------------------------

        [Theory]
        [InlineData(3, 36)]
        [InlineData(4, 60)]
        [InlineData(7, 168)]
        public void ReachableSetMatchesHandComputedCountsOnOpenGround(int allowance, int expected)
        {
            // A hex disc of radius n holds 1 + 3n(n+1) hexes; drop the centre the unit
            // already stands on and 3n(n+1) remain.
            Assert.Equal(expected, OpenGroundCount(allowance));

            // Board is big enough that the edge never clips the disc.
            var state = Board(radius: 12, moverPos: Hex.Zero);

            var reachable = Movement.ReachableFrom(state, Mover, allowance);

            Assert.Equal(expected, reachable.Count);
        }

        [Fact]
        public void ReachableSetExcludesTheHexTheUnitIsStandingOn()
        {
            var state = Board(radius: 12, moverPos: Hex.Zero);

            Assert.DoesNotContain(Hex.Zero, Movement.ReachableFrom(state, Mover, 4).Keys);
        }

        [Fact]
        public void ReachableCostEqualsHexDistanceOnOpenGround()
        {
            var state = Board(radius: 12, moverPos: Hex.Zero);

            foreach (var entry in Movement.ReachableFrom(state, Mover, 5))
                Assert.Equal(Hex.Zero.DistanceTo(entry.Key), entry.Value);
        }

        [Fact]
        public void TheBoardEdgeClipsTheReachableSet()
        {
            // Radius-3 board, allowance 5: the disc is limited by the board, not the allowance.
            var state = Board(radius: 3, moverPos: Hex.Zero);

            var reachable = Movement.ReachableFrom(state, Mover, 5);

            Assert.Equal(OpenGroundCount(3), reachable.Count);
            Assert.All(reachable.Keys, h => Assert.True(state.Board.Contains(h)));
        }

        [Fact]
        public void AZeroAllowanceReachesNothing()
        {
            var state = Board(radius: 5, moverPos: Hex.Zero);

            Assert.Empty(Movement.ReachableFrom(state, Mover, 0));
        }

        // ---- blocked in --------------------------------------------------------

        [Fact]
        public void AUnitWalledInByImpassableTerrainHasAnEmptyReachableSet()
        {
            var terrain = new Dictionary<Hex, TerrainKind>();
            for (int d = 0; d < 6; d++) terrain[Hex.Zero.Neighbour(d)] = TerrainKind.Impassable;

            var state = Board(radius: 5, moverPos: Hex.Zero, terrain: terrain);

            Assert.Empty(Movement.ReachableFrom(state, Mover, 6));
        }

        [Fact]
        public void AUnitWalledInByOtherUnitsHasAnEmptyReachableSet()
        {
            var ring = Enumerable.Range(0, 6)
                .Select(d => (Hex.Zero.Neighbour(d), d % 2 == 0 ? PlayerId.A : PlayerId.B))
                .ToArray();

            var state = Board(radius: 5, moverPos: Hex.Zero, terrain: null, others: ring);

            Assert.Empty(Movement.ReachableFrom(state, Mover, 6));
        }

        [Fact]
        public void AWalledInUnitCanStillNotPathAnywhere()
        {
            var terrain = new Dictionary<Hex, TerrainKind>();
            for (int d = 0; d < 6; d++) terrain[Hex.Zero.Neighbour(d)] = TerrainKind.Impassable;

            var state = Board(radius: 5, moverPos: Hex.Zero, terrain: terrain);

            Assert.Empty(Movement.FindPath(state, Mover, new Hex(2, 0), 6));
        }

        // ---- routing around obstacles ------------------------------------------

        [Fact]
        public void APathGoesAroundImpassableTerrainNotThroughIt()
        {
            var blocked = new Hex(1, 0);
            var dest = new Hex(2, 0);
            var terrain = new Dictionary<Hex, TerrainKind> { { blocked, TerrainKind.Impassable } };

            var state = Board(radius: 5, moverPos: Hex.Zero, terrain: terrain);

            var path = Movement.FindPath(state, Mover, dest, 4);

            Assert.NotEmpty(path);
            Assert.DoesNotContain(blocked, path);
            Assert.Equal(Hex.Zero, path[0]);
            Assert.Equal(dest, path[path.Count - 1]);
            AssertContiguous(path);

            // The straight line this replaced walks straight through the wall — which is the
            // whole reason LegalActions and ValidateMove used to disagree.
            Assert.Contains(blocked, Hex.Zero.LineTo(dest));
        }

        [Fact]
        public void RoutingAroundAnObstacleCostsAnExtraStep()
        {
            var terrain = new Dictionary<Hex, TerrainKind> { { new Hex(1, 0), TerrainKind.Impassable } };
            var state = Board(radius: 5, moverPos: Hex.Zero, terrain: terrain);
            var dest = new Hex(2, 0);

            var path = Movement.FindPath(state, Mover, dest, 4);

            // Straight-line distance is 2; the detour makes it 3 steps, so 4 hexes listed.
            Assert.Equal(2, Hex.Zero.DistanceTo(dest));
            Assert.Equal(4, path.Count);
            Assert.Equal(3, Movement.ReachableFrom(state, Mover, 4)[dest]);
        }

        [Theory]
        [InlineData(true)]   // friendly
        [InlineData(false)]  // enemy
        public void AUnitCannotPathThroughAnotherUnit(bool friendly)
        {
            var occupied = new Hex(1, 0);
            var dest = new Hex(2, 0);
            var owner = friendly ? PlayerId.A : PlayerId.B;

            var state = Board(radius: 5, moverPos: Hex.Zero, terrain: null, others: (occupied, owner));

            var path = Movement.FindPath(state, Mover, dest, 4);

            Assert.NotEmpty(path);
            Assert.DoesNotContain(occupied, path);
            AssertContiguous(path);

            // The occupied hex is not somewhere the unit may stop, either.
            Assert.DoesNotContain(occupied, Movement.ReachableFrom(state, Mover, 4).Keys);
        }

        [Fact]
        public void APathToAnOccupiedHexIsEmpty()
        {
            var occupied = new Hex(2, 0);
            var state = Board(radius: 5, moverPos: Hex.Zero, terrain: null, others: (occupied, PlayerId.B));

            Assert.Empty(Movement.FindPath(state, Mover, occupied, 4));
        }

        [Fact]
        public void APathBeyondTheAllowanceIsEmpty()
        {
            var state = Board(radius: 12, moverPos: Hex.Zero);

            Assert.Empty(Movement.FindPath(state, Mover, new Hex(5, 0), 4));
            Assert.NotEmpty(Movement.FindPath(state, Mover, new Hex(4, 0), 4));
        }

        [Fact]
        public void APathOffTheBoardIsEmpty()
        {
            var state = Board(radius: 3, moverPos: Hex.Zero);

            Assert.Empty(Movement.FindPath(state, Mover, new Hex(9, 0), 12));
        }

        // ---- determinism --------------------------------------------------------

        [Fact]
        public void FindPathReturnsTheIdenticalPathAcrossAHundredCalls()
        {
            // Several shortest routes exist to most of these hexes; the tie-break must land
            // on the same one every time or client and server will disagree about the route.
            var terrain = new Dictionary<Hex, TerrainKind>
            {
                { new Hex(1, 0), TerrainKind.Impassable },
                { new Hex(0, 2), TerrainKind.Impassable },
                { new Hex(-2, 1), TerrainKind.Impassable },
            };
            var state = Board(radius: 8, moverPos: Hex.Zero, terrain: terrain,
                              others: new[] { (new Hex(2, -2), PlayerId.B), (new Hex(-1, -1), PlayerId.A) });

            var dest = new Hex(3, 1);
            var expected = Movement.FindPath(state, Mover, dest, 7);
            Assert.NotEmpty(expected);

            for (int i = 0; i < 100; i++)
                Assert.Equal(expected, Movement.FindPath(state, Mover, dest, 7));
        }

        [Fact]
        public void ReachableSetIsIdenticalAcrossRepeatedCalls()
        {
            var state = Board(radius: 8, moverPos: Hex.Zero,
                              terrain: new Dictionary<Hex, TerrainKind> { { new Hex(1, 0), TerrainKind.Impassable } });

            var first = Movement.ReachableFrom(state, Mover, 5);

            for (int i = 0; i < 50; i++)
            {
                var again = Movement.ReachableFrom(state, Mover, 5);
                Assert.Equal(
                    first.OrderBy(k => k.Key.Q).ThenBy(k => k.Key.R).ToList(),
                    again.OrderBy(k => k.Key.Q).ThenBy(k => k.Key.R).ToList());
            }
        }

        // ---- path shape ---------------------------------------------------------

        [Fact]
        public void PathLengthNeverExceedsTheAllowancePlusOne()
        {
            // "+1" because the path includes the hex the unit starts on.
            var terrain = new Dictionary<Hex, TerrainKind>
            {
                { new Hex(1, 0), TerrainKind.Impassable },
                { new Hex(1, 1), TerrainKind.Impassable },
                { new Hex(-1, 2), TerrainKind.Impassable },
            };

            foreach (int allowance in new[] { 1, 2, 3, 4, 5, 6, 7 })
            {
                var state = Board(radius: 10, moverPos: Hex.Zero, terrain: terrain,
                                  others: (new Hex(2, -1), PlayerId.B));

                foreach (var hex in Movement.ReachableFrom(state, Mover, allowance).Keys)
                {
                    var path = Movement.FindPath(state, Mover, hex, allowance);
                    Assert.InRange(path.Count, 2, allowance + 1);
                    AssertContiguous(path);
                }
            }
        }

        [Fact]
        public void APathsStepCountMatchesTheReachableCost()
        {
            var terrain = new Dictionary<Hex, TerrainKind> { { new Hex(1, 0), TerrainKind.Impassable } };
            var state = Board(radius: 8, moverPos: Hex.Zero, terrain: terrain);

            foreach (var entry in Movement.ReachableFrom(state, Mover, 5))
            {
                var path = Movement.FindPath(state, Mover, entry.Key, 5);
                Assert.Equal(entry.Value, path.Count - 1);
            }
        }

        [Fact]
        public void APathToTheUnitsOwnHexIsJustThatHex()
        {
            var state = Board(radius: 5, moverPos: new Hex(1, 1));

            var path = Movement.FindPath(state, Mover, new Hex(1, 1), 4);

            Assert.Single(path);
            Assert.Equal(new Hex(1, 1), path[0]);
        }

        // ---- the blocking predicate --------------------------------------------

        [Fact]
        public void BlockingReasonNamesWhyAHexIsUnavailable()
        {
            var terrain = new Dictionary<Hex, TerrainKind> { { new Hex(1, 0), TerrainKind.Impassable } };
            var state = Board(radius: 3, moverPos: Hex.Zero, terrain: terrain,
                              others: (new Hex(0, 1), PlayerId.B));

            Assert.Equal(HexBlock.None, Movement.BlockingReason(state, Mover, new Hex(-1, 0)));
            Assert.Equal(HexBlock.Impassable, Movement.BlockingReason(state, Mover, new Hex(1, 0)));
            Assert.Equal(HexBlock.Occupied, Movement.BlockingReason(state, Mover, new Hex(0, 1)));
            Assert.Equal(HexBlock.OffBoard, Movement.BlockingReason(state, Mover, new Hex(9, 0)));

            // A unit never blocks itself.
            Assert.Equal(HexBlock.None, Movement.BlockingReason(state, Mover, Hex.Zero));
        }

        [Fact]
        public void DestroyedUnitsDoNotBlockMovement()
        {
            var corpseHex = new Hex(1, 0);
            var state = Board(radius: 5, moverPos: Hex.Zero, terrain: null, others: (corpseHex, PlayerId.B));

            var corpse = state.Units.First(u => u.Position == corpseHex);
            var dead = state.WithUnit(corpse.With(models: new List<ModelState> { new ModelState(0, isSlain: true) }));

            Assert.Equal(HexBlock.Occupied, Movement.BlockingReason(state, Mover, corpseHex));
            Assert.Equal(HexBlock.None, Movement.BlockingReason(dead, Mover, corpseHex));
        }

        private static void AssertContiguous(IReadOnlyList<Hex> path)
        {
            for (int i = 1; i < path.Count; i++)
                Assert.True(path[i - 1].DistanceTo(path[i]) == 1,
                    $"step {i} from {path[i - 1]} to {path[i]} is not adjacent");
        }
    }
}
