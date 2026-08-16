using System;
using System.Collections.Generic;
using System.Linq;

namespace Ironwake.Core
{
    /// <summary>
    /// TEMPORARY. Exists so the client can build the full
    /// tap → GameAction → Execute → animate GameEvents loop before the real rules land.
    ///
    /// It is deliberately simplified:
    ///   - flat statline for every unit (no content pack yet)
    ///   - no line of sight, no cover, no charge/melee, no morale
    ///   - objectives never score
    ///
    /// What it DOES do correctly is the shape: pure functions, seeded dice, events
    /// emitted for everything. When the real engine replaces this, the client should
    /// not need to change.
    ///
    /// DELETE THIS FILE once RulesEngine is complete. Do not build features on it.
    /// </summary>
    public sealed class StubEngine : IGameEngine
    {
        private readonly IContentPack _content;

        /// <summary>
        /// The one number still hardcoded. Wounding in the real ruleset compares a weapon's
        /// Power against the target's Resilience, and that rule does not exist yet — inventing
        /// it here would be adding a rule, not rewiring one. Until it lands every wound roll
        /// uses this flat target, and Power/Resilience stay authored but unread.
        /// </summary>
        private const int StubToWound = 4;  // 4+

        /// <summary>Turn structure rather than a statline, so it is not content's business.</summary>
        private const int ActionsPerActivation = 2;

        /// <summary>
        /// Cover makes a target one point harder to hit. A placeholder like
        /// <see cref="StubToWound"/> — the real modifier stack, where this becomes one entry
        /// among several that combine in a defined order, is still to come.
        /// </summary>
        private const int StubCoverPenalty = 1;

        /// <summary>Worst possible roll target. A 7+ on a d6 simply cannot be made.</summary>
        private const int WorstRollTarget = 7;

        /// <param name="content">Statlines come from here. Required — the engine holds none of its own.</param>
        public StubEngine(IContentPack content)
        {
            _content = content ?? throw new ArgumentNullException(nameof(content));
        }

        private UnitDefinition DefinitionOf(UnitState u) => _content.GetUnit(u.DefinitionId);

        /// <summary>
        /// A unit's primary weapon: the first in its authored list. The stub ignores
        /// <see cref="ShootAt.WeaponId"/> exactly as it always has — letting a player choose
        /// which weapon fires is a rule that does not exist yet.
        /// Null when a unit carries nothing, which reads downstream as zero range.
        /// </summary>
        private WeaponDefinition PrimaryWeaponOf(UnitState u)
        {
            var def = DefinitionOf(u);
            return def.WeaponIds.Count > 0 ? _content.GetWeapon(def.WeaponIds[0]) : null;
        }

        /// <summary>
        /// Move allowance in whole hexes, from content via <see cref="Measure"/>.
        /// Validation, LegalActions and ReachableHexes all derive it here so they cannot
        /// disagree about how far a unit can go.
        /// </summary>
        private int MoveAllowanceOf(UnitState u) => DefinitionOf(u).Stats.MoveInHexes;

        // ================= VALIDATE =================

        public ValidationResult Validate(GameState state, GameAction action)
        {
            if (state.Phase == PhaseKind.Complete)
                return ValidationResult.Illegal(ReasonCodes.MatchComplete, "The match is over.");

            if (action.Actor != state.ActivePlayer)
                return ValidationResult.Illegal(ReasonCodes.NotYourTurn, "It is not your turn.");

            switch (action)
            {
                case ActivateUnit a: return ValidateActivate(state, a);
                case MoveUnit m: return ValidateMove(state, m);
                case ShootAt s: return ValidateShoot(state, s);
                case EndActivation e: return ValidateEnd(state, e);
                case PassActivation _: return ValidationResult.Legal;
                default:
                    return ValidationResult.Illegal(ReasonCodes.UnknownAction,
                        $"The stub engine does not handle {action.Kind} yet.");
            }
        }

        private ValidationResult ValidateActivate(GameState s, ActivateUnit a)
        {
            var u = s.GetUnit(a.Unit);
            if (u == null) return ValidationResult.Illegal(ReasonCodes.UnitNotFound, "No such unit.");
            if (u.Owner != a.Actor) return ValidationResult.Illegal(ReasonCodes.NotYourUnit, "That unit is not yours.");
            if (!u.IsAlive) return ValidationResult.Illegal(ReasonCodes.UnitDead, "That unit is destroyed.");
            if (u.HasActivated) return ValidationResult.Illegal(ReasonCodes.AlreadyActivated, "That unit has already activated this round.");
            if (!s.ActiveUnit.IsNone) return ValidationResult.Illegal(ReasonCodes.NotActiveUnit, "Finish the current activation first.");
            return ValidationResult.Legal;
        }

        private ValidationResult ValidateMove(GameState s, MoveUnit m)
        {
            var u = s.GetUnit(m.Unit);
            if (u == null) return ValidationResult.Illegal(ReasonCodes.UnitNotFound, "No such unit.");
            if (u.Owner != m.Actor) return ValidationResult.Illegal(ReasonCodes.NotYourUnit, "That unit is not yours.");
            if (s.ActiveUnit != m.Unit) return ValidationResult.Illegal(ReasonCodes.NotActiveUnit, "Activate that unit first.");
            if (u.ActionsRemaining <= 0) return ValidationResult.Illegal(ReasonCodes.NoActionsRemaining, "No actions left.");

            if (m.Path == null || m.Path.Count < 2)
                return ValidationResult.Illegal(ReasonCodes.PathNotContiguous, "Path needs a start and an end.");
            if (m.Path[0] != u.Position)
                return ValidationResult.Illegal(ReasonCodes.PathNotContiguous, "Path must start at the unit's position.");
            int allowance = MoveAllowanceOf(u);
            if (m.Path.Count - 1 > allowance)
                return ValidationResult.Illegal(ReasonCodes.PathTooLong, $"Move is {allowance} hexes.");

            // The client's own path is validated step by step — we do not recompute a path
            // and compare, because two equally legal routes to the same hex must both be
            // accepted. The blocking rules come from Movement so that what LegalActions
            // offers and what this accepts can never drift apart.
            for (int i = 1; i < m.Path.Count; i++)
            {
                var h = m.Path[i];
                if (m.Path[i - 1].DistanceTo(h) != 1)
                    return ValidationResult.Illegal(ReasonCodes.PathNotContiguous, "Path must step one hex at a time.");

                switch (Movement.BlockingReason(s, u.Id, h))
                {
                    case HexBlock.OffBoard:
                        return ValidationResult.Illegal(ReasonCodes.OffBoard, "That hex is off the board.");
                    case HexBlock.Impassable:
                        return ValidationResult.Illegal(ReasonCodes.PathBlocked, "That hex is impassable.");
                    case HexBlock.Occupied:
                        return ValidationResult.Illegal(ReasonCodes.HexOccupied, "Another unit is there.");
                }
            }
            return ValidationResult.Legal;
        }

        private ValidationResult ValidateShoot(GameState s, ShootAt a)
        {
            var u = s.GetUnit(a.Unit);
            var t = s.GetUnit(a.Target);
            if (u == null || t == null) return ValidationResult.Illegal(ReasonCodes.UnitNotFound, "No such unit.");
            if (u.Owner != a.Actor) return ValidationResult.Illegal(ReasonCodes.NotYourUnit, "That unit is not yours.");
            if (s.ActiveUnit != a.Unit) return ValidationResult.Illegal(ReasonCodes.NotActiveUnit, "Activate that unit first.");
            if (u.ActionsRemaining <= 0) return ValidationResult.Illegal(ReasonCodes.NoActionsRemaining, "No actions left.");
            if (!t.IsAlive) return ValidationResult.Illegal(ReasonCodes.UnitDead, "That target is already destroyed.");
            if (t.Owner == u.Owner) return ValidationResult.Illegal(ReasonCodes.TargetFriendly, "You cannot shoot your own unit.");

            int range = PrimaryWeaponOf(u)?.RangeInHexes ?? 0;
            int dist = u.Position.DistanceTo(t.Position);
            if (dist > range)
                return ValidationResult.Illegal(ReasonCodes.OutOfRange, $"Out of range by {dist - range} hex(es).");

            // Same trace LegalActions and the client-facing query use, so all three agree.
            var los = LineOfSight.Trace(s, u.Position, t.Position);
            if (los.IsBlocked)
            {
                var where = los.BlockingHex.HasValue ? $" {los.BlockingHex.Value}" : string.Empty;
                return ValidationResult.Illegal(ReasonCodes.NoLineOfSight,
                    $"Line of sight is blocked by the terrain at{where}.");
            }

            return ValidationResult.Legal;
        }

        private ValidationResult ValidateEnd(GameState s, EndActivation e)
        {
            if (s.ActiveUnit.IsNone) return ValidationResult.Illegal(ReasonCodes.NotActiveUnit, "No unit is activated.");
            if (s.ActiveUnit != e.Unit) return ValidationResult.Illegal(ReasonCodes.NotActiveUnit, "That is not the active unit.");
            return ValidationResult.Legal;
        }

        // ================= EXECUTE =================

        public ExecutionResult Execute(GameState state, GameAction action)
        {
            var events = new List<GameEvent>();
            var rng = new Rng(state.Rng);
            GameState next;

            switch (action)
            {
                case ActivateUnit a: next = DoActivate(state, a, events); break;
                case MoveUnit m: next = DoMove(state, m, events); break;
                case ShootAt s: next = DoShoot(state, s, events, rng); break;
                case EndActivation e: next = DoEndActivation(state, e.Unit, events); break;
                case PassActivation _: next = DoEndActivation(state, UnitId.None, events); break;
                default: return new ExecutionResult(state, events);
            }

            next = next.With(rng: rng.Checkpoint(state.Rng));
            next = CheckRoundEnd(next, events);

            bool terminal = next.Phase == PhaseKind.Complete;
            if (terminal) events.Add(new MatchEndedEvent(WinnerOf(next)));

            return new ExecutionResult(next, events, terminal);
        }

        private GameState DoActivate(GameState s, ActivateUnit a, List<GameEvent> ev)
        {
            var u = s.GetUnit(a.Unit).With(actionsRemaining: ActionsPerActivation);
            ev.Add(new UnitActivatedEvent(a.Unit));
            return s.WithUnit(u).With(activeUnit: a.Unit);
        }

        private GameState DoMove(GameState s, MoveUnit m, List<GameEvent> ev)
        {
            var u = s.GetUnit(m.Unit);
            var dest = m.Path[m.Path.Count - 1];
            var moved = u.With(position: dest, actionsRemaining: u.ActionsRemaining - 1);
            ev.Add(new UnitMovedEvent(m.Unit, m.Path));
            return s.WithUnit(moved);
        }

        private GameState DoShoot(GameState s, ShootAt a, List<GameEvent> ev, Rng rng)
        {
            var shooter = s.GetUnit(a.Unit);
            var target = s.GetUnit(a.Target);

            int attacks = (PrimaryWeaponOf(shooter)?.Attacks ?? 0) * shooter.ModelsAlive;
            int saveTarget = DefinitionOf(target).Stats.Save;

            // Cover shifts the to-hit target. The roll's Purpose says so out loud rather than
            // the number quietly changing — a player watching the log has to be able to see
            // where a 4+ became a 5+.
            var los = LineOfSight.Trace(s, shooter.Position, target.Position);
            int baseToHit = DefinitionOf(shooter).Stats.Accuracy;
            int toHit = los.TargetInCover
                ? Math.Min(WorstRollTarget, baseToHit + StubCoverPenalty)
                : baseToHit;
            string hitPurpose = los.TargetInCover
                ? $"to-hit, cover -{StubCoverPenalty} (from {baseToHit}+)"
                : "to-hit";

            var hitRolls = rng.RollD6(attacks);
            int hits = Rng.CountSuccesses(hitRolls, toHit);
            ev.Add(new DiceRolledEvent(hitPurpose, toHit, hitRolls, hits));

            var woundRolls = rng.RollD6(hits);
            int wounds = Rng.CountSuccesses(woundRolls, StubToWound);
            ev.Add(new DiceRolledEvent("to-wound", StubToWound, woundRolls, wounds));

            var saveRolls = rng.RollD6(wounds);
            int saved = Rng.CountSuccesses(saveRolls, saveTarget);
            ev.Add(new DiceRolledEvent("save", saveTarget, saveRolls, saved));

            int damage = wounds - saved;
            ev.Add(new AttackResolvedEvent(a.Unit, a.Target, hits, wounds, saved, damage));

            var models = target.Models.ToList();
            for (int d = 0; d < damage; d++)
            {
                int idx = models.FindIndex(mm => !mm.IsSlain);
                if (idx < 0) break;
                models[idx] = models[idx].TakeDamage(1);
                if (models[idx].IsSlain) ev.Add(new ModelSlainEvent(a.Target, idx));
            }

            var newTarget = target.With(models: models);
            if (!newTarget.IsAlive) ev.Add(new UnitDestroyedEvent(a.Target));

            var newShooter = shooter.With(actionsRemaining: shooter.ActionsRemaining - 1);
            return s.WithUnit(newShooter).WithUnit(newTarget);
        }

        private GameState DoEndActivation(GameState s, UnitId unit, List<GameEvent> ev)
        {
            var next = s;
            if (!unit.IsNone)
            {
                var u = s.GetUnit(unit);
                if (u != null) next = next.WithUnit(u.With(hasActivated: true, actionsRemaining: 0));
            }
            var handover = s.ActivePlayer.Opponent;
            ev.Add(new ActivationEndedEvent(unit, handover));
            return next.With(activePlayer: handover, activeUnit: UnitId.None);
        }

        private GameState CheckRoundEnd(GameState s, List<GameEvent> ev)
        {
            bool anyLeft = s.Units.Any(u => u.IsAlive && !u.HasActivated);
            if (anyLeft) return s;

            ev.Add(new RoundEndedEvent(s.Round, s.ScoreA, s.ScoreB));

            // Match ends after 5 rounds, or when one side is wiped out.
            bool aDead = !s.UnitsOf(PlayerId.A).Any();
            bool bDead = !s.UnitsOf(PlayerId.B).Any();
            if (s.Round >= 5 || aDead || bDead)
                return s.With(phase: PhaseKind.Complete);

            var reset = s.Units.Select(u => u.With(hasActivated: false, actionsRemaining: 0)).ToList();
            return s.With(round: s.Round + 1, units: reset, activeUnit: UnitId.None);
        }

        private static PlayerId? WinnerOf(GameState s)
        {
            bool aAlive = s.UnitsOf(PlayerId.A).Any();
            bool bAlive = s.UnitsOf(PlayerId.B).Any();
            if (aAlive && !bAlive) return PlayerId.A;
            if (bAlive && !aAlive) return PlayerId.B;
            if (s.ScoreA > s.ScoreB) return PlayerId.A;
            if (s.ScoreB > s.ScoreA) return PlayerId.B;
            return null;
        }

        // ================= LEGAL ACTIONS =================

        public IReadOnlyList<GameAction> LegalActions(GameState state, PlayerId player)
        {
            var list = new List<GameAction>();
            if (state.Phase == PhaseKind.Complete || state.ActivePlayer != player) return list;

            if (state.ActiveUnit.IsNone)
            {
                foreach (var u in state.UnitsOf(player).Where(u => !u.HasActivated))
                    list.Add(new ActivateUnit(player, u.Id));
                list.Add(new PassActivation(player));
                return list;
            }

            var active = state.GetUnit(state.ActiveUnit);
            if (active == null) return list;

            if (active.ActionsRemaining > 0)
            {
                var activeDef = DefinitionOf(active);

                // Real pathfinding, not a straight line. LineTo walks through walls and other
                // units, so it used to offer moves that ValidateMove then refused.
                int allowance = MoveAllowanceOf(active);
                var reachable = Movement.ReachableFrom(state, active.Id, allowance);

                // Sorted explicitly: dictionary enumeration order is not guaranteed, and this
                // list's order decides what a client highlights first and what an AI picks.
                foreach (var hex in reachable.Keys.OrderBy(h => h.Q).ThenBy(h => h.R))
                {
                    var path = Movement.FindPath(state, active.Id, hex, allowance);
                    if (path.Count < 2) continue;
                    list.Add(new MoveUnit(player, active.Id, path));
                }

                var weaponId = activeDef.WeaponIds.Count > 0 ? activeDef.WeaponIds[0] : null;
                foreach (var t in state.UnitsOf(player.Opponent))
                {
                    var shot = new ShootAt(player, active.Id, t.Id, weaponId);
                    if (Validate(state, shot).IsLegal) list.Add(shot);
                }
            }

            list.Add(new EndActivation(player, active.Id));
            return list;
        }

        /// <inheritdoc />
        public IReadOnlyDictionary<Hex, int> ReachableHexes(GameState state, UnitId unit)
        {
            var u = state.GetUnit(unit);
            if (u == null || !u.IsAlive) return new Dictionary<Hex, int>();

            return Movement.ReachableFrom(state, unit, MoveAllowanceOf(u));
        }

        /// <inheritdoc />
        public LosResult CheckLineOfSight(GameState state, UnitId shooter, UnitId target)
        {
            var from = state.GetUnit(shooter);
            var to = state.GetUnit(target);
            if (from == null || to == null) return LosResult.NoSuchUnit;

            return LineOfSight.Trace(state, from.Position, to.Position);
        }
    }
}
