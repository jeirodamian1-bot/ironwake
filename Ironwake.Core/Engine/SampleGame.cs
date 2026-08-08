using System.Collections.Generic;

namespace Ironwake.Core
{
    /// <summary>
    /// Builds a ready-to-play GameState so the client has something concrete to render
    /// on day one. Radius 5 board, 3 units per side, a little terrain, 3 objectives.
    /// </summary>
    public static class SampleGame
    {
        /// <param name="content">
        /// Supplies every statline. Core cannot load this itself — the caller passes in a
        /// pack (JSON-backed in the harness, hand-built in tests).
        /// </param>
        /// <param name="seed">Dice seed. The same seed replays the same match.</param>
        public static GameState Create(IContentPack content, ulong seed = 12345)
        {
            if (content == null) throw new System.ArgumentNullException(nameof(content));

            var terrain = new Dictionary<Hex, TerrainKind>
            {
                { new Hex( 0, -2), TerrainKind.Cover },
                { new Hex( 1, -1), TerrainKind.Cover },
                { new Hex(-1,  2), TerrainKind.Cover },
                { new Hex( 0,  2), TerrainKind.Cover },
                { new Hex( 2,  0), TerrainKind.Obscuring },
                { new Hex(-2,  0), TerrainKind.Obscuring },
                { new Hex( 0,  0), TerrainKind.Elevated },
                { new Hex( 3, -3), TerrainKind.Impassable },
                { new Hex(-3,  3), TerrainKind.Impassable },
            };

            var board = new BoardState(radius: 5, terrain: terrain);

            var units = new List<UnitState>
            {
                MakeUnit(content, 1, PlayerId.A, "ashguard_lineholder", new Hex(-4,  1)),
                MakeUnit(content, 2, PlayerId.A, "ashguard_lineholder", new Hex(-4,  2)),
                MakeUnit(content, 3, PlayerId.A, "ashguard_warden",     new Hex(-3,  1)),

                MakeUnit(content, 4, PlayerId.B, "cinderkin_raider",    new Hex( 4, -1)),
                MakeUnit(content, 5, PlayerId.B, "cinderkin_raider",    new Hex( 4, -2)),
                MakeUnit(content, 6, PlayerId.B, "cinderkin_brute",     new Hex( 3, -1)),
            };

            var objectives = new List<ObjectiveState>
            {
                new ObjectiveState(new ObjectiveId(1), new Hex( 0,  0), 2),
                new ObjectiveState(new ObjectiveId(2), new Hex( 2, -2), 1),
                new ObjectiveState(new ObjectiveId(3), new Hex(-2,  2), 1),
            };

            return new GameState(
                round: 1,
                phase: PhaseKind.Activation,
                activePlayer: PlayerId.A,
                activeUnit: UnitId.None,
                board: board,
                units: units,
                objectives: objectives,
                scoreA: 0,
                scoreB: 0,
                rng: new RngState(seed),
                contentVersion: content.Version);
        }

        /// <summary>
        /// Builds a unit at full strength from its definition. Model count and wounds-per-model
        /// come from content, so the sample board cannot drift away from the authored statlines.
        /// </summary>
        private static UnitState MakeUnit(
            IContentPack content, int id, PlayerId owner, string defId, Hex pos)
        {
            var def = content.GetUnit(defId);

            var models = new List<ModelState>();
            for (int i = 0; i < def.ModelCount; i++) models.Add(new ModelState(def.Stats.Wounds));

            return new UnitState(
                new UnitId(id), owner, defId, pos,
                facing: 0,
                models: models,
                statuses: new List<StatusKind>(),
                hasActivated: false,
                actionsRemaining: 0);
        }
    }
}
