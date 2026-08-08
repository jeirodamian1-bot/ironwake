using System;
using System.IO;

namespace Ironwake.Content.Tests
{
    /// <summary>
    /// Writes a throwaway pack to disk so validation is exercised through the real loader
    /// rather than a mock of it. Deleted on dispose.
    /// </summary>
    internal sealed class TempPack : IDisposable
    {
        public string Root { get; }

        public TempPack()
        {
            Root = Path.Combine(Path.GetTempPath(), "ironwake-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public TempPack Weapons(string json) => Write("weapons", "weapons.json", json);
        public TempPack Units(string json) => Write("units", "units.json", json);
        public TempPack Factions(string json) => Write("factions", "factions.json", json);

        public TempPack Write(string subdirectory, string fileName, string json)
        {
            var dir = Path.Combine(Root, subdirectory);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, fileName), json);
            return this;
        }

        public JsonContentPack Load() => JsonContentPack.LoadFromDirectory(Root);

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { /* a leftover temp dir is not worth failing a test over */ }
        }
    }

    /// <summary>
    /// JSON fragments for building packs. Every helper defaults to valid content so a test
    /// can perturb exactly one thing and be sure that thing caused the failure.
    /// </summary>
    internal static class Json
    {
        public const string OneFaction = @"[{ ""id"": ""f1"", ""displayName"": ""Faction One"" }]";

        public const string OneWeapon = @"[{
            ""id"": ""w1"", ""displayName"": ""Weapon One"",
            ""range"": 60, ""attacks"": 2, ""power"": 4, ""armourPiercing"": 0, ""damage"": 1
        }]";

        /// <summary>A valid unit, with every field overridable.</summary>
        public static string Unit(
            string id = "u1", string factionId = "f1", string weaponId = "w1",
            int points = 50, int modelCount = 5, int move = 40, int accuracy = 4,
            int melee = 4, int resilience = 5, int save = 5, int wounds = 1, int nerve = 6)
            => $@"{{
                ""id"": ""{id}"", ""factionId"": ""{factionId}"", ""displayName"": ""Unit"",
                ""points"": {points}, ""modelCount"": {modelCount},
                ""stats"": {{
                    ""move"": {move}, ""accuracy"": {accuracy}, ""melee"": {melee},
                    ""resilience"": {resilience}, ""save"": {save},
                    ""wounds"": {wounds}, ""nerve"": {nerve}
                }},
                ""weaponIds"": [""{weaponId}""]
            }}";

        /// <summary>Wraps unit objects into the array form a units file expects.</summary>
        public static string Units(params string[] units) => "[" + string.Join(",", units) + "]";
    }
}
