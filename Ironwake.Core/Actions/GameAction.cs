using System;
using System.Collections.Generic;

namespace Ironwake.Core
{
    /// <summary>
    /// A player's intent. The client builds one of these from input and hands it to the
    /// engine. The client decides NOTHING about the outcome.
    ///
    /// Every action carries its Actor so the server can verify the sender is entitled
    /// to it without inspecting the payload.
    /// </summary>
    public abstract class GameAction
    {
        public PlayerId Actor { get; }
        protected GameAction(PlayerId actor) { Actor = actor; }
        public abstract string Kind { get; }
        public override string ToString() => $"{Kind}({Actor})";
    }

    /// <summary>Select a unit to activate this turn.</summary>
    public sealed class ActivateUnit : GameAction
    {
        public UnitId Unit { get; }
        public ActivateUnit(PlayerId actor, UnitId unit) : base(actor) { Unit = unit; }
        public override string Kind => "ActivateUnit";
    }

    /// <summary>
    /// Move along an explicit path. The client sends the full path so the engine can
    /// validate every intermediate hex — do not send only the destination.
    /// </summary>
    public sealed class MoveUnit : GameAction
    {
        public UnitId Unit { get; }
        public IReadOnlyList<Hex> Path { get; }
        public MoveUnit(PlayerId actor, UnitId unit, IReadOnlyList<Hex> path) : base(actor)
        {
            Unit = unit;
            Path = path ?? Array.Empty<Hex>();
        }
        public override string Kind => "MoveUnit";
    }

    public sealed class ShootAt : GameAction
    {
        public UnitId Unit { get; }
        public UnitId Target { get; }
        public string WeaponId { get; }
        public ShootAt(PlayerId actor, UnitId unit, UnitId target, string weaponId) : base(actor)
        {
            Unit = unit; Target = target; WeaponId = weaponId;
        }
        public override string Kind => "ShootAt";
    }

    public sealed class ChargeAt : GameAction
    {
        public UnitId Unit { get; }
        public UnitId Target { get; }
        public ChargeAt(PlayerId actor, UnitId unit, UnitId target) : base(actor)
        {
            Unit = unit; Target = target;
        }
        public override string Kind => "ChargeAt";
    }

    public sealed class FightUnit : GameAction
    {
        public UnitId Unit { get; }
        public UnitId Target { get; }
        public FightUnit(PlayerId actor, UnitId unit, UnitId target) : base(actor)
        {
            Unit = unit; Target = target;
        }
        public override string Kind => "FightUnit";
    }

    public sealed class UseAbility : GameAction
    {
        public UnitId Unit { get; }
        public string AbilityId { get; }
        public UnitId TargetUnit { get; }
        public Hex? TargetHex { get; }
        public UseAbility(PlayerId actor, UnitId unit, string abilityId,
                          UnitId targetUnit = default, Hex? targetHex = null) : base(actor)
        {
            Unit = unit; AbilityId = abilityId; TargetUnit = targetUnit; TargetHex = targetHex;
        }
        public override string Kind => "UseAbility";
    }

    /// <summary>Finish the current unit's activation and hand over to the opponent.</summary>
    public sealed class EndActivation : GameAction
    {
        public UnitId Unit { get; }
        public EndActivation(PlayerId actor, UnitId unit) : base(actor) { Unit = unit; }
        public override string Kind => "EndActivation";
    }

    /// <summary>Decline to activate anything. Also what a turn-timer expiry submits.</summary>
    public sealed class PassActivation : GameAction
    {
        public PassActivation(PlayerId actor) : base(actor) { }
        public override string Kind => "PassActivation";
    }
}
