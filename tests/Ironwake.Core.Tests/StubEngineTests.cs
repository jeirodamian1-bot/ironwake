using System.Collections.Generic;
using System.Linq;
using Ironwake.Core;
using Xunit;

namespace Ironwake.Core.Tests
{
    /// <summary>
    /// These assert the refusal *codes*, not merely that something was refused — the client
    /// branches on ReasonCode, so a rule failing for the wrong reason is still a bug.
    ///
    /// StubEngine is temporary and its numbers are placeholders, but the refusal contract
    /// it establishes is what the real RulesEngine has to honour.
    /// </summary>
    public class StubEngineTests
    {
        /// <summary>
        /// Statlines now come from content, so the tests supply their own pack rather than
        /// depending on numbers baked into the engine.
        /// </summary>
        private static readonly IContentPack Content = TestContent.Basic();

        /// <summary>
        /// Derived from the test pack rather than copied from it — if the fixture's Move
        /// changes, the boundary these tests assert moves with it.
        /// </summary>
        private static readonly int MoveAllowance =
            Content.GetUnit("test_unit").Stats.MoveInHexes;

        private static readonly UnitId A1 = new UnitId(1);
        private static readonly UnitId A2 = new UnitId(2);
        private static readonly UnitId B1 = new UnitId(3);

        /// <summary>
        /// Two units for A at (0,0) and (1,0), one for B at (3,0), on an empty radius-5 board.
        /// A is to act. Deliberately not SampleGame: these tests should not break when the
        /// sample layout is retuned.
        /// </summary>
        private static GameState Fixture()
        {
            var units = new List<UnitState>
            {
                MakeUnit(A1, PlayerId.A, new Hex(0, 0)),
                MakeUnit(A2, PlayerId.A, new Hex(1, 0)),
                MakeUnit(B1, PlayerId.B, new Hex(3, 0)),
            };

            return new GameState(
                round: 1,
                phase: PhaseKind.Activation,
                activePlayer: PlayerId.A,
                activeUnit: UnitId.None,
                board: new BoardState(radius: 5),
                units: units,
                objectives: new List<ObjectiveState>(),
                scoreA: 0,
                scoreB: 0,
                rng: new RngState(4242UL),
                contentVersion: "test");
        }

        private static UnitState MakeUnit(UnitId id, PlayerId owner, Hex pos) =>
            new UnitState(
                id, owner, "test_unit", pos,
                facing: 0,
                models: new List<ModelState> { new ModelState(1) },
                statuses: new List<StatusKind>(),
                hasActivated: false,
                actionsRemaining: 0);

        /// <summary>Run the activation so the unit is the active one with actions to spend.</summary>
        private static GameState Activate(IGameEngine engine, GameState state, UnitId unit)
        {
            var action = new ActivateUnit(state.ActivePlayer, unit);
            Assert.True(engine.Validate(state, action).IsLegal);
            return engine.Execute(state, action).NextState;
        }

        // ---- movement allowance ---------------------------------------------

        [Fact]
        public void AUnitCannotMoveFurtherThanItsAllowance()
        {
            var engine = new StubEngine(Content);
            var state = Activate(engine, Fixture(), A1);

            var start = new Hex(0, 0);
            var tooFar = new Hex(0, MoveAllowance + 1);   // 5 steps, allowance is 4
            Assert.Equal(MoveAllowance + 1, start.DistanceTo(tooFar));

            var result = engine.Validate(state, new MoveUnit(PlayerId.A, A1, start.LineTo(tooFar)));

            Assert.False(result.IsLegal);
            Assert.Equal(ReasonCodes.PathTooLong, result.ReasonCode);
        }

        [Fact]
        public void AUnitMayMoveExactlyItsAllowance()
        {
            // Pins the boundary: without this, an off-by-one that refuses legal moves passes.
            var engine = new StubEngine(Content);
            var state = Activate(engine, Fixture(), A1);

            var start = new Hex(0, 0);
            var edge = new Hex(0, MoveAllowance);
            Assert.Equal(MoveAllowance, start.DistanceTo(edge));

            var result = engine.Validate(state, new MoveUnit(PlayerId.A, A1, start.LineTo(edge)));

            Assert.True(result.IsLegal, $"expected legal, got {result}");
        }

        [Fact]
        public void AMoveOffTheBoardIsRefused()
        {
            var engine = new StubEngine(Content);
            var state = Activate(engine, Fixture(), A1);

            // Nothing within the 4-hex allowance of (0,0) is off a radius-5 board, so walk
            // to the edge first, then try to step past it with the second action.
            var edge = new Hex(0, -4);
            state = engine.Execute(
                state, new MoveUnit(PlayerId.A, A1, new Hex(0, 0).LineTo(edge))).NextState;
            Assert.Equal(edge, state.GetUnit(A1).Position);

            var result = engine.Validate(
                state, new MoveUnit(PlayerId.A, A1, edge.LineTo(new Hex(0, -6))));

            Assert.False(result.IsLegal);
            Assert.Equal(ReasonCodes.OffBoard, result.ReasonCode);
        }

        [Fact]
        public void AMoveOntoAnotherUnitIsRefused()
        {
            var engine = new StubEngine(Content);
            var state = Activate(engine, Fixture(), A1);

            var result = engine.Validate(
                state, new MoveUnit(PlayerId.A, A1, new Hex(0, 0).LineTo(new Hex(3, 0))));

            Assert.False(result.IsLegal);
            Assert.Equal(ReasonCodes.HexOccupied, result.ReasonCode);
        }

        [Fact]
        public void ANonContiguousPathIsRefused()
        {
            var engine = new StubEngine(Content);
            var state = Activate(engine, Fixture(), A1);

            var jump = new List<Hex> { new Hex(0, 0), new Hex(0, 2) };
            var result = engine.Validate(state, new MoveUnit(PlayerId.A, A1, jump));

            Assert.False(result.IsLegal);
            Assert.Equal(ReasonCodes.PathNotContiguous, result.ReasonCode);
        }

        // ---- friendly fire ---------------------------------------------------

        [Fact]
        public void AUnitCannotShootAFriendlyUnit()
        {
            var engine = new StubEngine(Content);
            var state = Activate(engine, Fixture(), A1);

            var result = engine.Validate(state, new ShootAt(PlayerId.A, A1, A2, "stub_weapon"));

            Assert.False(result.IsLegal);
            Assert.Equal(ReasonCodes.TargetFriendly, result.ReasonCode);
        }

        [Fact]
        public void AUnitCannotShootItself()
        {
            var engine = new StubEngine(Content);
            var state = Activate(engine, Fixture(), A1);

            var result = engine.Validate(state, new ShootAt(PlayerId.A, A1, A1, "stub_weapon"));

            Assert.False(result.IsLegal);
            Assert.Equal(ReasonCodes.TargetFriendly, result.ReasonCode);
        }

        [Fact]
        public void AUnitMayShootAnEnemyInRange()
        {
            // Control for the two above: the refusal must be about friendliness, not about
            // shooting being broken generally.
            var engine = new StubEngine(Content);
            var state = Activate(engine, Fixture(), A1);

            var result = engine.Validate(state, new ShootAt(PlayerId.A, A1, B1, "stub_weapon"));

            Assert.True(result.IsLegal, $"expected legal, got {result}");
        }

        // ---- acting out of turn ----------------------------------------------

        [Fact]
        public void APlayerCannotActivateOutOfTurn()
        {
            var engine = new StubEngine(Content);
            var state = Fixture();               // A is the active player
            Assert.Equal(PlayerId.A, state.ActivePlayer);

            var result = engine.Validate(state, new ActivateUnit(PlayerId.B, B1));

            Assert.False(result.IsLegal);
            Assert.Equal(ReasonCodes.NotYourTurn, result.ReasonCode);
        }

        [Fact]
        public void APlayerCannotMoveOutOfTurn()
        {
            var engine = new StubEngine(Content);
            var state = Activate(engine, Fixture(), A1);   // still A's turn, A1 active

            var result = engine.Validate(
                state, new MoveUnit(PlayerId.B, B1, new Hex(3, 0).LineTo(new Hex(3, 1))));

            Assert.False(result.IsLegal);
            Assert.Equal(ReasonCodes.NotYourTurn, result.ReasonCode);
        }

        [Fact]
        public void APlayerCannotShootOutOfTurn()
        {
            var engine = new StubEngine(Content);
            var state = Activate(engine, Fixture(), A1);

            var result = engine.Validate(state, new ShootAt(PlayerId.B, B1, A1, "stub_weapon"));

            Assert.False(result.IsLegal);
            Assert.Equal(ReasonCodes.NotYourTurn, result.ReasonCode);
        }

        [Fact]
        public void TurnPassesToTheOpponentAfterActivationEnds_AndTheFormerPlayerIsLockedOut()
        {
            var engine = new StubEngine(Content);
            var state = Activate(engine, Fixture(), A1);

            state = engine.Execute(state, new EndActivation(PlayerId.A, A1)).NextState;
            Assert.Equal(PlayerId.B, state.ActivePlayer);

            var result = engine.Validate(state, new ActivateUnit(PlayerId.A, A2));

            Assert.False(result.IsLegal);
            Assert.Equal(ReasonCodes.NotYourTurn, result.ReasonCode);
        }

        [Fact]
        public void ActingOnAnOpponentsUnitOnYourOwnTurn_IsNotYourUnit_NotNotYourTurn()
        {
            // Distinguishes the two codes: it IS A's turn, so the refusal must name the
            // ownership problem rather than the turn.
            var engine = new StubEngine(Content);
            var result = engine.Validate(Fixture(), new ActivateUnit(PlayerId.A, B1));

            Assert.False(result.IsLegal);
            Assert.Equal(ReasonCodes.NotYourUnit, result.ReasonCode);
        }

        // ---- surrounding contract --------------------------------------------

        [Fact]
        public void NothingIsLegalOnceTheMatchIsComplete()
        {
            var engine = new StubEngine(Content);
            var state = Fixture().With(phase: PhaseKind.Complete);

            var result = engine.Validate(state, new ActivateUnit(PlayerId.A, A1));

            Assert.False(result.IsLegal);
            Assert.Equal(ReasonCodes.MatchComplete, result.ReasonCode);
        }

        [Fact]
        public void ALegalResultCarriesNoReasonCode()
        {
            var engine = new StubEngine(Content);
            var result = engine.Validate(Fixture(), new ActivateUnit(PlayerId.A, A1));

            Assert.True(result.IsLegal);
            Assert.Null(result.ReasonCode);
        }

        [Fact]
        public void ExecuteDoesNotMutateTheStateItWasGiven()
        {
            // GameState is immutable: mutations return new instances.
            var engine = new StubEngine(Content);
            var before = Fixture();
            var positionBefore = before.GetUnit(A1).Position;

            var state = Activate(engine, before, A1);
            engine.Execute(state, new MoveUnit(PlayerId.A, A1, new Hex(0, 0).LineTo(new Hex(0, 3))));

            Assert.Equal(UnitId.None, before.ActiveUnit);
            Assert.Equal(positionBefore, before.GetUnit(A1).Position);
            Assert.Equal(0, before.GetUnit(A1).ActionsRemaining);
        }

        [Fact]
        public void EveryLegalActionOfferedActuallyValidates()
        {
            // The client highlights whatever LegalActions returns; an entry that then fails
            // Validate would present the player with a button that does nothing.
            var engine = new StubEngine(Content);
            var state = Activate(engine, Fixture(), A1);

            var offered = engine.LegalActions(state, PlayerId.A);

            Assert.NotEmpty(offered);
            Assert.All(offered, a => Assert.True(
                engine.Validate(state, a).IsLegal,
                $"LegalActions offered {a} but Validate refused it"));
        }

        [Fact]
        public void LegalActionsIsEmptyForThePlayerNotToAct()
        {
            var engine = new StubEngine(Content);
            Assert.Empty(engine.LegalActions(Fixture(), PlayerId.B));
        }
    }
}
