using System;
using System.Collections.Generic;

namespace Ironwake.Core
{
    /// <summary>
    /// Round-end morale.
    ///
    /// A unit that lost models this round rolls a D6, adds the number lost, and compares the
    /// total to its Nerve. Over Nerve fails.
    ///
    /// Failure applies <see cref="StatusKind.Shaken"/> rather than removing models. Removing
    /// models for a failed test reads as arbitrary punishment when the player cannot see the
    /// tension building; a status they can see, plan around and recover from is legible.
    /// </summary>
    public static class Morale
    {
        /// <summary>Shaken units are -1 to hit, with everything else that implies.</summary>
        public const int ShakenModifier = -1;

        /// <summary>
        /// Does this roll pass? Pure, so the rule can be tested without an Rng.
        /// Passing is <c>die + modelsLost &lt;= nerve</c>; strictly over Nerve fails.
        /// </summary>
        public static bool Passes(int die, int modelsLost, int nerve) => die + modelsLost <= nerve;

        /// <summary>True if the unit has to test at all. No losses, no test.</summary>
        public static bool MustTest(UnitState unit) =>
            unit != null && unit.IsAlive && unit.ModelsLostThisRound > 0;

        /// <summary>
        /// Runs the whole round-end morale step and returns the new state.
        ///
        /// ORDER MATTERS, and it is what gives Shaken its one-round life:
        ///   1. clear Shaken from everyone — they have now had their round of it,
        ///   2. test every unit that lost models, applying fresh Shaken on a failure,
        ///   3. reset the per-round loss counters.
        /// So a unit shaken at the end of round N stays shaken through round N+1 and recovers
        /// at the end of it.
        ///
        /// Units are visited in <see cref="UnitId"/> order so the dice are consumed in the
        /// same sequence on every machine.
        /// </summary>
        public static GameState Resolve(
            GameState state, IContentPack content, Rng rng, List<GameEvent> events)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (content == null) throw new ArgumentNullException(nameof(content));
            if (rng == null) throw new ArgumentNullException(nameof(rng));

            var ordered = new List<UnitState>(state.Units);
            ordered.Sort((a, b) => a.Id.Value.CompareTo(b.Id.Value));

            var next = state;

            foreach (var snapshot in ordered)
            {
                // Re-read: an earlier iteration may have replaced this unit.
                var unit = next.GetUnit(snapshot.Id);
                if (unit == null) continue;

                // Step 1: last round's Shaken wears off.
                var recovered = unit.WithoutStatus(StatusKind.Shaken);

                // Step 2: test, if this round cost it anything.
                if (!MustTest(recovered))
                {
                    next = next.WithUnit(recovered.With(modelsLostThisRound: 0));
                    continue;
                }

                int nerve = content.GetUnit(recovered.DefinitionId).Stats.Nerve;
                int lost = recovered.ModelsLostThisRound;

                var roll = rng.RollD6(1);
                bool passed = Passes(roll[0], lost, nerve);

                // Morale is a single die against Nerve rather than a pool against a target,
                // so Successes is 1 for a pass and the "target" carried is the Nerve value.
                events.Add(new DiceRolledEvent(
                    RollKind.Morale, recovered.Id, UnitId.None,
                    nerve, nerve, Modifiers.None, roll, passed ? 1 : 0));

                var settled = recovered.With(modelsLostThisRound: 0);
                if (!passed)
                {
                    settled = settled.WithStatus(StatusKind.Shaken);
                    events.Add(new StatusAppliedEvent(settled.Id, StatusKind.Shaken));
                }

                next = next.WithUnit(settled);
            }

            return next;
        }
    }
}
