using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Ironwake.Core;

namespace Ironwake.Content
{
    /// <summary>
    /// An <see cref="IContentPack"/> loaded from a directory of JSON files:
    ///
    /// <code>
    /// &lt;root&gt;/
    ///   pack.json          optional, supplies Version
    ///   factions/*.json
    ///   units/*.json
    ///   weapons/*.json
    /// </code>
    ///
    /// Each file may hold a single object or an array of objects, so content can be split
    /// one-per-file or grouped, whichever an author prefers.
    ///
    /// Everything is read and validated up front. Once constructed the pack is immutable
    /// and every collection it exposes is in a deterministic, explicitly sorted order —
    /// Dictionary enumeration order never reaches a caller.
    /// </summary>
    public sealed class JsonContentPack : IContentPack
    {
        private readonly Dictionary<string, UnitDefinition> _units;
        private readonly Dictionary<string, WeaponDefinition> _weapons;
        private readonly Dictionary<string, FactionDefinition> _factions;

        /// <inheritdoc />
        public string Version { get; }

        /// <inheritdoc />
        public IReadOnlyList<UnitDefinition> AllUnits { get; }

        /// <summary>Every weapon in the pack, ordered by id.</summary>
        public IReadOnlyList<WeaponDefinition> AllWeapons { get; }

        /// <summary>Every faction in the pack, ordered by id.</summary>
        public IReadOnlyList<FactionDefinition> AllFactions { get; }

        private JsonContentPack(
            string version,
            IReadOnlyList<UnitDefinition> units,
            IReadOnlyList<WeaponDefinition> weapons,
            IReadOnlyList<FactionDefinition> factions)
        {
            Version = version;
            AllUnits = units;
            AllWeapons = weapons;
            AllFactions = factions;

            // Ordinal comparers throughout: the default string comparer is culture-sensitive,
            // which would make lookups and ordering vary by machine locale.
            _units = units.ToDictionary(u => u.Id, StringComparer.Ordinal);
            _weapons = weapons.ToDictionary(w => w.Id, StringComparer.Ordinal);
            _factions = factions.ToDictionary(f => f.Id, StringComparer.Ordinal);
        }

        // ---- LOOKUPS --------------------------------------------------------

        /// <inheritdoc />
        public UnitDefinition GetUnit(string id) =>
            _units.TryGetValue(id ?? string.Empty, out var u)
                ? u
                : throw new ContentNotFoundException("unit", id);

        /// <inheritdoc />
        public WeaponDefinition GetWeapon(string id) =>
            _weapons.TryGetValue(id ?? string.Empty, out var w)
                ? w
                : throw new ContentNotFoundException("weapon", id);

        /// <inheritdoc />
        public FactionDefinition GetFaction(string id) =>
            _factions.TryGetValue(id ?? string.Empty, out var f)
                ? f
                : throw new ContentNotFoundException("faction", id);

        /// <inheritdoc />
        public bool TryGetUnit(string id, out UnitDefinition unit) =>
            _units.TryGetValue(id ?? string.Empty, out unit);

        // ---- LOADING --------------------------------------------------------

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        /// <summary>
        /// Read and validate a pack. Throws <see cref="ContentValidationException"/> carrying
        /// EVERY problem found, so an author can fix the whole pack in one pass.
        /// </summary>
        /// <exception cref="DirectoryNotFoundException">If <paramref name="rootDirectory"/> does not exist.</exception>
        /// <exception cref="ContentValidationException">If the content is invalid.</exception>
        public static JsonContentPack LoadFromDirectory(string rootDirectory)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory))
                throw new ArgumentException("Content root must be supplied.", nameof(rootDirectory));
            if (!Directory.Exists(rootDirectory))
                throw new DirectoryNotFoundException($"No content directory at '{rootDirectory}'.");

            var errors = new List<ContentError>();

            var weaponDtos = ReadDirectory<WeaponDto>(Path.Combine(rootDirectory, "weapons"), errors);
            var unitDtos = ReadDirectory<UnitDto>(Path.Combine(rootDirectory, "units"), errors);
            var factionDtos = ReadDirectory<FactionDto>(Path.Combine(rootDirectory, "factions"), errors);

            var weapons = BuildWeapons(weaponDtos, errors);
            var factions = BuildFactions(factionDtos, errors);
            var units = BuildUnits(unitDtos, weapons, factions, errors);

            if (errors.Count > 0) throw new ContentValidationException(SortErrors(errors));

            var orderedUnits = units.Values
                .OrderBy(u => u.Id, StringComparer.Ordinal)
                .ToList();

            // Faction membership is derived from each unit's FactionId rather than authored
            // separately — two sources of truth for the same fact will disagree eventually.
            var orderedFactions = factions.Values
                .OrderBy(f => f.Id, StringComparer.Ordinal)
                .Select(f => new FactionDefinition(
                    f.Id,
                    f.DisplayName,
                    orderedUnits.Where(u => string.Equals(u.FactionId, f.Id, StringComparison.Ordinal))
                                .Select(u => u.Id)
                                .ToList()))
                .ToList();

            var orderedWeapons = weapons.Values
                .OrderBy(w => w.Id, StringComparer.Ordinal)
                .ToList();

            return new JsonContentPack(
                ReadVersion(rootDirectory), orderedUnits, orderedWeapons, orderedFactions);
        }

        private static string ReadVersion(string rootDirectory)
        {
            var path = Path.Combine(rootDirectory, "pack.json");
            if (!File.Exists(path)) return "unversioned";

            try
            {
                var dto = JsonSerializer.Deserialize<PackDto>(File.ReadAllText(path), JsonOptions);
                return string.IsNullOrWhiteSpace(dto?.Version) ? "unversioned" : dto.Version;
            }
            catch (JsonException)
            {
                return "unversioned";
            }
        }

        /// <summary>
        /// Read every *.json in a directory. Files are visited in ordinal name order so that
        /// error output is identical run to run.
        /// </summary>
        private static List<T> ReadDirectory<T>(string directory, List<ContentError> errors)
        {
            var results = new List<T>();
            if (!Directory.Exists(directory)) return results;

            var files = Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly);
            Array.Sort(files, StringComparer.Ordinal);

            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                try
                {
                    var text = File.ReadAllText(file);
                    using var doc = JsonDocument.Parse(text, new JsonDocumentOptions
                    {
                        CommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true,
                    });

                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var element in doc.RootElement.EnumerateArray())
                            results.Add(JsonSerializer.Deserialize<T>(element.GetRawText(), JsonOptions));
                    }
                    else
                    {
                        results.Add(JsonSerializer.Deserialize<T>(text, JsonOptions));
                    }
                }
                catch (JsonException ex)
                {
                    errors.Add(new ContentError(
                        ContentErrorCodes.InvalidJson, name,
                        $"Could not parse '{name}': {ex.Message}"));
                }
            }

            return results;
        }

        // ---- BUILD + VALIDATE ----------------------------------------------

        private static Dictionary<string, WeaponDefinition> BuildWeapons(
            List<WeaponDto> dtos, List<ContentError> errors)
        {
            var map = new Dictionary<string, WeaponDefinition>(StringComparer.Ordinal);

            foreach (var dto in dtos.Where(d => d != null))
            {
                if (string.IsNullOrWhiteSpace(dto.Id))
                {
                    errors.Add(new ContentError(ContentErrorCodes.MissingField, "(weapon)",
                        "A weapon has no id."));
                    continue;
                }
                if (map.ContainsKey(dto.Id))
                {
                    errors.Add(new ContentError(ContentErrorCodes.DuplicateId, dto.Id,
                        $"Weapon id '{dto.Id}' is defined more than once."));
                    continue;
                }

                map[dto.Id] = new WeaponDefinition(
                    dto.Id, dto.DisplayName ?? dto.Id, dto.Range, dto.Attacks,
                    dto.Power, dto.ArmourPiercing, dto.Damage, dto.Keywords);
            }

            return map;
        }

        private static Dictionary<string, FactionDefinition> BuildFactions(
            List<FactionDto> dtos, List<ContentError> errors)
        {
            var map = new Dictionary<string, FactionDefinition>(StringComparer.Ordinal);

            foreach (var dto in dtos.Where(d => d != null))
            {
                if (string.IsNullOrWhiteSpace(dto.Id))
                {
                    errors.Add(new ContentError(ContentErrorCodes.MissingField, "(faction)",
                        "A faction has no id."));
                    continue;
                }
                if (map.ContainsKey(dto.Id))
                {
                    errors.Add(new ContentError(ContentErrorCodes.DuplicateId, dto.Id,
                        $"Faction id '{dto.Id}' is defined more than once."));
                    continue;
                }

                map[dto.Id] = new FactionDefinition(dto.Id, dto.DisplayName ?? dto.Id);
            }

            return map;
        }

        private static Dictionary<string, UnitDefinition> BuildUnits(
            List<UnitDto> dtos,
            Dictionary<string, WeaponDefinition> weapons,
            Dictionary<string, FactionDefinition> factions,
            List<ContentError> errors)
        {
            var map = new Dictionary<string, UnitDefinition>(StringComparer.Ordinal);

            foreach (var dto in dtos.Where(d => d != null))
            {
                if (string.IsNullOrWhiteSpace(dto.Id))
                {
                    errors.Add(new ContentError(ContentErrorCodes.MissingField, "(unit)",
                        "A unit has no id."));
                    continue;
                }
                if (map.ContainsKey(dto.Id))
                {
                    errors.Add(new ContentError(ContentErrorCodes.DuplicateId, dto.Id,
                        $"Unit id '{dto.Id}' is defined more than once."));
                    continue;
                }

                if (dto.Stats == null)
                {
                    errors.Add(new ContentError(ContentErrorCodes.MissingField, dto.Id,
                        $"Unit '{dto.Id}' has no stats block."));
                    continue;
                }

                // Referential integrity.
                if (string.IsNullOrWhiteSpace(dto.FactionId))
                {
                    errors.Add(new ContentError(ContentErrorCodes.MissingField, dto.Id,
                        $"Unit '{dto.Id}' has no factionId."));
                }
                else if (!factions.ContainsKey(dto.FactionId))
                {
                    errors.Add(new ContentError(ContentErrorCodes.UnknownFaction, dto.Id,
                        $"Unit '{dto.Id}' belongs to faction '{dto.FactionId}', which does not exist."));
                }

                if (dto.WeaponIds != null)
                {
                    foreach (var weaponId in dto.WeaponIds)
                    {
                        if (!weapons.ContainsKey(weaponId ?? string.Empty))
                        {
                            errors.Add(new ContentError(ContentErrorCodes.UnknownWeapon, dto.Id,
                                $"Unit '{dto.Id}' carries weapon '{weaponId}', which does not exist."));
                        }
                    }
                }

                ValidateStats(dto, errors);

                map[dto.Id] = new UnitDefinition(
                    dto.Id, dto.FactionId, dto.DisplayName ?? dto.Id, dto.Points, dto.ModelCount,
                    new Statline(
                        dto.Stats.Move, dto.Stats.Accuracy, dto.Stats.Melee, dto.Stats.Resilience,
                        dto.Stats.Save, dto.Stats.Wounds, dto.Stats.Nerve),
                    dto.WeaponIds, dto.AbilityIds, dto.Keywords);
            }

            return map;
        }

        private static void ValidateStats(UnitDto dto, List<ContentError> errors)
        {
            var s = dto.Stats;

            CheckBand(dto.Id, "Accuracy", s.Accuracy, StatRanges.RollTargetMin, StatRanges.RollTargetMax, errors);
            CheckBand(dto.Id, "Melee", s.Melee, StatRanges.RollTargetMin, StatRanges.RollTargetMax, errors);
            CheckBand(dto.Id, "Save", s.Save, StatRanges.RollTargetMin, StatRanges.RollTargetMax, errors);
            CheckBand(dto.Id, "Resilience", s.Resilience, StatRanges.ResilienceMin, StatRanges.ResilienceMax, errors);
            CheckBand(dto.Id, "Wounds", s.Wounds, StatRanges.WoundsMin, StatRanges.WoundsMax, errors);
            CheckBand(dto.Id, "ModelCount", dto.ModelCount, StatRanges.ModelCountMin, StatRanges.ModelCountMax, errors);

            if (dto.Points < StatRanges.PointsMin)
            {
                errors.Add(new ContentError(ContentErrorCodes.StatOutOfRange, dto.Id,
                    $"Unit '{dto.Id}' has Points {dto.Points}; must be greater than 0."));
            }
        }

        private static void CheckBand(
            string id, string stat, int value, int min, int max, List<ContentError> errors)
        {
            if (value < min || value > max)
            {
                errors.Add(new ContentError(ContentErrorCodes.StatOutOfRange, id,
                    $"Unit '{id}' has {stat} {value}; permitted range is {min}-{max}."));
            }
        }

        /// <summary>Stable error ordering so failures read the same way on every machine.</summary>
        private static IReadOnlyList<ContentError> SortErrors(List<ContentError> errors) =>
            errors
                .OrderBy(e => e.Code, StringComparer.Ordinal)
                .ThenBy(e => e.Id, StringComparer.Ordinal)
                .ThenBy(e => e.Message, StringComparer.Ordinal)
                .ToList();

        // ---- DTOs -----------------------------------------------------------
        // Deliberately separate from the Core types: these are mutable, tolerant of missing
        // fields, and exist only long enough to be validated and mapped into immutable ones.

        private sealed class PackDto
        {
            public string Version { get; set; }
        }

        private sealed class StatlineDto
        {
            public int Move { get; set; }
            public int Accuracy { get; set; }
            public int Melee { get; set; }
            public int Resilience { get; set; }
            public int Save { get; set; }
            public int Wounds { get; set; }
            public int Nerve { get; set; }
        }

        private sealed class UnitDto
        {
            public string Id { get; set; }
            public string FactionId { get; set; }
            public string DisplayName { get; set; }
            public int Points { get; set; }
            public int ModelCount { get; set; }
            public StatlineDto Stats { get; set; }
            public List<string> WeaponIds { get; set; }
            public List<string> AbilityIds { get; set; }
            public List<string> Keywords { get; set; }
        }

        private sealed class WeaponDto
        {
            public string Id { get; set; }
            public string DisplayName { get; set; }
            public int Range { get; set; }
            public int Attacks { get; set; }
            public int Power { get; set; }
            public int ArmourPiercing { get; set; }
            public int Damage { get; set; }
            public List<string> Keywords { get; set; }
        }

        private sealed class FactionDto
        {
            public string Id { get; set; }
            public string DisplayName { get; set; }
        }
    }
}
