using System;
using System.Collections.Generic;

namespace Ironwake.Core
{
    /// <summary>
    /// The engine's read-only view of authored content.
    ///
    /// Core defines this interface but never loads it: parsing, file access and validation
    /// all live in Ironwake.Content. That is what keeps Core dependency-free and lets tests
    /// (and Unity) supply a hand-built pack with no JSON involved.
    ///
    /// Implementations must expose collections in a deterministic order — never raw
    /// Dictionary or HashSet enumeration order.
    /// </summary>
    public interface IContentPack
    {
        /// <summary>Identifies which content the state was built against, for replay safety.</summary>
        string Version { get; }

        /// <summary>Look up a unit. Throws <see cref="ContentNotFoundException"/> if absent.</summary>
        UnitDefinition GetUnit(string id);

        /// <summary>Look up a weapon. Throws <see cref="ContentNotFoundException"/> if absent.</summary>
        WeaponDefinition GetWeapon(string id);

        /// <summary>Look up a faction. Throws <see cref="ContentNotFoundException"/> if absent.</summary>
        FactionDefinition GetFaction(string id);

        /// <summary>Every unit in the pack, ordered by <see cref="UnitDefinition.Id"/>.</summary>
        IReadOnlyList<UnitDefinition> AllUnits { get; }

        /// <summary>Non-throwing lookup, for callers where a miss is expected and not an error.</summary>
        bool TryGetUnit(string id, out UnitDefinition unit);
    }

    /// <summary>
    /// Thrown when content is asked for an id it does not have.
    ///
    /// Lookups throw rather than returning null on purpose: a missing id is a content bug,
    /// and a null drifting through the engine surfaces far from its cause as a
    /// NullReferenceException with nothing useful in the message.
    /// </summary>
    public sealed class ContentNotFoundException : Exception
    {
        /// <summary>The id that was not found.</summary>
        public string Id { get; }

        /// <summary>What kind of thing was being looked up — "unit", "weapon", "faction".</summary>
        public string Kind { get; }

        public ContentNotFoundException(string kind, string id)
            : base($"No {kind} with id '{id}' exists in the content pack.")
        {
            Kind = kind;
            Id = id;
        }
    }
}
