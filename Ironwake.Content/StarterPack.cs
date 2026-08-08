using System.IO;
using Ironwake.Core;

namespace Ironwake.Content
{
    /// <summary>
    /// Locates and loads the starter content that ships beside the assembly.
    ///
    /// Callers use this instead of hard-coding a path, so the console harness, the tests
    /// and (later) the server all resolve the same content the same way.
    /// </summary>
    public static class StarterPack
    {
        /// <summary>Folder name the starter content is copied into on build.</summary>
        public const string FolderName = "StarterPack";

        /// <summary>Absolute path to the starter content beside the running assembly.</summary>
        public static string DefaultPath =>
            Path.Combine(System.AppContext.BaseDirectory, FolderName);

        /// <summary>Load the starter pack from its default location.</summary>
        public static IContentPack Load() => JsonContentPack.LoadFromDirectory(DefaultPath);
    }
}
