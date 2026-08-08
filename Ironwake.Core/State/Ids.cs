using System;

namespace Ironwake.Core
{
    /// <summary>Wrapper so a unit id can never be passed where a player id is expected.</summary>
    public readonly struct UnitId : IEquatable<UnitId>
    {
        public int Value { get; }
        public UnitId(int value) { Value = value; }
        public static readonly UnitId None = new UnitId(0);
        public bool IsNone => Value == 0;

        public bool Equals(UnitId o) => Value == o.Value;
        public override bool Equals(object o) => o is UnitId u && Equals(u);
        public override int GetHashCode() => Value;
        public static bool operator ==(UnitId a, UnitId b) => a.Equals(b);
        public static bool operator !=(UnitId a, UnitId b) => !a.Equals(b);
        public override string ToString() => $"U{Value}";
    }

    public readonly struct PlayerId : IEquatable<PlayerId>
    {
        public int Value { get; }
        public PlayerId(int value) { Value = value; }
        public static readonly PlayerId A = new PlayerId(1);
        public static readonly PlayerId B = new PlayerId(2);
        public PlayerId Opponent => Value == 1 ? B : A;

        public bool Equals(PlayerId o) => Value == o.Value;
        public override bool Equals(object o) => o is PlayerId p && Equals(p);
        public override int GetHashCode() => Value;
        public static bool operator ==(PlayerId a, PlayerId b) => a.Equals(b);
        public static bool operator !=(PlayerId a, PlayerId b) => !a.Equals(b);
        public override string ToString() => $"P{Value}";
    }

    public readonly struct ObjectiveId : IEquatable<ObjectiveId>
    {
        public int Value { get; }
        public ObjectiveId(int value) { Value = value; }

        public bool Equals(ObjectiveId o) => Value == o.Value;
        public override bool Equals(object o) => o is ObjectiveId x && Equals(x);
        public override int GetHashCode() => Value;
        public static bool operator ==(ObjectiveId a, ObjectiveId b) => a.Equals(b);
        public static bool operator !=(ObjectiveId a, ObjectiveId b) => !a.Equals(b);
        public override string ToString() => $"O{Value}";
    }

    public enum TerrainKind
    {
        Open = 0,
        Cover = 1,      // -1 to hit shots against units here
        Obscuring = 2,  // blocks line of sight through it
        Impassable = 3, // cannot be entered
        Elevated = 4,   // sees over Obscuring
    }

    public enum PhaseKind
    {
        Deployment = 0,
        Activation = 1,
        Scoring = 2,
        Complete = 3,
    }

    public enum StatusKind
    {
        None = 0,
        Shaken = 1,   // -1 to hit, cannot Charge
        Engaged = 2,  // in melee, cannot Shoot
        Overwatch = 3,
    }
}
