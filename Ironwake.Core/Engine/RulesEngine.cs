using System;
using System.Collections.Generic;
using System.Linq;

namespace Ironwake.Core
{
    /// <summary>
    /// The rules engine: the authority on what is legal and what happens.
    ///
    /// Pure throughout. <see cref="Validate"/> is side-effect free and cheap enough to call
    /// on every mouse move; <see cref="Execute"/> takes a state and an action and returns a
    /// new state plus the events that explain it. Nothing is mutated, no clock is read, and
    /// every die comes from the seeded <see cref="Rng"/> carried in the state — which is what
    /// lets the same assembly run in a client and on an authoritative server and agree.
    ///
    /// It holds no game data of its own. Statlines, weapons and points all come from
    /// <see cref="IContentPack"/>; the rules themselves live in the Rules namespace
    /// (<see cref="Movement"/>, <see cref="LineOfSight"/>, <see cref="Wounding"/>,
    /// <see cref="Melee"/>, <see cref="Morale"/>, <see cref="Scoring"/>) and this class
    /// sequences them.
    /// </summary>
    public sealed class RulesEngine : IGameEngine
    {
        private readonly IContentPack _content;

        /// <summary>
        /// Actions in an activation. Turn structure rather than a statline, so it is the
        /// engine's business rather than content's — until a unit exists that gets three.
        /// </summary>
        private const int ActionsPerActivation = 2;

        /// <summary>
        /// Cover is -1 to hit. A rule, not a stat: it describes what terrain does, not what
        /// any particular unit is.
        /// </summary>
        private const int CoverModifier = -1;

        /// <param name="content">Statlines come from here. Required — the engine holds none of its own.</param>
        public RulesEngine(IContentPack content)
        {
            _content = content ?? throw new ArgumentNullException(nameof(content));
        }

        private UnitDefinition DefinitionOf(UnitState u) => _content.GetUnit(u.DefinitionId);

        /// <summary>
        /// A unit's primary weapon: the first in its authored list. <see cref="ShootAt.WeaponId"/>
        /// is deliberately ignored — letting a player choose which weapon fires is a rule that
        /// does not exist yet, and guessing at it here would be inventing one.
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
                case ChargeAt c: return ValidateCharge(state, c);
                case FightUnit f: return ValidateFight(state, f);
                case EndActivation e: return ValidateEnd(state, e);
                case PassActivation _: return ValidationResult.Legal;
                default:
                    return ValidationResult.Illegal(ReasonCodes.UnknownAction,
                        $"{action.Kind} is not implemented.");
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

            // Locked in melee: you may swing or walk away, but not shoot past the enemy in
            // your face.
            if (u.HasStatus(StatusKind.Engaged))
                return ValidationResult.Illegal(ReasonCodes.UnitEngaged,
                    "That unit is engaged in melee and cannot shoot.");

            var weapon = PrimaryWeaponOf(u);
            if (weapon == null)
                return ValidationResult.Illegal(ReasonCodes.NoWeapon, "That unit carries no weapon.");

            // Range 0 is what content uses to mean melee. Shooting with a maul is not a
            // long shot, it is a category error, so it gets its own refusal.
            if (weapon.Range <= 0)
                return ValidationResult.Illegal(ReasonCodes.WeaponIsMelee,
                    $"{weapon.DisplayName} is a melee weapon and cannot be fired.");

            int range = weapon.RangeInHexes;
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

        /// <summary>
        /// A charge is a move that ends beside the target and turns into a fight, so it
        /// reuses the movement and sight rules rather than inventing its own.
        /// </summary>
        private ValidationResult ValidateCharge(GameState s, ChargeAt a)
        {
            var u = s.GetUnit(a.Unit);
            var t = s.GetUnit(a.Target);
            if (u == null || t == null) return ValidationResult.Illegal(ReasonCodes.UnitNotFound, "No such unit.");
            if (u.Owner != a.Actor) return ValidationResult.Illegal(ReasonCodes.NotYourUnit, "That unit is not yours.");
            if (s.ActiveUnit != a.Unit) return ValidationResult.Illegal(ReasonCodes.NotActiveUnit, "Activate that unit first.");
            if (!t.IsAlive) return ValidationResult.Illegal(ReasonCodes.UnitDead, "That target is already destroyed.");
            if (t.Owner == u.Owner) return ValidationResult.Illegal(ReasonCodes.TargetFriendly, "You cannot charge your own unit.");

            // Charging costs both actions, so a unit that has already acted cannot start one.
            if (u.ActionsRemaining < ActionsPerActivation)
                return ValidationResult.Illegal(ReasonCodes.NoActionsRemaining,
                    "A charge takes the whole activation.");

            if (u.HasStatus(StatusKind.Shaken))
                return ValidationResult.Illegal(ReasonCodes.UnitShaken, "A shaken unit will not charge.");

            var los = LineOfSight.Trace(s, u.Position, t.Position);
            if (los.IsBlocked)
            {
                var where = los.BlockingHex.HasValue ? $" {los.BlockingHex.Value}" : string.Empty;
                return ValidationResult.Illegal(ReasonCodes.NoLineOfSight,
                    $"Line of sight is blocked by the terrain at{where}.");
            }

            int allowance = MoveAllowanceOf(u);

            // An empty path means "work it out for me". A supplied one is checked step by
            // step, exactly as ValidateMove checks a move — two equally legal run-ins to the
            // same hex must both be accepted.
            if (a.Path.Count == 0)
            {
                if (!Melee.FindApproach(s, a.Unit, a.Target, allowance).IsPossible)
                    return ValidationResult.Illegal(ReasonCodes.NoChargePath,
                        "No route to a hex beside that unit within its move.");

                return ValidationResult.Legal;
            }

            if (a.Path[0] != u.Position)
                return ValidationResult.Illegal(ReasonCodes.PathNotContiguous,
                    "Path must start at the unit's position.");
            if (a.Path.Count - 1 > allowance)
                return ValidationResult.Illegal(ReasonCodes.PathTooLong, $"Move is {allowance} hexes.");

            for (int i = 1; i < a.Path.Count; i++)
            {
                var step = a.Path[i];
                if (a.Path[i - 1].DistanceTo(step) != 1)
                    return ValidationResult.Illegal(ReasonCodes.PathNotContiguous,
                        "Path must step one hex at a time.");

                switch (Movement.BlockingReason(s, u.Id, step))
                {
                    case HexBlock.OffBoard:
                        return ValidationResult.Illegal(ReasonCodes.OffBoard, "That hex is off the board.");
                    case HexBlock.Impassable:
                        return ValidationResult.Illegal(ReasonCodes.PathBlocked, "That hex is impassable.");
                    case HexBlock.Occupied:
                        return ValidationResult.Illegal(ReasonCodes.HexOccupied, "Another unit is there.");
                }
            }

            // A charge has to end in contact, or it is just a move.
            if (a.Path[a.Path.Count - 1].DistanceTo(t.Position) != 1)
                return ValidationResult.Illegal(ReasonCodes.NoChargePath,
                    "A charge must end beside its target.");

            return ValidationResult.Legal;
        }

        private ValidationResult ValidateFight(GameState s, FightUnit a)
        {
            var u = s.GetUnit(a.Unit);
            var t = s.GetUnit(a.Target);
            if (u == null || t == null) return ValidationResult.Illegal(ReasonCodes.UnitNotFound, "No such unit.");
            if (u.Owner != a.Actor) return ValidationResult.Illegal(ReasonCodes.NotYourUnit, "That unit is not yours.");
            if (s.ActiveUnit != a.Unit) return ValidationResult.Illegal(ReasonCodes.NotActiveUnit, "Activate that unit first.");
            if (u.ActionsRemaining <= 0) return ValidationResult.Illegal(ReasonCodes.NoActionsRemaining, "No actions left.");
            if (!t.IsAlive) return ValidationResult.Illegal(ReasonCodes.UnitDead, "That target is already destroyed.");
            if (t.Owner == u.Owner) return ValidationResult.Illegal(ReasonCodes.TargetFriendly, "You cannot fight your own unit.");

            if (!Melee.AreAdjacent(u, t))
                return ValidationResult.Illegal(ReasonCodes.NotAdjacent, "That unit is not adjacent.");

            if (Melee.MeleeWeaponOf(_content, u) == null)
                return ValidationResult.Illegal(ReasonCodes.NoMeleeWeapon,
                    "That unit carries no melee weapon.");

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
                case ChargeAt c: next = DoCharge(state, c, events, rng); break;
                case FightUnit f: next = DoFight(state, f.Unit, f.Target, events, rng, spendAction: true); break;
                case EndActivation e: next = DoEndActivation(state, e.Unit, events); break;
                case PassActivation _: next = DoEndActivation(state, UnitId.None, events); break;
                default: return new ExecutionResult(state, events);
            }

            // Engagement is derived from adjacency, so it is recomputed after anything that
            // could have moved or killed someone rather than maintained by hand.
            next = Melee.RefreshEngagement(next);

            // Round end can roll morale, so the checkpoint has to be taken after it or the
            // stored RNG position would under-count the dice actually consumed.
            next = CheckRoundEnd(next, events, rng);
            next = next.With(rng: rng.Checkpoint(state.Rng));

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

        /// <summary>
        /// Roll a pool against a statline number, emit the event, return the successes.
        ///
        /// Every roll in the engine goes through here, so modifier application and the
        /// impossible-target rule cannot be done differently in two places.
        /// </summary>
        private static int Roll(
            Rng rng, List<GameEvent> ev, RollKind kind, UnitId roller, UnitId target,
            int baseTarget, IReadOnlyList<RollModifier> modifiers, int diceCount)
        {
            int finalTarget = Modifiers.FinalTarget(baseTarget, modifiers);

            // Content writes 7 to mean "cannot" — an unarmoured unit's save, for one. No dice
            // are rolled at all in that case, rather than rolling a pool that could never
            // succeed. The event is still emitted so the log shows the attempt.
            if (Modifiers.IsImpossible(baseTarget))
            {
                ev.Add(new DiceRolledEvent(
                    kind, roller, target, baseTarget, finalTarget, modifiers,
                    Array.Empty<int>(), 0));
                return 0;
            }

            var rolls = rng.RollD6(diceCount);
            int successes = Rng.CountSuccesses(rolls, finalTarget);

            ev.Add(new DiceRolledEvent(
                kind, roller, target, baseTarget, finalTarget, modifiers, rolls, successes));

            return successes;
        }

        private GameState DoShoot(GameState s, ShootAt a, List<GameEvent> ev, Rng rng)
        {
            var shooter = s.GetUnit(a.Unit);
            var target = s.GetUnit(a.Target);

            var los = LineOfSight.Trace(s, shooter.Position, target.Position);

            var hitModifiers = HitModifiers(shooter);
            if (los.TargetInCover)
                hitModifiers.Add(new RollModifier(ModifierSource.Cover, CoverModifier));

            return ResolveAttack(
                s, a.Unit, a.Target, PrimaryWeaponOf(shooter),
                DefinitionOf(shooter).Stats.Accuracy, hitModifiers, ev, rng, spendAction: true);
        }

        /// <summary>
        /// A melee exchange. Wounding, saves and damage go through exactly the same code as
        /// shooting — only the to-hit stat and the modifier list differ.
        /// </summary>
        private GameState DoFight(
            GameState s, UnitId attackerId, UnitId targetId,
            List<GameEvent> ev, Rng rng, bool spendAction)
        {
            var attacker = s.GetUnit(attackerId);
            var weapon = Melee.MeleeWeaponOf(_content, attacker);

            // A charge by a unit with nothing to swing still pins the enemy; it just does no
            // damage on arrival.
            if (weapon == null) return s;

            // Cover does not apply in melee — see Melee.CoverAppliesInMelee for the reasoning.
            return ResolveAttack(
                s, attackerId, targetId, weapon,
                DefinitionOf(attacker).Stats.Melee, HitModifiers(attacker), ev, rng, spendAction);
        }

        /// <summary>Modifiers that follow the attacker around whatever it is doing.</summary>
        private static List<RollModifier> HitModifiers(UnitState attacker)
        {
            var modifiers = new List<RollModifier>();
            if (attacker.HasStatus(StatusKind.Shaken))
                modifiers.Add(new RollModifier(ModifierSource.Shaken, Morale.ShakenModifier));
            return modifiers;
        }

        /// <summary>
        /// Hit, wound, save, damage. Shared by shooting and melee so the two can never drift
        /// into different arithmetic.
        /// </summary>
        private GameState ResolveAttack(
            GameState s, UnitId attackerId, UnitId targetId, WeaponDefinition weapon,
            int toHitStat, List<RollModifier> hitModifiers,
            List<GameEvent> ev, Rng rng, bool spendAction)
        {
            var attacker = s.GetUnit(attackerId);
            var target = s.GetUnit(targetId);
            var targetStats = DefinitionOf(target).Stats;

            int attacks = weapon.Attacks * attacker.ModelsAlive;
            int hits = Roll(rng, ev, RollKind.ToHit, attackerId, targetId,
                            toHitStat, hitModifiers, attacks);

            // ---- to wound: the weapon's Power against the target's Resilience ----
            int woundTarget = Wounding.TargetFor(weapon.Power, targetStats.Resilience);
            int wounds = Roll(rng, ev, RollKind.ToWound, attackerId, targetId,
                              woundTarget, Modifiers.None, hits);

            // ---- save: the target's armour, worsened by the weapon's AP ----
            var saveModifiers = new List<RollModifier>();
            if (weapon.ArmourPiercing > 0)
                saveModifiers.Add(new RollModifier(
                    ModifierSource.ArmourPiercing, -weapon.ArmourPiercing, weapon.DisplayName));

            // The saving unit rolls, so Roller is the target here.
            int saved = Roll(rng, ev, RollKind.Save, targetId, attackerId,
                             targetStats.Save, saveModifiers, wounds);

            // ---- damage ----
            int unsaved = wounds - saved;
            int damage = unsaved * weapon.Damage;
            ev.Add(new AttackResolvedEvent(attackerId, targetId, hits, wounds, saved, damage));

            var models = target.Models.ToList();
            int slain = 0;
            for (int w = 0; w < unsaved; w++)
            {
                int idx = models.FindIndex(mm => !mm.IsSlain);
                if (idx < 0) break;

                // Each unsaved wound lands on one model for the weapon's full Damage. Excess
                // beyond that model's remaining wounds is lost rather than spilling onto the
                // next — a ruling, and the conventional one: a rifle that kills in one shot
                // does not kill two men because the first was already hurt.
                models[idx] = models[idx].TakeDamage(weapon.Damage);
                if (models[idx].IsSlain)
                {
                    slain++;
                    ev.Add(new ModelSlainEvent(targetId, idx));
                }
            }

            // Morale tests against this at round end, so losses accumulate across every
            // attack the unit suffers, shooting and melee alike.
            var newTarget = target.With(
                models: models,
                modelsLostThisRound: target.ModelsLostThisRound + slain);

            if (!newTarget.IsAlive) ev.Add(new UnitDestroyedEvent(targetId));

            var newAttacker = spendAction
                ? attacker.With(actionsRemaining: attacker.ActionsRemaining - 1)
                : attacker;

            return s.WithUnit(newAttacker).WithUnit(newTarget);
        }

        /// <summary>
        /// A charge: close the distance, then swing. It spends the whole activation.
        ///
        /// RULING: a unit with no melee weapon may still charge. It arrives, both sides become
        /// Engaged, and it simply does no damage — which is a real tactic, because an Engaged
        /// enemy cannot shoot. Requiring a melee weapon would make charging the preserve of
        /// the three units that carry one and take the lockdown option away from everyone else.
        /// </summary>
        private GameState DoCharge(GameState s, ChargeAt a, List<GameEvent> ev, Rng rng)
        {
            var charger = s.GetUnit(a.Unit);

            // Take the route the action carries; resolve one only when it carries none.
            IReadOnlyList<Hex> path = a.Path;
            if (path.Count == 0)
            {
                var approach = Melee.FindApproach(s, a.Unit, a.Target, MoveAllowanceOf(charger));
                if (!approach.IsPossible) return s;
                path = approach.Path;
            }

            var destination = path[path.Count - 1];

            // Declared first, so a consumer reading only the event stream can tell this run-in
            // from an ordinary move; then the move itself, so it animates the same way.
            ev.Add(new ChargeDeclaredEvent(a.Unit, a.Target, path));
            ev.Add(new UnitMovedEvent(a.Unit, path));

            var moved = charger.With(position: destination, actionsRemaining: 0);
            var next = s.WithUnit(moved);

            // Both sides are now in melee. RefreshEngagement would catch this anyway, but
            // setting it here means the free fight below already sees the right state.
            next = Melee.RefreshEngagement(next);

            // The free fight costs nothing further — the charge already spent everything.
            return DoFight(next, a.Unit, a.Target, ev, rng, spendAction: false);
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

        private GameState CheckRoundEnd(GameState s, List<GameEvent> ev, Rng rng)
        {
            // Annihilation ends a match the moment it happens, not at the next round break.
            if (Scoring.IsMatchOver(s, atRoundEnd: false, out _))
                return s.With(phase: PhaseKind.Complete);

            bool anyLeft = s.Units.Any(u => u.IsAlive && !u.HasActivated);
            if (anyLeft) return s;

            // Objectives pay out once, here, on where control stands as the round closes.
            var next = Scoring.ScoreRound(s, ev);

            ev.Add(new RoundEndedEvent(next.Round, next.ScoreA, next.ScoreB));

            if (Scoring.IsMatchOver(next, atRoundEnd: true, out _))
                return next.With(phase: PhaseKind.Complete);

            // Morale clears last round's Shaken, tests whoever took losses, and resets the
            // per-round counters. Not rolled on the final round — there is no next round for
            // a Shaken unit to suffer through.
            next = Morale.Resolve(next, _content, rng, ev);

            var reset = next.Units.Select(u => u.With(hasActivated: false, actionsRemaining: 0)).ToList();
            return next.With(round: next.Round + 1, units: reset, activeUnit: UnitId.None);
        }

        /// <summary>
        /// Who won. Delegates to <see cref="Scoring.IsMatchOver"/> so the engine cannot decide
        /// a winner by rules different from the ones that ended the match.
        /// </summary>
        private static PlayerId? WinnerOf(GameState s)
        {
            Scoring.IsMatchOver(s, atRoundEnd: true, out var winner);
            return winner;
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

                // UnitsOf enumerates in the order units are stored, which is stable, so the
                // offered actions come out in the same order every run.
                foreach (var t in state.UnitsOf(player.Opponent))
                {
                    var shot = new ShootAt(player, active.Id, t.Id, weaponId);
                    if (Validate(state, shot).IsLegal) list.Add(shot);

                    // Issued with its route already filled in, exactly as MoveUnit is, so a
                    // client can preview the landing hex without asking a second question.
                    var approach = Melee.FindApproach(state, active.Id, t.Id, allowance);
                    var charge = new ChargeAt(player, active.Id, t.Id,
                                              approach.IsPossible ? approach.Path : null);
                    if (Validate(state, charge).IsLegal) list.Add(charge);

                    var fight = new FightUnit(player, active.Id, t.Id);
                    if (Validate(state, fight).IsLegal) list.Add(fight);
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

        /// <inheritdoc />
        public IReadOnlyDictionary<ObjectiveId, PlayerId?> ProjectedControl(GameState state) =>
            Scoring.ProjectedControl(state);

        /// <inheritdoc />
        public ChargeApproach PreviewCharge(GameState state, UnitId charger, UnitId target)
        {
            var mover = state.GetUnit(charger);
            if (mover == null) return ChargeApproach.None;

            return Melee.FindApproach(state, charger, target, MoveAllowanceOf(mover));
        }

        /// <inheritdoc />
        public UnitDefinition GetDefinition(UnitState unit)
        {
            if (unit == null) throw new ArgumentNullException(nameof(unit));
            return _content.GetUnit(unit.DefinitionId);
        }

        /// <inheritdoc />
        public IReadOnlyCollection<Hex> ShootableHexes(GameState state, UnitId shooter, string weaponId)
        {
            var unit = state.GetUnit(shooter);
            if (unit == null || !unit.IsAlive) return Array.Empty<Hex>();

            var weapon = weaponId == null ? PrimaryWeaponOf(unit) : _content.GetWeapon(weaponId);

            // Range 0 is content's way of saying melee: nothing is shootable with it.
            if (weapon == null || weapon.Range <= 0) return Array.Empty<Hex>();

            return (IReadOnlyCollection<Hex>)LineOfSight.VisibleFrom(
                state, unit.Position, weapon.RangeInHexes);
        }

        /// <inheritdoc />
        public int ActionCost(GameState state, GameAction action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));

            switch (action)
            {
                // A charge is the whole activation: the run-in and the free fight together.
                case ChargeAt _: return ActionsPerActivation;

                case MoveUnit _:
                case ShootAt _:
                case FightUnit _: return 1;

                // Activating grants actions rather than spending them; ending and passing
                // close the activation out.
                case ActivateUnit _:
                case EndActivation _:
                case PassActivation _: return 0;

                default: return 0;
            }
        }
    }
}
