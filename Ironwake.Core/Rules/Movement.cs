using System;
using System.Collections.Generic;

namespace Ironwake.Core
{
    /// <summary>Why a hex cannot be entered. Maps onto the movement <see cref="ReasonCodes"/>.</summary>
    public enum HexBlock
    {
        /// <summary>Nothing stops the mover entering.</summary>
        None = 0,

        /// <summary>Past the edge of the board.</summary>
        OffBoard = 1,

        /// <summary>Terrain cannot be crossed.</summary>
        Impassable = 2,

        /// <summary>Another living unit stands there.</summary>
        Occupied = 3,
    }

    /// <summary>
    /// Movement reachability and pathfinding.
    ///
    /// Everything that asks "can this unit stand there?" or "how does it get there?" comes
    /// through here. That matters: before this existed, LegalActions built paths with
    /// <see cref="Hex.LineTo"/> — a straight line that ignores terrain and occupancy — while
    /// validation applied the real rules, so the engine offered moves it then refused. One
    /// blocking predicate shared by both is what stops that happening again.
    /// </summary>
    public static class Movement
    {
        /// <summary>
        /// The single blocking rule. Validation and pathfinding both call this, so they
        /// cannot disagree about what a legal destination is.
        /// </summary>
        /// <param name="mover">Its own hex never blocks it.</param>
        public static HexBlock BlockingReason(GameState state, UnitId mover, Hex hex)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));

            if (!state.Board.Contains(hex)) return HexBlock.OffBoard;
            if (!state.Board.IsPassable(hex)) return HexBlock.Impassable;

            // UnitAt already ignores destroyed units, so corpses do not block.
            var occupant = state.UnitAt(hex);
            if (occupant != null && occupant.Id != mover) return HexBlock.Occupied;

            return HexBlock.None;
        }

        /// <summary>
        /// Cost to enter a passable hex. Every hex costs 1 today.
        ///
        /// This is the ONE place terrain cost is decided — making Cover cost 2 is a change
        /// here and nowhere else. Note the searches below are breadth-first, which is only
        /// correct while every cost is 1; see the guard in <see cref="Step"/>.
        /// </summary>
        private static int MoveCostOf(GameState state, Hex hex) => 1;

        /// <summary>
        /// Cost of stepping into a hex, with the assumption BFS depends on made explicit.
        /// A silent switch to non-uniform costs would quietly start returning non-optimal
        /// paths, so it fails loudly instead.
        /// </summary>
        private static int Step(GameState state, Hex hex)
        {
            int cost = MoveCostOf(state, hex);
            if (cost != 1)
            {
                throw new NotSupportedException(
                    "Movement uses breadth-first search, which assumes every hex costs 1. " +
                    "MoveCostOf now returns " + cost + " — convert ReachableFrom and FindPath " +
                    "to a uniform-cost (Dijkstra) search before introducing variable terrain cost.");
            }
            return cost;
        }

        /// <summary>
        /// Every hex the unit can reach within its allowance, mapped to what it costs to get
        /// there. The unit's own starting hex is NOT included — it is not somewhere to move to.
        ///
        /// Blocked by board edge, impassable terrain, and any other living unit, friend or foe.
        /// </summary>
        /// <remarks>
        /// The returned dictionary's enumeration order is NOT meaningful. Callers that turn
        /// this into an ordered result — LegalActions, anything the client renders in sequence —
        /// must sort it explicitly.
        /// </remarks>
        public static IReadOnlyDictionary<Hex, int> ReachableFrom(
            GameState state, UnitId unit, int allowanceHexes)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));

            var reachable = new Dictionary<Hex, int>();

            var mover = state.GetUnit(unit);
            if (mover == null || allowanceHexes <= 0) return reachable;

            var start = mover.Position;
            var costAt = new Dictionary<Hex, int> { { start, 0 } };
            var frontier = new Queue<Hex>();
            frontier.Enqueue(start);

            while (frontier.Count > 0)
            {
                var current = frontier.Dequeue();
                int currentCost = costAt[current];

                for (int direction = 0; direction < 6; direction++)
                {
                    var next = current.Neighbour(direction);

                    if (costAt.ContainsKey(next)) continue;
                    if (BlockingReason(state, unit, next) != HexBlock.None) continue;

                    int cost = currentCost + Step(state, next);
                    if (cost > allowanceHexes) continue;

                    costAt[next] = cost;
                    reachable[next] = cost;
                    frontier.Enqueue(next);
                }
            }

            return reachable;
        }

        /// <summary>
        /// Shortest legal path from the unit's position to <paramref name="dest"/>, including
        /// both ends. Empty when the destination cannot be reached within the allowance.
        ///
        /// A path to the unit's own hex is a single hex — correct as a path, but not a legal
        /// <see cref="MoveUnit"/>, which needs at least two.
        /// </summary>
        /// <remarks>
        /// DETERMINISM. Several shortest paths usually exist; this always returns the same one.
        /// The search is breadth-first, each hex expands its neighbours in <see cref="Hex.Directions"/>
        /// order (E, NE, NW, W, SW, SE), and a hex keeps the first parent that reaches it.
        /// So where paths tie on length, the winner is the one whose earliest differing step
        /// takes the lowest direction index. No dictionary is ever enumerated to build the
        /// result — only looked up — so nothing here depends on hash order.
        /// </remarks>
        public static IReadOnlyList<Hex> FindPath(
            GameState state, UnitId unit, Hex dest, int allowanceHexes)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));

            var empty = Array.Empty<Hex>();

            var mover = state.GetUnit(unit);
            if (mover == null) return empty;

            var start = mover.Position;
            if (dest == start) return new[] { start };

            if (allowanceHexes <= 0) return empty;
            if (BlockingReason(state, unit, dest) != HexBlock.None) return empty;

            var costAt = new Dictionary<Hex, int> { { start, 0 } };
            var cameFrom = new Dictionary<Hex, Hex>();
            var frontier = new Queue<Hex>();
            frontier.Enqueue(start);

            bool found = false;
            while (frontier.Count > 0 && !found)
            {
                var current = frontier.Dequeue();
                int currentCost = costAt[current];

                for (int direction = 0; direction < 6; direction++)
                {
                    var next = current.Neighbour(direction);

                    if (costAt.ContainsKey(next)) continue;
                    if (BlockingReason(state, unit, next) != HexBlock.None) continue;

                    int cost = currentCost + Step(state, next);
                    if (cost > allowanceHexes) continue;

                    costAt[next] = cost;
                    cameFrom[next] = current;
                    frontier.Enqueue(next);

                    if (next == dest) { found = true; break; }
                }
            }

            if (!found) return empty;

            // Walk the parent links back, then reverse. Pure lookups, no enumeration.
            var reversed = new List<Hex>();
            var step = dest;
            reversed.Add(step);
            while (step != start)
            {
                step = cameFrom[step];
                reversed.Add(step);
            }
            reversed.Reverse();
            return reversed;
        }
    }
}
