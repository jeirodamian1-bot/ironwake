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
        /// SHAKEN MODELS COUNT HALF, ROUNDED DOWN. A broken unit is holding the ground badly,
        /// not evaporating off it: five shaken models contribute two. They used to contribute
        /// nothing, which stacked three penalties onto one failed nerve test — the to-hit
        /// modifier, no charging, AND total loss of the objective. Half weight keeps morale
        /// meaningful while leaving a unit that breaks on the board rather than off it.
        /// A single shaken model contributes nothing, which is the rounding, not a rule.
        ///
        /// ENGAGED MODELS COUNT IN FULL. Standing on an objective swinging at somebody is
        /// exactly what holding it looks like; melee should not evict you from the ground you
        /// are fighting over.
        /// </summary>
        public static int ContributionOf(GameState state, ObjectiveState objective, PlayerId player)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (objective == null) throw new ArgumentNullException(nameof(objective));

            int models = 0;
            foreach (var unit in state.Units)
            {
                if (!unit.IsAlive || unit.Owner != player) continue;
                if (objective.Position.DistanceTo(unit.Position) > ControlRadiusHexes) continue;

                models += unit.HasStatus(StatusKind.Shaken)
                    ? unit.ModelsAlive / ShakenControlDivisor   // integer division: rounds down
                    : unit.ModelsAlive;
            }
            return models;
        }

        /// <summary>Shaken models count at one over this. See <see cref="ContributionOf"/>.</summary>
        public const int ShakenControlDivisor = 2;

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
        ///   1. <see cref="PointsToWin"/> ends it the moment it is reached,
        ///   2. otherwise the match runs to the end of round <see cref="FinalRound"/> and the
        ///      higher score wins,
        ///   3. otherwise a side with no living units ends the match NOW — and it is still
        ///      decided on score.
        ///
        /// THE RULING, and it is the one that decides what game this is: ANNIHILATION ENDS
        /// THE MATCH BUT DOES NOT WIN IT. Wiping the enemy out while behind on points is a
        /// loss. This is a mission-objective game, not an elimination game, and the previous
        /// ordering — where a wipe won outright — made objectives decorative: a faction could
        /// out-score its opponent three to one and still lose three matches in four.
        ///
        /// A consequence worth knowing: a wipe stops the match immediately, so the wiping
        /// player forfeits whatever they would have scored when that round closed. Killing
        /// the last enemy model while one point behind loses the match.
        ///
        /// Every branch decides on score, and equal scores are a draw in all of them — a draw
        /// is a real outcome here, not a failure to decide.
        /// </summary>
        public static bool IsMatchOver(GameState state, bool atRoundEnd, out PlayerId? winner)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));

            // 1. The points threshold. Only reachable at round end, since that is the only
            //    time anything scores, but checked here so the rule lives in one place.
            if (state.ScoreA >= PointsToWin || state.ScoreB >= PointsToWin)
            {
                winner = HigherScorer(state);
                return true;
            }

            // 2. Time.
            if (atRoundEnd && state.Round >= FinalRound)
            {
                winner = HigherScorer(state);
                return true;
            }

            // 3. Annihilation, checked continuously rather than only at round end. It ends
            //    the match; the score decides it.
            if (!HasLivingUnits(state, PlayerId.A) || !HasLivingUnits(state, PlayerId.B))
            {
                winner = HigherScorer(state);
                return true;
            }

            winner = null;
            return false;
        }

        /// <summary>Whoever is ahead, or null when level. Every ending is decided this way.</summary>
        private static PlayerId? HigherScorer(GameState state) =>
            state.ScoreA > state.ScoreB ? PlayerId.A
          : state.ScoreB > state.ScoreA ? (PlayerId?)PlayerId.B
          : null;

        private static bool HasLivingUnits(GameState state, PlayerId player)
        {
            foreach (var unit in state.Units)
                if (unit.Owner == player && unit.IsAlive) return true;
            return false;
        }
    }
}
