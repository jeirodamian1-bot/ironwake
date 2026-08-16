using System;
using System.Collections.Generic;
using System.Linq;
using Ironwake.Core;

namespace Ironwake.ConsoleHarness.Viz
{
    /// <summary>
    /// The harness's action policy: fight, else charge, else shoot, else activate, else take
    /// the longest available move, else end the activation.
    ///
    /// Deliberately aggressive rather than clever. It prefers closing to shooting because
    /// that is what exercises the melee rules — a shooting-first policy leaves charge offered
    /// and never taken, so the whole system goes untested in practice. It is not an AI and
    /// makes no attempt to pick the higher-damage option.
    ///
    /// Picking ActivateUnit explicitly matters — fall through to the last legal action and
    /// you get PassActivation forever, so nothing ever happens.
    /// </summary>
    public static class MatchPolicy
    {
        public static GameAction Pick(IReadOnlyList<GameAction> legal)
        {
            // Already in melee: swing, it costs one action and needs no approach.
            var fight = legal.FirstOrDefault(a => a is FightUnit);
            if (fight != null) return fight;

            var charge = legal.FirstOrDefault(a => a is ChargeAt);
            if (charge != null) return charge;

            var shoot = legal.FirstOrDefault(a => a is ShootAt);
            if (shoot != null) return shoot;

            var activate = legal.FirstOrDefault(a => a is ActivateUnit);
            if (activate != null) return activate;

            var move = legal.OfType<MoveUnit>().OrderByDescending(m => m.Path.Count).FirstOrDefault();
            if (move != null) return move;

            return legal[legal.Count - 1];
        }
    }

    /// <summary>
    /// The sight line behind a shot, kept so the viewer can draw what the engine decided
    /// rather than re-deriving it. The engine is asked; nothing here works it out.
    /// </summary>
    public sealed class ShotTrace
    {
        public Hex From { get; }
        public Hex To { get; }
        public bool Blocked { get; }
        public Hex? BlockingHex { get; }
        public bool TargetInCover { get; }

        public ShotTrace(Hex from, Hex to, bool blocked, Hex? blockingHex, bool targetInCover)
        {
            From = from;
            To = to;
            Blocked = blocked;
            BlockingHex = blockingHex;
            TargetInCover = targetInCover;
        }
    }

    /// <summary>One executed action and everything that came of it.</summary>
    public sealed class RecordedStep
    {
        /// <summary>0-based position in the match.</summary>
        public int Index { get; }

        /// <summary>The action submitted to the engine.</summary>
        public GameAction Action { get; }

        /// <summary>Everything the engine emitted in response, in order.</summary>
        public IReadOnlyList<GameEvent> Events { get; }

        /// <summary>The round the action was taken IN — the state after it may have moved on.</summary>
        public int RoundBefore { get; }

        /// <summary>Full snapshot after the action resolved.</summary>
        public GameState StateAfter { get; }

        /// <summary>Set only when the action was a shot.</summary>
        public ShotTrace Shot { get; }

        public RecordedStep(int index, GameAction action, IReadOnlyList<GameEvent> events,
                            int roundBefore, GameState stateAfter, ShotTrace shot = null)
        {
            Index = index;
            Action = action;
            Events = events ?? Array.Empty<GameEvent>();
            RoundBefore = roundBefore;
            StateAfter = stateAfter;
            Shot = shot;
        }
    }

    /// <summary>A complete match, replayable frame by frame.</summary>
    public sealed class MatchRecording
    {
        public ulong Seed { get; }
        public string ContentVersion { get; }

        /// <summary>Before any action was taken.</summary>
        public GameState InitialState { get; }

        public IReadOnlyList<RecordedStep> Steps { get; }

        /// <summary>True if the match reached a conclusion rather than hitting the step guard.</summary>
        public bool Completed { get; }

        public MatchRecording(ulong seed, string contentVersion, GameState initialState,
                              IReadOnlyList<RecordedStep> steps, bool completed)
        {
            Seed = seed;
            ContentVersion = contentVersion;
            InitialState = initialState;
            Steps = steps ?? Array.Empty<RecordedStep>();
            Completed = completed;
        }

        /// <summary>Final state — after the last step, or the initial state if nothing happened.</summary>
        public GameState FinalState =>
            Steps.Count > 0 ? Steps[Steps.Count - 1].StateAfter : InitialState;
    }

    /// <summary>
    /// Plays a match and keeps every intermediate state so it can be scrubbed through later.
    ///
    /// This is presentation scaffolding, not engine behaviour — it only ever calls the public
    /// IGameEngine surface, exactly as a client would.
    /// </summary>
    public static class MatchRecorder
    {
        /// <param name="maxSteps">Safety guard, matching the console harness's own.</param>
        public static MatchRecording Record(
            IGameEngine engine,
            GameState initial,
            ulong seed,
            Func<IReadOnlyList<GameAction>, GameAction> pick = null,
            int maxSteps = 500)
        {
            if (engine == null) throw new ArgumentNullException(nameof(engine));
            if (initial == null) throw new ArgumentNullException(nameof(initial));

            pick ??= MatchPolicy.Pick;

            var steps = new List<RecordedStep>();
            var state = initial;
            bool completed = false;

            while (state.Phase != PhaseKind.Complete && steps.Count < maxSteps)
            {
                var legal = engine.LegalActions(state, state.ActivePlayer);
                if (legal.Count == 0) break;

                var choice = pick(legal);

                // The recorder trusts the engine no more than a client would.
                var check = engine.Validate(state, choice);
                if (!check.IsLegal)
                    throw new InvalidOperationException(
                        $"The engine offered an action it then refused: {choice} → {check}");

                int roundBefore = state.Round;

                // Captured BEFORE execution, while the shooter and target are still where the
                // engine saw them — a destroyed target would otherwise be traced post-mortem.
                var shot = TraceShot(engine, state, choice);

                var result = engine.Execute(state, choice);

                steps.Add(new RecordedStep(
                    steps.Count, choice, result.Events, roundBefore, result.NextState, shot));

                state = result.NextState;
                if (result.IsTerminal) { completed = true; break; }
            }

            if (state.Phase == PhaseKind.Complete) completed = true;

            return new MatchRecording(seed, initial.ContentVersion, initial, steps, completed);
        }

        /// <summary>Ask the engine about the sight line, for shots only.</summary>
        private static ShotTrace TraceShot(IGameEngine engine, GameState state, GameAction action)
        {
            if (!(action is ShootAt shot)) return null;

            var shooter = state.GetUnit(shot.Unit);
            var target = state.GetUnit(shot.Target);
            if (shooter == null || target == null) return null;

            var los = engine.CheckLineOfSight(state, shot.Unit, shot.Target);

            return new ShotTrace(
                shooter.Position, target.Position, los.IsBlocked, los.BlockingHex, los.TargetInCover);
        }
    }
}
