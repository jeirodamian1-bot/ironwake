using System.Collections.Generic;

namespace Ironwake.Core
{
    /// <summary>
    /// The whole engine surface. Two methods, both pure.
    ///
    /// Validate  — cheap, side-effect free, safe to call on every mouse move for UI feedback.
    /// Execute   — assumes the action is already legal. Consumes RNG. Returns the new world.
    ///
    /// Execute must be deterministic: same state + same action => same result, on any machine.
    /// </summary>
    public interface IGameEngine
    {
        ValidationResult Validate(GameState state, GameAction action);
        ExecutionResult Execute(GameState state, GameAction action);

        /// <summary>
        /// Every action the given player could legally take right now.
        /// Used by the client to highlight options and by the AI simulator to pick moves.
        /// </summary>
        IReadOnlyList<GameAction> LegalActions(GameState state, PlayerId player);

        /// <summary>
        /// Every hex the unit can move to, mapped to what it costs to get there. The unit's
        /// own hex is not included.
        ///
        /// This exists so the client can highlight a movement range with ONE call instead of
        /// calling <see cref="Validate"/> once per candidate hex. Empty for a missing or
        /// destroyed unit.
        /// </summary>
        /// <remarks>
        /// Enumeration order of the result is not meaningful — sort before rendering
        /// anything order-dependent.
        /// </remarks>
        IReadOnlyDictionary<Hex, int> ReachableHexes(GameState state, UnitId unit);

        /// <summary>
        /// Whether the shooter can see the target, and whether the target has cover.
        ///
        /// The client uses this to grey out targets and to explain why — the engine decides,
        /// the client only renders the answer. Returns a blocked result with no blocking hex
        /// if either unit does not exist.
        /// </summary>
        LosResult CheckLineOfSight(GameState state, UnitId shooter, UnitId target);
    }

    public sealed class ExecutionResult
    {
        public GameState NextState { get; }
        public IReadOnlyList<GameEvent> Events { get; }
        public bool IsTerminal { get; }

        public ExecutionResult(GameState nextState, IReadOnlyList<GameEvent> events, bool isTerminal = false)
        {
            NextState = nextState;
            Events = events ?? new List<GameEvent>();
            IsTerminal = isTerminal;
        }
    }

    /// <summary>
    /// Refusals carry a machine-readable code AND a human sentence, so the UI can
    /// explain *why* a button is disabled instead of silently greying it out.
    /// </summary>
    public sealed class ValidationResult
    {
        public bool IsLegal { get; }
        public string ReasonCode { get; }
        public string Detail { get; }

        private ValidationResult(bool isLegal, string reasonCode, string detail)
        {
            IsLegal = isLegal; ReasonCode = reasonCode; Detail = detail;
        }

        public static readonly ValidationResult Legal = new ValidationResult(true, null, null);

        public static ValidationResult Illegal(string code, string detail) =>
            new ValidationResult(false, code, detail);

        public override string ToString() => IsLegal ? "Legal" : $"Illegal[{ReasonCode}]: {Detail}";
    }

    /// <summary>Canonical refusal codes. Add here, not as raw strings at call sites.</summary>
    public static class ReasonCodes
    {
        public const string NotYourTurn        = "NOT_YOUR_TURN";
        public const string NotYourUnit        = "NOT_YOUR_UNIT";
        public const string UnitNotFound       = "UNIT_NOT_FOUND";
        public const string UnitDead           = "UNIT_DEAD";
        public const string AlreadyActivated   = "ALREADY_ACTIVATED";
        public const string NoActionsRemaining = "NO_ACTIONS_REMAINING";
        public const string NotActiveUnit      = "NOT_ACTIVE_UNIT";
        public const string PathBlocked        = "PATH_BLOCKED";
        public const string PathTooLong        = "PATH_TOO_LONG";
        public const string PathNotContiguous  = "PATH_NOT_CONTIGUOUS";
        public const string OffBoard           = "OFF_BOARD";
        public const string HexOccupied        = "HEX_OCCUPIED";
        public const string OutOfRange         = "OUT_OF_RANGE";
        public const string NoLineOfSight      = "NO_LINE_OF_SIGHT";
        public const string TargetFriendly     = "TARGET_FRIENDLY";

        /// <summary>The weapon has no range: it is a melee weapon and cannot be shot with.</summary>
        public const string WeaponIsMelee      = "WEAPON_IS_MELEE";

        /// <summary>The unit carries nothing at all.</summary>
        public const string NoWeapon           = "NO_WEAPON";
        public const string UnitShaken         = "UNIT_SHAKEN";
        public const string MatchComplete      = "MATCH_COMPLETE";
        public const string UnknownAction      = "UNKNOWN_ACTION";
    }
}
