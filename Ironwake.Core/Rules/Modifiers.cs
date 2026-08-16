using System;
using System.Collections.Generic;
using System.Text;

namespace Ironwake.Core
{
    /// <summary>Where a roll modifier came from. The reason a target number moved.</summary>
    public enum ModifierSource
    {
        /// <summary>The target is sheltering in Cover or Obscuring terrain.</summary>
        Cover = 0,

        /// <summary>The shooter holds high ground.</summary>
        Elevated = 1,

        /// <summary>The roller moved before acting.</summary>
        Moved = 2,

        /// <summary>The weapon cuts through armour, worsening a save.</summary>
        ArmourPiercing = 3,

        /// <summary>Anything content-driven. Detail carries the ability's name.</summary>
        Ability = 4,
    }

    /// <summary>
    /// One reason a roll got easier or harder.
    ///
    /// SIGN CONVENTION, and it is the thing to get wrong here: <see cref="Value"/> modifies
    /// the ROLL, the way players say it — "cover is -1 to hit". A NEGATIVE value makes the
    /// roll worse, which RAISES the target number. So a -1 modifier turns a 4+ into a 5+.
    /// <see cref="Modifiers.FinalTarget"/> is the only place that conversion happens.
    /// </summary>
    public sealed class RollModifier
    {
        public ModifierSource Source { get; }

        /// <summary>Modifier to the roll: negative is worse for the roller.</summary>
        public int Value { get; }

        /// <summary>Human-readable specifics, e.g. the ability name. May be null.</summary>
        public string Detail { get; }

        public RollModifier(ModifierSource source, int value, string detail = null)
        {
            Source = source;
            Value = value;
            Detail = detail;
        }

        /// <summary>"cover -1", or "ability +1 (Marksman)".</summary>
        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append(Label(Source)).Append(' ').Append(Value >= 0 ? "+" : "").Append(Value);
            if (!string.IsNullOrEmpty(Detail)) sb.Append(" (").Append(Detail).Append(')');
            return sb.ToString();
        }

        private static string Label(ModifierSource source)
        {
            switch (source)
            {
                case ModifierSource.Cover: return "cover";
                case ModifierSource.Elevated: return "elevated";
                case ModifierSource.Moved: return "moved";
                case ModifierSource.ArmourPiercing: return "AP";
                case ModifierSource.Ability: return "ability";
                default: return source.ToString().ToLowerInvariant();
            }
        }
    }

    /// <summary>
    /// Turns a base target number and a list of modifiers into the number actually rolled
    /// against. The single place that arithmetic happens, so no call site can quietly invent
    /// its own stacking rule.
    /// </summary>
    public static class Modifiers
    {
        /// <summary>Best a roll can ever need. A natural 1 always fails regardless.</summary>
        public const int BestTarget = 2;

        /// <summary>Worst a roll can be pushed to while still being possible.</summary>
        public const int WorstTarget = 6;

        /// <summary>
        /// Content's "cannot" sentinel. A statline of 7 means no save at all / cannot hit,
        /// and no amount of help promotes it into a real roll.
        /// </summary>
        public const int Impossible = 7;

        /// <summary>
        /// True when a base target is content's way of saying the roll simply cannot be made
        /// — an unarmoured unit's Save of 7, for instance. Such a roll is not attempted.
        /// </summary>
        public static bool IsImpossible(int baseTarget) => baseTarget >= Impossible;

        /// <summary>
        /// The target number to roll against, after every modifier and the cap.
        ///
        /// ORDER: modifiers are summed, then the cap is applied once at the end. Summation is
        /// commutative, so the order they were collected in cannot change the result — that
        /// is deliberate, because an order-dependent stack is the kind of rule two
        /// implementations get subtly different.
        ///
        /// THE CAP IS A RULING: however many modifiers pile up, the result is clamped to
        /// 2+ at best and 6+ at worst. No stack of bonuses ever makes a roll automatic, and
        /// no stack of penalties ever makes it futile — both extremes stop being a dice game.
        /// A natural 1 already fails on its own (see <see cref="Rng.CountSuccesses"/>), so a
        /// 2+ still carries real risk.
        ///
        /// The cap deliberately does NOT rescue an impossible base target: 7+ stays 7+, since
        /// that is content saying "no save", not a roll that happens to be hard.
        /// </summary>
        public static int FinalTarget(int baseTarget, IReadOnlyList<RollModifier> modifiers)
        {
            if (IsImpossible(baseTarget)) return Impossible;

            int total = 0;
            if (modifiers != null)
            {
                for (int i = 0; i < modifiers.Count; i++) total += modifiers[i].Value;
            }

            // A modifier improves the ROLL, so it lowers the number needed.
            int target = baseTarget - total;

            if (target < BestTarget) return BestTarget;
            if (target > WorstTarget) return WorstTarget;
            return target;
        }

        /// <summary>Sum of the modifiers, for display. Positive helps the roller.</summary>
        public static int NetValue(IReadOnlyList<RollModifier> modifiers)
        {
            int total = 0;
            if (modifiers == null) return total;
            for (int i = 0; i < modifiers.Count; i++) total += modifiers[i].Value;
            return total;
        }

        /// <summary>"cover -1, AP -1" — empty string when nothing applied.</summary>
        public static string Describe(IReadOnlyList<RollModifier> modifiers)
        {
            if (modifiers == null || modifiers.Count == 0) return string.Empty;

            var sb = new StringBuilder();
            for (int i = 0; i < modifiers.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(modifiers[i]);
            }
            return sb.ToString();
        }

        /// <summary>An immutable empty list, so callers need not allocate to mean "none".</summary>
        public static readonly IReadOnlyList<RollModifier> None = Array.Empty<RollModifier>();
    }
}
