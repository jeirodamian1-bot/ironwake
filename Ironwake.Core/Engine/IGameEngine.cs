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

        /// <summary>
        /// Who currently holds each objective, before any scoring resolves. Null means
        /// contested or empty.
        ///
        /// Scoring only happens at round end, so this is how a client shades the board
        /// mid-round and shows a player what they stand to gain if the round closed now.
        /// </summary>
        /// <remarks>
        /// Enumeration order of the result is not meaningful — sort by objective id before
        /// rendering anything order-dependent.
        /// </remarks>
        IReadOnlyDictionary<ObjectiveId, PlayerId?> ProjectedControl(GameState state);

        /// <summary>
        /// Where a charge would land, and the route it would take, without committing to it.
        ///
        /// A charge moves the unit before it fights, so a player choosing one is choosing a
        /// destination as much as a target. This is what lets the client draw that
        /// destination on hover. The path it returns is exactly the one
        /// <see cref="LegalActions"/> puts on its <see cref="ChargeAt"/> offers.
        /// </summary>
        ChargeApproach PreviewCharge(GameState state, UnitId charger, UnitId target);

        /// <summary>
        /// The content definition behind a unit — display name, points, statline, weapons.
        ///
        /// <see cref="GameState"/> deliberately carries only a <see cref="UnitState.DefinitionId"/>
        /// and a content version, so state stays small and pins the content it was built
        /// against. Without this a client holding only a state cannot so much as label a
        /// counter.
        /// </summary>
        /// <exception cref="ContentNotFoundException">If the unit's definition is not in the pack.</exception>
        UnitDefinition GetDefinition(UnitState unit);

        /// <summary>
        /// Every hex the shooter could put fire into: on the board, inside the weapon's
        /// range, and with line of sight. Empty for a melee weapon or a unit that cannot see
        /// out of where it stands.
        ///
        /// Lets the client shade a threat range in one call rather than probing
        /// <see cref="Validate"/> at every hex, and lets it shade EMPTY ground — which
        /// probing per enemy cannot do at all.
        /// </summary>
        /// <param name="state">The board to measure across.</param>
        /// <param name="shooter">The unit doing the shooting.</param>
        /// <param name="weaponId">The weapon to measure, or null for the unit's primary.</param>
        /// <remarks>Enumeration order is not meaningful — sort before rendering.</remarks>
        IReadOnlyCollection<Hex> ShootableHexes(GameState state, UnitId shooter, string weaponId);

        /// <summary>
        /// How many of a unit's actions this action spends.
        ///
        /// Mostly one, but a charge spends the whole activation, and the client needs to be
        /// able to say so before the player finds out by having no actions left.
        /// Activating, ending and passing cost nothing.
        /// </summary>
        int ActionCost(GameState state, GameAction action);
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

        /// <summary>Locked in melee: cannot shoot, though it may fight or walk away.</summary>
        public const string UnitEngaged        = "UNIT_ENGAGED";

        /// <summary>No route to any hex adjacent to the target, within the move allowance.</summary>
        public const string NoChargePath       = "NO_CHARGE_PATH";

        /// <summary>Fighting requires being next to the target.</summary>
        public const string NotAdjacent        = "NOT_ADJACENT";

        /// <summary>The unit carries nothing it can swing.</summary>
        public const string NoMeleeWeapon      = "NO_MELEE_WEAPON";
        public const string MatchComplete      = "MATCH_COMPLETE";
        public const string UnknownAction      = "UNKNOWN_ACTION";
    }
}
