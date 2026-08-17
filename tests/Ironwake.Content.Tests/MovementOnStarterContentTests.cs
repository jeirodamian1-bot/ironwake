using System.Collections.Generic;
using System.Linq;
using Ironwake.Core;
using Xunit;

namespace Ironwake.Content.Tests
{
    /// <summary>
    /// Movement driven by the real authored statlines, not a test fixture. This is where a
    /// content retune that accidentally flattens the factions' mobility shows up.
    /// </summary>
    public class MovementOnStarterContentTests
    {
        /// <summary>Hexes in a disc of radius n excluding the centre.</summary>
        private static int OpenGroundCount(int radius) => 3 * radius * (radius + 1);

        /// <summary>A single unit of the given definition alone on a big empty board.</summary>
        private static GameState SoloOnOpenGround(string definitionId, Hex where)
        {
            var unit = new UnitState(
                new UnitId(1), PlayerId.A, definitionId, where, facing: 0,
                models: new List<ModelState> { new ModelState(1) },
                statuses: new List<StatusKind>(),
                hasActivated: false, actionsRemaining: 2);

            return new GameState(
                round: 1, phase: PhaseKind.Activation,
                activePlayer: PlayerId.A, activeUnit: new UnitId(1),
                board: new BoardState(radius: 10),
                units: new List<UnitState> { unit },
                objectives: new List<ObjectiveState>(),
                scoreA: 0, scoreB: 0, rng: new RngState(1UL),
                contentVersion: "test");
        }

        [Fact]
        public void AshrunnerReachesStrictlyMoreHexesThanBulwarkFromTheSamePosition()
        {
            var pack = StarterPack.Load();
            IGameEngine engine = new RulesEngine(pack);
            var spot = Hex.Zero;

            var ashrunner = engine.ReachableHexes(SoloOnOpenGround("cinderkin_ashrunner", spot), new UnitId(1));
            var bulwark = engine.ReachableHexes(SoloOnOpenGround("ashguard_bulwark", spot), new UnitId(1));

            Assert.True(ashrunner.Count > bulwark.Count,
                $"ashrunner reached {ashrunner.Count} hexes, bulwark reached {bulwark.Count}");

            // 70 tenths is 7 hexes, 30 tenths is 3 — on open ground that is the full disc each.
            Assert.Equal(7, pack.GetUnit("cinderkin_ashrunner").Stats.MoveInHexes);
            Assert.Equal(3, pack.GetUnit("ashguard_bulwark").Stats.MoveInHexes);
            Assert.Equal(OpenGroundCount(7), ashrunner.Count);
            Assert.Equal(OpenGroundCount(3), bulwark.Count);

            // Everywhere the slow unit can go, the fast one can too.
            Assert.All(bulwark.Keys, h => Assert.Contains(h, ashrunner.Keys));
        }

        [Fact]
        public void EveryStarterUnitCanActuallyMove()
        {
            // A unit authored with move 0 would load fine and then be unable to do anything.
            var pack = StarterPack.Load();
            IGameEngine engine = new RulesEngine(pack);

            foreach (var definition in pack.AllUnits)
            {
                var state = SoloOnOpenGround(definition.Id, Hex.Zero);
                var reachable = engine.ReachableHexes(state, new UnitId(1));

                Assert.True(reachable.Count > 0, $"{definition.Id} cannot move anywhere");
                Assert.Equal(OpenGroundCount(definition.Stats.MoveInHexes), reachable.Count);
            }
        }

        [Fact]
        public void EveryReachableHexValidatesOnRealContent()
        {
            // Same invariant the Core suite asserts against a hand-built pack, re-checked
            // against the authored statlines.
            var pack = StarterPack.Load();
            var engine = new RulesEngine(pack);

            foreach (var definition in pack.AllUnits)
            {
                var state = SoloOnOpenGround(definition.Id, Hex.Zero);
                int allowance = definition.Stats.MoveInHexes;

                foreach (var hex in engine.ReachableHexes(state, new UnitId(1)).Keys
                                          .OrderBy(h => h.Q).ThenBy(h => h.R))
                {
                    var path = Movement.FindPath(state, new UnitId(1), hex, allowance);
                    var result = engine.Validate(state, new MoveUnit(PlayerId.A, new UnitId(1), path));

                    Assert.True(result.IsLegal,
                        $"{definition.Id} advertised {hex} but the move was refused: {result}");
                }
            }
        }
    }
}
