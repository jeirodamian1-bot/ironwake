using System;
using System.Collections.Generic;

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

    /// <summary>Emitted for every dice roll so the log can show the actual numbers.</summary>
    public sealed class DiceRolledEvent : GameEvent
    {
        public string Purpose { get; }      // "to-hit", "to-wound", "save", "morale"
        public int Target { get; }          // the number needed
        public int[] Results { get; }
        public int Successes { get; }
        public DiceRolledEvent(string purpose, int target, int[] results, int successes)
        {
            Purpose = purpose; Target = target;
            Results = results ?? Array.Empty<int>();
            Successes = successes;
        }
        public override string Kind => "DiceRolled";
        public override string Describe() =>
            $"{Purpose} ({Target}+): [{string.Join(",", Results)}] → {Successes} success(es).";
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
