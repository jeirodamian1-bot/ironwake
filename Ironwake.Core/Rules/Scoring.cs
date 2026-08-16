using System;
using System.Collections.Generic;

namespace Ironwake.Core
{
    /// <summary>
    /// Objective control, scoring, and how a match is won.
    ///
    /// Shaped like the other rule modules: one predicate that validation, the client-facing
    /// query and the round-end resolution all call, so what a player is shown mid-round and
    /// what they are awarded at the end cannot disagree.
    /// </summary>
    public static class Scoring
    {
        /// <summary>How far from an objective a model still counts as holding it.</summary>
        public const int ControlRadiusHexes = 3;

        /// <summary>Reaching this ends the match immediately.</summary>
        public const int PointsToWin = 12;

        /// <summary>The last round, if nobody has won outright before it.</summary>
        public const int FinalRound = 5;

        /// <summary>
        /// Models a player has contributing to an objective.
        ///
        /// MODELS, not units: a five-strong squad counts five. Two rulings live here:
        ///
        /// SHAKEN MODELS DO NOT COUNT. A unit that has broken is not holding ground, and this
        /// is what gives morale teeth beyond the to-hit penalty — losing a nerve test can hand
        /// an objective to the enemy without a shot being fired.
        ///
        /// ENGAGED MODELS DO COUNT. Standing on an objective swinging at somebody is exactly
        /// what holding it looks like; melee should not evict you from the ground you are
        /// fighting over.
        /// </summary>
        public static int ContributionOf(GameState state, ObjectiveState objective, PlayerId player)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (objective == null) throw new ArgumentNullException(nameof(objective));

            int models = 0;
            foreach (var unit in state.Units)
            {
                if (!unit.IsAlive || unit.Owner != player) continue;
                if (unit.HasStatus(StatusKind.Shaken)) continue;
                if (objective.Position.DistanceTo(unit.Position) > ControlRadiusHexes) continue;

                models += unit.ModelsAlive;
            }
            return models;
        }

        /// <summary>
        /// Who holds an objective right now, or null when nobody does.
        ///
        /// Control needs STRICTLY more models than the opponent. Equal numbers are contested
        /// and score for neither side — a tie is a stand-off, not a win for whoever arrived
        /// first.
        /// </summary>
        public static PlayerId? ControllerOf(GameState state, ObjectiveState objective)
        {
            int a = ContributionOf(state, objective, PlayerId.A);
            int b = ContributionOf(state, objective, PlayerId.B);

            if (a > b) return PlayerId.A;
            if (b > a) return PlayerId.B;
            return null;
        }

        /// <summary>
        /// Who holds what right now, before any scoring resolves. The client uses this to
        /// shade the board mid-round.
        /// </summary>
        /// <remarks>
        /// Enumeration order of the result is not meaningful — sort by
        /// <see cref="ObjectiveId"/> before rendering anything order-dependent.
        /// </remarks>
        public static IReadOnlyDictionary<ObjectiveId, PlayerId?> ProjectedControl(GameState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));

            var projected = new Dictionary<ObjectiveId, PlayerId?>();
            foreach (var objective in state.Objectives)
                projected[objective.Id] = ControllerOf(state, objective);

            return projected;
        }

        /// <summary>
        /// Award points for every held objective and record who holds them.
        ///
        /// Scoring happens ONCE, at round end — not per action. Control can swing several
        /// times within a round as units move and break; only where it stands when the round
        /// closes is worth anything. <see cref="ObjectiveState.ControlledBy"/> is likewise a
        /// round-end record, which is why the live view is a separate query.
        ///
        /// Objectives resolve in id order so events and dice land in the same sequence on
        /// every machine.
        /// </summary>
        public static GameState ScoreRound(GameState state, List<GameEvent> events)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));

            var ordered = new List<ObjectiveState>(state.Objectives);
            ordered.Sort((x, y) => x.Id.Value.CompareTo(y.Id.Value));

            var updated = new List<ObjectiveState>(ordered.Count);
            int scoreA = state.ScoreA;
            int scoreB = state.ScoreB;

            foreach (var objective in ordered)
            {
                var holder = ControllerOf(state, objective);

                if (holder != objective.ControlledBy)
                    events?.Add(new ObjectiveControlChangedEvent(
                        objective.Id, objective.ControlledBy, holder));

                if (holder.HasValue)
                {
                    if (holder.Value == PlayerId.A) scoreA += objective.PointValue;
                    else scoreB += objective.PointValue;

                    events?.Add(new ObjectiveScoredEvent(holder.Value, objective.Id, objective.PointValue));
                }

                updated.Add(objective.WithControl(holder));
            }

            return state.With(objectives: updated, scoreA: scoreA, scoreB: scoreB);
        }

        /// <summary>
        /// Is the match over, and who won? Null winner with <c>true</c> is a draw.
        ///
        /// Evaluated in a fixed order, and the order is the rule:
        ///   1. a side with no living units loses immediately, whatever the score,
        ///   2. otherwise <see cref="PointsToWin"/> ends it the moment it is reached,
        ///   3. otherwise the match runs to the end of round <see cref="FinalRound"/> and the
        ///      higher score wins; equal is a draw.
        /// </summary>
        public static bool IsMatchOver(GameState state, bool atRoundEnd, out PlayerId? winner)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));

            bool aAlive = HasLivingUnits(state, PlayerId.A);
            bool bAlive = HasLivingUnits(state, PlayerId.B);

            // 1. Annihilation, checked continuously rather than only at round end.
            if (!aAlive || !bAlive)
            {
                winner = aAlive ? PlayerId.A : bAlive ? (PlayerId?)PlayerId.B : null;
                return true;
            }

            // 2. The points threshold. Only reachable at round end, since that is the only
            //    time anything scores, but checked here so the rule lives in one place.
            if (state.ScoreA >= PointsToWin || state.ScoreB >= PointsToWin)
            {
                winner = state.ScoreA > state.ScoreB ? PlayerId.A
                       : state.ScoreB > state.ScoreA ? (PlayerId?)PlayerId.B
                       : null;
                return true;
            }

            // 3. Time. A draw is a real outcome here, not a failure to decide.
            if (atRoundEnd && state.Round >= FinalRound)
            {
                winner = state.ScoreA > state.ScoreB ? PlayerId.A
                       : state.ScoreB > state.ScoreA ? (PlayerId?)PlayerId.B
                       : null;
                return true;
            }

            winner = null;
            return false;
        }

        private static bool HasLivingUnits(GameState state, PlayerId player)
        {
            foreach (var unit in state.Units)
                if (unit.Owner == player && unit.IsAlive) return true;
            return false;
        }
    }
}
