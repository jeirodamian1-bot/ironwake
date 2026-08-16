using System;
using System.Collections.Generic;
using System.Text;

namespace Ironwake.Core
{
    /// <summary>
    /// A thing that happened. The engine emits an ordered list of these; the client
    /// plays them back as animation and appends them to the combat log.
    ///
    /// The client must NEVER work out what happened by diffing two GameStates.
    /// If the client needs to show something, there is an event for it — and if there
    /// isn't, add one to the engine rather than inferring it client-side.
    /// </summary>
    public abstract class GameEvent
    {
        public abstract string Kind { get; }
        /// <summary>One-line human-readable form for the combat log.</summary>
        public abstract string Describe();
        public override string ToString() => Describe();
    }

    public sealed class UnitActivatedEvent : GameEvent
    {
        public UnitId Unit { get; }
        public UnitActivatedEvent(UnitId unit) { Unit = unit; }
        public override string Kind => "UnitActivated";
        public override string Describe() => $"{Unit} activates.";
    }

    public sealed class UnitMovedEvent : GameEvent
    {
        public UnitId Unit { get; }
        public IReadOnlyList<Hex> Path { get; }
        public UnitMovedEvent(UnitId unit, IReadOnlyList<Hex> path)
        {
            Unit = unit; Path = path ?? Array.Empty<Hex>();
        }
        public override string Kind => "UnitMoved";
        public override string Describe() =>
            $"{Unit} moves {Path.Count - 1} hex(es) to {(Path.Count > 0 ? Path[Path.Count - 1].ToString() : "?")}.";
    }

    /// <summary>Which step of a resolution a roll belongs to.</summary>
    public enum RollKind
    {
        ToHit = 0,
        ToWound = 1,
        Save = 2,
        Morale = 3,
    }

    /// <summary>
    /// Emitted for every dice roll so the log can show the actual numbers.
    ///
    /// Everything here is structured. It used to encode the modifiers as prose in a Purpose
    /// string, which meant a client wanting to render them had to parse English, and it did
    /// not say WHO rolled — so a log line could not be attributed to a unit without inferring
    /// it from whatever event happened to come next. Both are fixed: read the fields, and use
    /// <see cref="Describe"/> only for the human-readable line.
    /// </summary>
    public sealed class DiceRolledEvent : GameEvent
    {
        /// <summary>Named RollKind rather than Kind because <see cref="GameEvent.Kind"/> is the event type.</summary>
        public RollKind RollKind { get; }

        /// <summary>The unit that rolled.</summary>
        public UnitId Roller { get; }

        /// <summary>What it was rolled against. <see cref="UnitId.None"/> where there is no target.</summary>
        public UnitId Target { get; }

        /// <summary>The statline number before anything modified it.</summary>
        public int BaseTarget { get; }

        /// <summary>What was actually needed, after modifiers and the cap.</summary>
        public int FinalTarget { get; }

        /// <summary>Every reason the number moved, in the order they were applied.</summary>
        public IReadOnlyList<RollModifier> Modifiers { get; }

        public int[] Results { get; }
        public int Successes { get; }

        public DiceRolledEvent(
            RollKind rollKind, UnitId roller, UnitId target,
            int baseTarget, int finalTarget,
            IReadOnlyList<RollModifier> modifiers,
            int[] results, int successes)
        {
            RollKind = rollKind;
            Roller = roller;
            Target = target;
            BaseTarget = baseTarget;
            FinalTarget = finalTarget;
            Modifiers = modifiers ?? Ironwake.Core.Modifiers.None;
            Results = results ?? Array.Empty<int>();
            Successes = successes;
        }

        public override string Kind => "DiceRolled";

        /// <summary>Composed from the structured fields — the prose is a view, not the data.</summary>
        public override string Describe()
        {
            var sb = new StringBuilder();
            sb.Append(Roller).Append(' ').Append(Label(RollKind));
            if (!Target.IsNone) sb.Append(" vs ").Append(Target);

            sb.Append(" (");
            if (Ironwake.Core.Modifiers.IsImpossible(FinalTarget))
            {
                // Content's "cannot" sentinel: no armour at all, for instance.
                sb.Append("cannot");
            }
            else if (Modifiers.Count > 0)
            {
                sb.Append(BaseTarget).Append("+ → ").Append(FinalTarget).Append("+: ")
                  .Append(Ironwake.Core.Modifiers.Describe(Modifiers));
            }
            else
            {
                sb.Append(FinalTarget).Append('+');
            }
            sb.Append("): [").Append(string.Join(",", Results)).Append("] → ")
              .Append(Successes).Append(" success(es).");

            return sb.ToString();
        }

        private static string Label(RollKind kind)
        {
            switch (kind)
            {
                case RollKind.ToHit: return "to-hit";
                case RollKind.ToWound: return "to-wound";
                case RollKind.Save: return "save";
                case RollKind.Morale: return "morale";
                default: return kind.ToString().ToLowerInvariant();
            }
        }
    }

    public sealed class AttackResolvedEvent : GameEvent
    {
        public UnitId Attacker { get; }
        public UnitId Target { get; }
        public int Hits { get; }
        public int Wounds { get; }
        public int Saved { get; }
        public int DamageDealt { get; }
        public AttackResolvedEvent(UnitId attacker, UnitId target,
                                   int hits, int wounds, int saved, int damageDealt)
        {
            Attacker = attacker; Target = target;
            Hits = hits; Wounds = wounds; Saved = saved; DamageDealt = damageDealt;
        }
        public override string Kind => "AttackResolved";
        public override string Describe() =>
            $"{Attacker} → {Target}: {Hits} hit, {Wounds} wounded, {Saved} saved, {DamageDealt} damage.";
    }

    public sealed class ModelSlainEvent : GameEvent
    {
        public UnitId Unit { get; }
        public int ModelIndex { get; }
        public ModelSlainEvent(UnitId unit, int modelIndex) { Unit = unit; ModelIndex = modelIndex; }
        public override string Kind => "ModelSlain";
        public override string Describe() => $"{Unit} loses a model.";
    }

    public sealed class UnitDestroyedEvent : GameEvent
    {
        public UnitId Unit { get; }
        public UnitDestroyedEvent(UnitId unit) { Unit = unit; }
        public override string Kind => "UnitDestroyed";
        public override string Describe() => $"{Unit} is destroyed.";
    }

    public sealed class StatusAppliedEvent : GameEvent
    {
        public UnitId Unit { get; }
        public StatusKind Status { get; }
        public StatusAppliedEvent(UnitId unit, StatusKind status) { Unit = unit; Status = status; }
        public override string Kind => "StatusApplied";
        public override string Describe() => $"{Unit} is now {Status}.";
    }

    public sealed class ObjectiveScoredEvent : GameEvent
    {
        public PlayerId Player { get; }
        public ObjectiveId Objective { get; }
        public int Points { get; }
        public ObjectiveScoredEvent(PlayerId player, ObjectiveId objective, int points)
        {
            Player = player; Objective = objective; Points = points;
        }
        public override string Kind => "ObjectiveScored";
        public override string Describe() => $"{Player} scores {Points} from {Objective}.";
    }

    public sealed class ActivationEndedEvent : GameEvent
    {
        public UnitId Unit { get; }
        public PlayerId NextPlayer { get; }
        public ActivationEndedEvent(UnitId unit, PlayerId nextPlayer)
        {
            Unit = unit; NextPlayer = nextPlayer;
        }
        public override string Kind => "ActivationEnded";
        public override string Describe() => $"Activation ends. {NextPlayer} to act.";
    }

    public sealed class RoundEndedEvent : GameEvent
    {
        public int Round { get; }
        public int ScoreA { get; }
        public int ScoreB { get; }
        public RoundEndedEvent(int round, int scoreA, int scoreB)
        {
            Round = round; ScoreA = scoreA; ScoreB = scoreB;
        }
        public override string Kind => "RoundEnded";
        public override string Describe() => $"Round {Round} ends. {ScoreA} - {ScoreB}.";
    }

    public sealed class MatchEndedEvent : GameEvent
    {
        public PlayerId? Winner { get; }   // null = draw
        public MatchEndedEvent(PlayerId? winner) { Winner = winner; }
        public override string Kind => "MatchEnded";
        public override string Describe() =>
            Winner.HasValue ? $"Match over. {Winner.Value} wins." : "Match over. Draw.";
    }
}
