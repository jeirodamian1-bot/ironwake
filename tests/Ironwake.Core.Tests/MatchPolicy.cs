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
        public static GameAction Pick(IReadOnlyList<GameAction> legal)
        {
            // Mirrors Ironwake.Console's MatchPolicy: aggressive, preferring to close, so
            // that match-driving tests actually exercise charge and melee.
            var fight = legal.FirstOrDefault(a => a is FightUnit);
            if (fight != null) return fight;

            // Shoot before charging. A unit with no melee weapon may legally charge, but the
            // free fight does nothing, so charging with one spends the whole activation to
            // deal zero damage. Preferring the charge made the sweep measure that blunder
            // rather than the game.
            var shoot = legal.FirstOrDefault(a => a is ShootAt);
            if (shoot != null) return shoot;

            var charge = legal.FirstOrDefault(a => a is ChargeAt);
            if (charge != null) return charge;

            var activate = legal.FirstOrDefault(a => a is ActivateUnit);
            if (activate != null) return activate;

            var move = legal.OfType<MoveUnit>().OrderByDescending(m => m.Path.Count).FirstOrDefault();
            if (move != null) return move;

            return legal[legal.Count - 1];
        }
    }
}
