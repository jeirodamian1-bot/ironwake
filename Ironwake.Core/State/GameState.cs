using System;
using System.Collections.Generic;
using System.Linq;

namespace Ironwake.Core
{
    /// <summary>
    /// Complete snapshot of a match. Immutable — every mutation produces a new instance.
    ///
    /// RULES FOR THIS TYPE:
    ///   - No floats. Distances are stored in tenths of an inch as int.
    ///   - No DateTime. No file or network access. No UnityEngine.
    ///   - Serialisable to JSON in full; target under 40 KB for a 12-unit match.
    /// </summary>
    public sealed class GameState
    {
        public int Round { get; }
        public PhaseKind Phase { get; }
        public PlayerId ActivePlayer { get; }
        public UnitId ActiveUnit { get; }          // None if no unit currently activated
        public BoardState Board { get; }
        public IReadOnlyList<UnitState> Units { get; }
        public IReadOnlyList<ObjectiveState> Objectives { get; }
        public int ScoreA { get; }
        public int ScoreB { get; }
        public RngState Rng { get; }
        public string ContentVersion { get; }

        public GameState(
            int round,
            PhaseKind phase,
            PlayerId activePlayer,
            UnitId activeUnit,
            BoardState board,
            IReadOnlyList<UnitState> units,
            IReadOnlyList<ObjectiveState> objectives,
            int scoreA,
            int scoreB,
            RngState rng,
            string contentVersion)
        {
            Round = round;
            Phase = phase;
            ActivePlayer = activePlayer;
            ActiveUnit = activeUnit;
            Board = board;
            Units = units ?? Array.Empty<UnitState>();
            Objectives = objectives ?? Array.Empty<ObjectiveState>();
            ScoreA = scoreA;
            ScoreB = scoreB;
            Rng = rng;
            ContentVersion = contentVersion;
        }

        // ---- QUERIES --------------------------------------------------------

        public UnitState GetUnit(UnitId id) => Units.FirstOrDefault(u => u.Id == id);

        public UnitState UnitAt(Hex hex) =>
            Units.FirstOrDefault(u => u.Position == hex && u.IsAlive);

        public IEnumerable<UnitState> UnitsOf(PlayerId player) =>
            Units.Where(u => u.Owner == player && u.IsAlive);

        public int ScoreOf(PlayerId p) => p == PlayerId.A ? ScoreA : ScoreB;

        // ---- MUTATORS (return new instances) --------------------------------

        public GameState With(
            int? round = null,
            PhaseKind? phase = null,
            PlayerId? activePlayer = null,
            UnitId? activeUnit = null,
            BoardState board = null,
            IReadOnlyList<UnitState> units = null,
            IReadOnlyList<ObjectiveState> objectives = null,
            int? scoreA = null,
            int? scoreB = null,
            RngState rng = null)
            => new GameState(
                round ?? Round,
                phase ?? Phase,
                activePlayer ?? ActivePlayer,
                activeUnit ?? ActiveUnit,
                board ?? Board,
                units ?? Units,
                objectives ?? Objectives,
                scoreA ?? ScoreA,
                scoreB ?? ScoreB,
                rng ?? Rng,
                ContentVersion);

        /// <summary>Replace one unit, leaving the rest untouched.</summary>
        public GameState WithUnit(UnitState replacement)
        {
            var list = new List<UnitState>(Units.Count);
            foreach (var u in Units)
                list.Add(u.Id == replacement.Id ? replacement : u);
            return With(units: list);
        }
    }

    public sealed class BoardState
    {
        /// <summary>Radius in hexes from centre. Radius 5 is roughly a skirmish board.</summary>
        public int Radius { get; }
        private readonly Dictionary<Hex, TerrainKind> _terrain;

        public BoardState(int radius, IReadOnlyDictionary<Hex, TerrainKind> terrain = null)
        {
            Radius = radius;
            // Copy explicitly rather than casting to IDictionary - the cast fails at
            // runtime for any read-only implementation that isn't a Dictionary.
            _terrain = new Dictionary<Hex, TerrainKind>();
            if (terrain != null)
                foreach (var kv in terrain) _terrain[kv.Key] = kv.Value;
        }

        public IReadOnlyDictionary<Hex, TerrainKind> Terrain => _terrain;

        public bool Contains(Hex h) => Hex.Zero.DistanceTo(h) <= Radius;

        public TerrainKind TerrainAt(Hex h) =>
            _terrain.TryGetValue(h, out var t) ? t : TerrainKind.Open;

        public bool IsPassable(Hex h) =>
            Contains(h) && TerrainAt(h) != TerrainKind.Impassable;

        /// <summary>Every hex on the board, in deterministic order.</summary>
        public IEnumerable<Hex> AllHexes() =>
            Hex.Zero.WithinRange(Radius).OrderBy(h => h.Q).ThenBy(h => h.R);
    }

    public sealed class UnitState
    {
        public UnitId Id { get; }
        public PlayerId Owner { get; }
        /// <summary>Key into the content pack. The engine holds no statlines itself.</summary>
        public string DefinitionId { get; }
        public Hex Position { get; }
        /// <summary>0-5, matching Hex.Directions.</summary>
        public int Facing { get; }
        public IReadOnlyList<ModelState> Models { get; }
        public IReadOnlyList<StatusKind> Statuses { get; }
        public bool HasActivated { get; }
        public int ActionsRemaining { get; }

        /// <summary>
        /// Models lost since the last round cleanup. Morale tests against this at round end,
        /// then it resets. Stored rather than derived because nothing else remembers what a
        /// unit looked like when the round began.
        /// </summary>
        public int ModelsLostThisRound { get; }

        public UnitState(
            UnitId id, PlayerId owner, string definitionId, Hex position, int facing,
            IReadOnlyList<ModelState> models, IReadOnlyList<StatusKind> statuses,
            bool hasActivated, int actionsRemaining, int modelsLostThisRound = 0)
        {
            Id = id; Owner = owner; DefinitionId = definitionId;
            Position = position; Facing = facing;
            Models = models ?? Array.Empty<ModelState>();
            Statuses = statuses ?? Array.Empty<StatusKind>();
            HasActivated = hasActivated; ActionsRemaining = actionsRemaining;
            ModelsLostThisRound = modelsLostThisRound;
        }

        public int ModelsAlive => Models.Count(m => !m.IsSlain);
        public bool IsAlive => ModelsAlive > 0;
        public bool HasStatus(StatusKind s) => Statuses.Contains(s);

        public UnitState With(
            Hex? position = null, int? facing = null,
            IReadOnlyList<ModelState> models = null,
            IReadOnlyList<StatusKind> statuses = null,
            bool? hasActivated = null, int? actionsRemaining = null,
            int? modelsLostThisRound = null)
            => new UnitState(
                Id, Owner, DefinitionId,
                position ?? Position, facing ?? Facing,
                models ?? Models, statuses ?? Statuses,
                hasActivated ?? HasActivated,
                actionsRemaining ?? ActionsRemaining,
                modelsLostThisRound ?? ModelsLostThisRound);

        /// <summary>Returns this unit with <paramref name="status"/> present exactly once.</summary>
        public UnitState WithStatus(StatusKind status)
        {
            if (HasStatus(status)) return this;
            var list = new List<StatusKind>(Statuses) { status };
            return With(statuses: list);
        }

        /// <summary>Returns this unit with <paramref name="status"/> removed.</summary>
        public UnitState WithoutStatus(StatusKind status)
        {
            if (!HasStatus(status)) return this;
            var list = new List<StatusKind>();
            foreach (var s in Statuses) if (s != status) list.Add(s);
            return With(statuses: list);
        }
    }

    public sealed class ModelState
    {
        public int WoundsRemaining { get; }
        public bool IsSlain { get; }

        public ModelState(int woundsRemaining, bool isSlain = false)
        {
            WoundsRemaining = woundsRemaining;
            IsSlain = isSlain;
        }

        public ModelState TakeDamage(int amount)
        {
            int left = WoundsRemaining - amount;
            return left <= 0 ? new ModelState(0, true) : new ModelState(left);
        }
    }

    public sealed class ObjectiveState
    {
        public ObjectiveId Id { get; }
        public Hex Position { get; }
        public int PointValue { get; }
        /// <summary>Player currently controlling, or null if contested/empty.</summary>
        public PlayerId? ControlledBy { get; }

        public ObjectiveState(ObjectiveId id, Hex position, int pointValue, PlayerId? controlledBy = null)
        {
            Id = id; Position = position; PointValue = pointValue; ControlledBy = controlledBy;
        }

        public ObjectiveState WithControl(PlayerId? p) =>
            new ObjectiveState(Id, Position, PointValue, p);
    }
}
