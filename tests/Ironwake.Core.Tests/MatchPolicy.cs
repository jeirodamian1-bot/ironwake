using System.Collections.Generic;
using System.Linq;
using Ironwake.Core;

namespace Ironwake.Core.Tests
{
    /// <summary>
    /// The console harness's action policy, in one place so tests that drive a match cannot
    /// drift apart from each other.
    ///
    /// Picking ActivateUnit explicitly matters. Fall through to the last legal action and you
    /// get PassActivation forever: no unit activates, no dice roll, and a match-driving test
    /// silently stops testing anything.
    /// </summary>
    internal static class MatchPolicy
    {
        /// <summary>
        /// With a state in hand the policy can play the actual win condition: secure an
        /// objective first, then fight from it.
        ///
        /// Without this the policy was a pure combat AI, blind to objectives — it shot
        /// whenever anything was in range and never walked anywhere. That made "Ashguard
        /// have no reason to leave their deployment zone" true of the HARNESS rather than of
        /// the game, and no amount of board or content tuning could have been measured
        /// through it.
        /// </summary>
        public static GameAction Pick(GameState state, IReadOnlyList<GameAction> legal)
        {
            // Already in melee: swing, it costs one action and needs no approach.
            var fight = legal.FirstOrDefault(a => a is FightUnit);
            if (fight != null) return fight;

            var seize = MoveTowardObjective(state, legal);
            if (seize != null) return seize;

            // Shoot before charging. A unit with no melee weapon may legally charge, but the
            // free fight does nothing, so charging with one spends the whole activation to
            // deal zero damage.
            var shoot = legal.FirstOrDefault(a => a is ShootAt);
            if (shoot != null) return shoot;

            var charge = legal.FirstOrDefault(a => a is ChargeAt);
            if (charge != null) return charge;

            var activate = legal.FirstOrDefault(a => a is ActivateUnit);
            if (activate != null) return activate;

            // Holding ground with nothing to shoot: stand still. The longest-move fallback
            // below would otherwise walk the unit straight off the objective it is scoring.
            if (IsHoldingAnObjective(state))
            {
                var stand = legal.FirstOrDefault(a => a is EndActivation);
                if (stand != null) return stand;
            }

            var move = legal.OfType<MoveUnit>().OrderByDescending(m => m.Path.Count).FirstOrDefault();
            if (move != null) return move;

            return legal[legal.Count - 1];
        }


        /// <summary>True if the active unit stands within control range of any objective.</summary>
        private static bool IsHoldingAnObjective(GameState state)
        {
            if (state == null || state.ActiveUnit.IsNone) return false;

            var mover = state.GetUnit(state.ActiveUnit);
            if (mover == null) return false;

            foreach (var objective in state.Objectives)
                if (objective.Position.DistanceTo(mover.Position) <= Scoring.ControlRadiusHexes)
                    return true;
            return false;
        }
        /// <summary>
        /// The move that gets the active unit closest to the nearest objective it is not
        /// already holding, or null if it is in range of one or cannot improve.
        /// Fully deterministic: nearest objective by distance then id, then the move that
        /// closes the most ground, ties broken by destination hex.
        /// </summary>
        private static GameAction MoveTowardObjective(GameState state, IReadOnlyList<GameAction> legal)
        {
            if (state == null || state.ActiveUnit.IsNone) return null;

            var mover = state.GetUnit(state.ActiveUnit);
            if (mover == null || state.Objectives.Count == 0) return null;

            // Standing on an objective already: hold it and fight from here.
            if (IsHoldingAnObjective(state)) return null;

            var moves = legal.OfType<MoveUnit>().ToList();
            if (moves.Count == 0) return null;

            var target = state.Objectives
                .OrderBy(o => o.Position.DistanceTo(mover.Position))
                .ThenBy(o => o.Id.Value)
                .First();

            int here = target.Position.DistanceTo(mover.Position);

            return moves
                .Select(m => new { Move = m, To = m.Path[m.Path.Count - 1] })
                .Select(x => new { x.Move, x.To, Distance = target.Position.DistanceTo(x.To) })
                .Where(x => x.Distance < here)
                .OrderBy(x => x.Distance)
                .ThenByDescending(x => x.Move.Path.Count)
                .ThenBy(x => x.To.Q).ThenBy(x => x.To.R)
                .Select(x => (GameAction)x.Move)
                .FirstOrDefault();
        }
    }
}
