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

        /// <summary>
        /// Who held each objective after this action, AS THE ENGINE REPORTS IT. Captured
        /// rather than recomputed downstream: a consumer working control out for itself is
        /// the win rule bug again in a different costume.
        /// </summary>
        public IReadOnlyDictionary<ObjectiveId, PlayerId?> Control { get; }

        public RecordedStep(int index, GameAction action, IReadOnlyList<GameEvent> events,
                            int roundBefore, GameState stateAfter, ShotTrace shot = null,
                            IReadOnlyDictionary<ObjectiveId, PlayerId?> control = null)
        {
            Index = index;
            Action = action;
            Events = events ?? Array.Empty<GameEvent>();
            RoundBefore = roundBefore;
            StateAfter = stateAfter;
            Shot = shot;
            Control = control ?? new Dictionary<ObjectiveId, PlayerId?>();
        }
    }

    /// <summary>A complete match, replayable frame by frame.</summary>
    public sealed class MatchRecording
    {
        public ulong Seed { get; }
        public string ContentVersion { get; }

        /// <summary>Before any action was taken.</summary>
        public GameState InitialState { get; }

        /// <summary>Objective control at the start, as the engine reports it.</summary>
        public IReadOnlyDictionary<ObjectiveId, PlayerId?> InitialControl { get; }

        public IReadOnlyList<RecordedStep> Steps { get; }

        /// <summary>True if the match reached a conclusion rather than hitting the step guard.</summary>
        public bool Completed { get; }

        public MatchRecording(ulong seed, string contentVersion, GameState initialState,
                              IReadOnlyList<RecordedStep> steps, bool completed,
                              IReadOnlyDictionary<ObjectiveId, PlayerId?> initialControl = null)
        {
            Seed = seed;
            ContentVersion = contentVersion;
            InitialState = initialState;
            Steps = steps ?? Array.Empty<RecordedStep>();
            Completed = completed;
            InitialControl = initialControl ?? new Dictionary<ObjectiveId, PlayerId?>();
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



            var steps = new List<RecordedStep>();
            var state = initial;
            bool completed = false;

            // Captures the loop variable, so the default policy always sees the current state.
            Func<IReadOnlyList<GameAction>, GameAction> defaultPick =
                actions => MatchPolicy.Pick(state, actions);
            var choose = pick ?? defaultPick;

            while (state.Phase != PhaseKind.Complete && steps.Count < maxSteps)
            {
                var legal = engine.LegalActions(state, state.ActivePlayer);
                if (legal.Count == 0) break;

                var choice = choose(legal);

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
                    steps.Count, choice, result.Events, roundBefore, result.NextState, shot,
                    engine.ProjectedControl(result.NextState)));

                state = result.NextState;
                if (result.IsTerminal) { completed = true; break; }
            }

            if (state.Phase == PhaseKind.Complete) completed = true;

            return new MatchRecording(seed, initial.ContentVersion, initial, steps, completed,
                                      engine.ProjectedControl(initial));
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
