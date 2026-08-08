using System.Linq;
using Ironwake.Core;
using Xunit;

namespace Ironwake.Content.Tests
{
    /// <summary>
    /// Every validation rule, asserted by error CODE rather than by message text — the codes
    /// are the contract, the wording is not.
    /// </summary>
    public class ValidationTests
    {
        [Fact]
        public void AValidPackLoadsWithoutComplaint()
        {
            // Baseline. If this ever fails, every other test here is testing the wrong thing.
            using var temp = new TempPack()
                .Factions(Json.OneFaction)
                .Weapons(Json.OneWeapon)
                .Units(Json.Units(Json.Unit()));

            var pack = temp.Load();

            Assert.Single(pack.AllUnits);
            Assert.Equal("u1", pack.AllUnits[0].Id);
        }

        // ---- duplicate ids ---------------------------------------------------

        [Fact]
        public void DuplicateUnitIdIsRejected()
        {
            using var temp = new TempPack()
                .Factions(Json.OneFaction)
                .Weapons(Json.OneWeapon)
                .Units(Json.Units(Json.Unit(id: "same"), Json.Unit(id: "same")));

            var ex = Assert.Throws<ContentValidationException>(() => temp.Load());

            var error = Assert.Single(ex.Errors);
            Assert.Equal(ContentErrorCodes.DuplicateId, error.Code);
            Assert.Equal("same", error.Id);
        }

        [Fact]
        public void DuplicateWeaponIdIsRejected()
        {
            using var temp = new TempPack()
                .Factions(Json.OneFaction)
                .Weapons(@"[
                    { ""id"": ""w1"", ""range"": 60, ""attacks"": 2, ""power"": 4, ""armourPiercing"": 0, ""damage"": 1 },
                    { ""id"": ""w1"", ""range"": 30, ""attacks"": 1, ""power"": 3, ""armourPiercing"": 0, ""damage"": 1 }
                ]")
                .Units(Json.Units(Json.Unit()));

            var ex = Assert.Throws<ContentValidationException>(() => temp.Load());

            Assert.Contains(ex.Errors, e =>
                e.Code == ContentErrorCodes.DuplicateId && e.Id == "w1");
        }

        [Fact]
        public void DuplicateFactionIdIsRejected()
        {
            using var temp = new TempPack()
                .Factions(@"[
                    { ""id"": ""f1"", ""displayName"": ""One"" },
                    { ""id"": ""f1"", ""displayName"": ""Also One"" }
                ]")
                .Weapons(Json.OneWeapon)
                .Units(Json.Units(Json.Unit()));

            var ex = Assert.Throws<ContentValidationException>(() => temp.Load());

            Assert.Contains(ex.Errors, e =>
                e.Code == ContentErrorCodes.DuplicateId && e.Id == "f1");
        }

        // ---- broken references ----------------------------------------------

        [Fact]
        public void UnitReferencingAMissingWeaponIsRejected()
        {
            using var temp = new TempPack()
                .Factions(Json.OneFaction)
                .Weapons(Json.OneWeapon)
                .Units(Json.Units(Json.Unit(weaponId: "no_such_weapon")));

            var ex = Assert.Throws<ContentValidationException>(() => temp.Load());

            var error = Assert.Single(ex.Errors);
            Assert.Equal(ContentErrorCodes.UnknownWeapon, error.Code);
            Assert.Equal("u1", error.Id);
            Assert.Contains("no_such_weapon", error.Message);
        }

        [Fact]
        public void UnitReferencingAMissingFactionIsRejected()
        {
            using var temp = new TempPack()
                .Factions(Json.OneFaction)
                .Weapons(Json.OneWeapon)
                .Units(Json.Units(Json.Unit(factionId: "no_such_faction")));

            var ex = Assert.Throws<ContentValidationException>(() => temp.Load());

            var error = Assert.Single(ex.Errors);
            Assert.Equal(ContentErrorCodes.UnknownFaction, error.Code);
            Assert.Equal("u1", error.Id);
            Assert.Contains("no_such_faction", error.Message);
        }

        // ---- stat bands -------------------------------------------------------

        [Theory]
        [InlineData("accuracy", 1)]
        [InlineData("accuracy", 8)]
        [InlineData("melee", 1)]
        [InlineData("melee", 8)]
        [InlineData("save", 1)]
        [InlineData("save", 8)]
        public void RollTargetsOutsideTwoToSevenAreRejected(string stat, int value)
        {
            var unit = Json.Unit(
                accuracy: stat == "accuracy" ? value : 4,
                melee: stat == "melee" ? value : 4,
                save: stat == "save" ? value : 5);

            using var temp = new TempPack()
                .Factions(Json.OneFaction).Weapons(Json.OneWeapon).Units(Json.Units(unit));

            var ex = Assert.Throws<ContentValidationException>(() => temp.Load());

            var error = Assert.Single(ex.Errors);
            Assert.Equal(ContentErrorCodes.StatOutOfRange, error.Code);
            Assert.Contains(stat, error.Message, System.StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData(2)]
        [InlineData(7)]
        public void RollTargetsAtTheBandEdgesAreAccepted(int value)
        {
            // Pins the boundary: 2+ is the best possible roll and 7 means "cannot".
            using var temp = new TempPack()
                .Factions(Json.OneFaction).Weapons(Json.OneWeapon)
                .Units(Json.Units(Json.Unit(accuracy: value, melee: value, save: value)));

            Assert.Single(temp.Load().AllUnits);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(11)]
        public void ResilienceOutsideOneToTenIsRejected(int value)
        {
            using var temp = new TempPack()
                .Factions(Json.OneFaction).Weapons(Json.OneWeapon)
                .Units(Json.Units(Json.Unit(resilience: value)));

            var ex = Assert.Throws<ContentValidationException>(() => temp.Load());

            Assert.Equal(ContentErrorCodes.StatOutOfRange, Assert.Single(ex.Errors).Code);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(21)]
        public void WoundsOutsideOneToTwentyIsRejected(int value)
        {
            using var temp = new TempPack()
                .Factions(Json.OneFaction).Weapons(Json.OneWeapon)
                .Units(Json.Units(Json.Unit(wounds: value)));

            var ex = Assert.Throws<ContentValidationException>(() => temp.Load());

            Assert.Equal(ContentErrorCodes.StatOutOfRange, Assert.Single(ex.Errors).Code);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(21)]
        public void ModelCountOutsideOneToTwentyIsRejected(int value)
        {
            using var temp = new TempPack()
                .Factions(Json.OneFaction).Weapons(Json.OneWeapon)
                .Units(Json.Units(Json.Unit(modelCount: value)));

            var ex = Assert.Throws<ContentValidationException>(() => temp.Load());

            Assert.Equal(ContentErrorCodes.StatOutOfRange, Assert.Single(ex.Errors).Code);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-25)]
        public void PointsMustBeGreaterThanZero(int value)
        {
            using var temp = new TempPack()
                .Factions(Json.OneFaction).Weapons(Json.OneWeapon)
                .Units(Json.Units(Json.Unit(points: value)));

            var ex = Assert.Throws<ContentValidationException>(() => temp.Load());

            var error = Assert.Single(ex.Errors);
            Assert.Equal(ContentErrorCodes.StatOutOfRange, error.Code);
            Assert.Contains("Points", error.Message);
        }

        // ---- all errors at once -----------------------------------------------

        [Fact]
        public void APackWithThreeDistinctErrorsReportsAllThree()
        {
            // The whole reason validation collects instead of throwing on first sight:
            // an author should be able to fix everything in one pass.
            using var temp = new TempPack()
                .Factions(Json.OneFaction)
                .Weapons(Json.OneWeapon)
                .Units(Json.Units(
                    Json.Unit(id: "bad_stat", accuracy: 99),
                    Json.Unit(id: "bad_weapon", weaponId: "ghost_gun"),
                    Json.Unit(id: "twin"),
                    Json.Unit(id: "twin")));

            var ex = Assert.Throws<ContentValidationException>(() => temp.Load());

            Assert.Equal(3, ex.Errors.Count);
            Assert.True(ex.Has(ContentErrorCodes.StatOutOfRange));
            Assert.True(ex.Has(ContentErrorCodes.UnknownWeapon));
            Assert.True(ex.Has(ContentErrorCodes.DuplicateId));

            // Every error names the definition it belongs to.
            Assert.Contains(ex.Errors, e => e.Id == "bad_stat");
            Assert.Contains(ex.Errors, e => e.Id == "bad_weapon");
            Assert.Contains(ex.Errors, e => e.Id == "twin");
        }

        [Fact]
        public void TheExceptionMessageListsEveryError()
        {
            using var temp = new TempPack()
                .Factions(Json.OneFaction)
                .Weapons(Json.OneWeapon)
                .Units(Json.Units(
                    Json.Unit(id: "a", accuracy: 99),
                    Json.Unit(id: "b", weaponId: "ghost_gun")));

            var ex = Assert.Throws<ContentValidationException>(() => temp.Load());

            Assert.Contains("2 errors", ex.Message);
            Assert.Contains("a", ex.Message);
            Assert.Contains("ghost_gun", ex.Message);
        }

        [Fact]
        public void ErrorOrderIsStableAcrossLoads()
        {
            using var temp = new TempPack()
                .Factions(Json.OneFaction)
                .Weapons(Json.OneWeapon)
                .Units(Json.Units(
                    Json.Unit(id: "zeta", accuracy: 99),
                    Json.Unit(id: "alpha", weaponId: "ghost_gun"),
                    Json.Unit(id: "mid", save: 0)));

            var first = Assert.Throws<ContentValidationException>(() => temp.Load());
            var second = Assert.Throws<ContentValidationException>(() => temp.Load());

            Assert.Equal(
                first.Errors.Select(e => e.ToString()),
                second.Errors.Select(e => e.ToString()));
        }

        // ---- malformed input --------------------------------------------------

        [Fact]
        public void UnparseableJsonIsReportedRatherThanThrowingRaw()
        {
            using var temp = new TempPack()
                .Factions(Json.OneFaction)
                .Weapons(Json.OneWeapon)
                .Units("{ this is not json");

            var ex = Assert.Throws<ContentValidationException>(() => temp.Load());

            var error = Assert.Single(ex.Errors);
            Assert.Equal(ContentErrorCodes.InvalidJson, error.Code);
            Assert.Contains("units.json", error.Id);
        }

        [Fact]
        public void AUnitWithNoIdIsReported()
        {
            using var temp = new TempPack()
                .Factions(Json.OneFaction)
                .Weapons(Json.OneWeapon)
                .Units(@"[{ ""displayName"": ""Nameless"", ""points"": 10, ""modelCount"": 1 }]");

            var ex = Assert.Throws<ContentValidationException>(() => temp.Load());

            Assert.Equal(ContentErrorCodes.MissingField, Assert.Single(ex.Errors).Code);
        }

        [Fact]
        public void AMissingDirectoryIsADistinctFailure()
        {
            // Not a content error — the caller pointed at nothing at all.
            Assert.Throws<System.IO.DirectoryNotFoundException>(
                () => JsonContentPack.LoadFromDirectory("/no/such/content/anywhere"));
        }

        // ---- file layout tolerance --------------------------------------------

        [Fact]
        public void AFileMayHoldASingleObjectInsteadOfAnArray()
        {
            using var temp = new TempPack()
                .Factions(@"{ ""id"": ""f1"", ""displayName"": ""Faction One"" }")
                .Weapons(Json.OneWeapon)
                .Units(Json.Unit());

            var pack = temp.Load();

            Assert.Single(pack.AllUnits);
            Assert.Equal("f1", pack.GetFaction("f1").Id);
        }

        [Fact]
        public void ContentSplitAcrossSeveralFilesIsCombined()
        {
            using var temp = new TempPack()
                .Factions(Json.OneFaction)
                .Weapons(Json.OneWeapon);
            temp.Write("units", "a.json", Json.Units(Json.Unit(id: "u_a")));
            temp.Write("units", "b.json", Json.Units(Json.Unit(id: "u_b")));

            var pack = temp.Load();

            Assert.Equal(new[] { "u_a", "u_b" }, pack.AllUnits.Select(u => u.Id));
        }
    }
}
