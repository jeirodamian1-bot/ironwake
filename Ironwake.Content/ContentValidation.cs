using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Ironwake.Content
{
    /// <summary>Canonical validation failure codes. Add here, not as raw strings at call sites.</summary>
    public static class ContentErrorCodes
    {
        /// <summary>A file could not be parsed as JSON at all.</summary>
        public const string InvalidJson = "INVALID_JSON";

        /// <summary>A required field (id, stats block) was absent or blank.</summary>
        public const string MissingField = "MISSING_FIELD";

        /// <summary>Two definitions of the same kind share an id.</summary>
        public const string DuplicateId = "DUPLICATE_ID";

        /// <summary>A unit carries a weapon id that no weapon defines.</summary>
        public const string UnknownWeapon = "UNKNOWN_WEAPON";

        /// <summary>A unit belongs to a faction id that no faction defines.</summary>
        public const string UnknownFaction = "UNKNOWN_FACTION";

        /// <summary>A stat falls outside its permitted band.</summary>
        public const string StatOutOfRange = "STAT_OUT_OF_RANGE";
    }

    /// <summary>
    /// Permitted stat bands, inclusive at both ends. Content that falls outside these is
    /// rejected at load rather than producing nonsense at the table.
    /// </summary>
    public static class StatRanges
    {
        /// <summary>Roll targets: 2+ at best, 7 meaning "cannot".</summary>
        public const int RollTargetMin = 2;
        public const int RollTargetMax = 7;

        public const int ResilienceMin = 1;
        public const int ResilienceMax = 10;

        public const int WoundsMin = 1;
        public const int WoundsMax = 20;

        public const int ModelCountMin = 1;
        public const int ModelCountMax = 20;

        /// <summary>Points must be strictly positive — a free unit breaks list building.</summary>
        public const int PointsMin = 1;
    }

    /// <summary>One thing wrong with a content pack. Packs report every error, not just the first.</summary>
    public sealed class ContentError
    {
        /// <summary>A <see cref="ContentErrorCodes"/> value.</summary>
        public string Code { get; }

        /// <summary>The id of the offending definition, or the file name when the id is unknown.</summary>
        public string Id { get; }

        /// <summary>Human-readable explanation, including the offending value where relevant.</summary>
        public string Message { get; }

        public ContentError(string code, string id, string message)
        {
            Code = code;
            Id = id;
            Message = message;
        }

        public override string ToString() => $"[{Code}] {Id}: {Message}";
    }

    /// <summary>
    /// Thrown when a pack fails to load. Carries EVERY error found, because fixing content
    /// one error per build is miserable — an author wants the whole list in one pass.
    /// </summary>
    public sealed class ContentValidationException : Exception
    {
        /// <summary>All failures, in a deterministic order.</summary>
        public IReadOnlyList<ContentError> Errors { get; }

        public ContentValidationException(IReadOnlyList<ContentError> errors)
            : base(BuildMessage(errors))
        {
            Errors = errors ?? Array.Empty<ContentError>();
        }

        /// <summary>True if any error carries the given code.</summary>
        public bool Has(string code) => Errors.Any(e => e.Code == code);

        private static string BuildMessage(IReadOnlyList<ContentError> errors)
        {
            if (errors == null || errors.Count == 0) return "Content validation failed.";

            var sb = new StringBuilder();
            sb.Append("Content validation failed with ").Append(errors.Count)
              .Append(errors.Count == 1 ? " error:" : " errors:");
            foreach (var e in errors) sb.AppendLine().Append("  ").Append(e);
            return sb.ToString();
        }
    }
}
