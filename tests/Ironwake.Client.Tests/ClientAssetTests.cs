using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Ironwake.Client.Tests
{
    /// <summary>
    /// The client layer, checked without a browser. Two properties matter and both are
    /// readable from files: it must load nothing from the network, and it must build with
    /// nothing from the network.
    /// </summary>
    public class ClientAssetTests
    {
        /// <summary>Walks up from the test binary to the repo root.</summary>
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Ironwake.sln")))
                dir = dir.Parent;

            Assert.NotNull(dir);
            return dir.FullName;
        }

        private static string ClientDir => Path.Combine(RepoRoot(), "Ironwake.Client");
        private static string IndexHtml => Path.Combine(ClientDir, "wwwroot", "index.html");

        // ---- offline at runtime -----------------------------------------------------

        [Fact]
        public void ThePageExistsAndIsSubstantial()
        {
            Assert.True(File.Exists(IndexHtml), $"no page at {IndexHtml}");

            var html = File.ReadAllText(IndexHtml);
            Assert.True(html.Length > 4000, $"the page is only {html.Length} characters");
            Assert.StartsWith("<!DOCTYPE html>", html);
            Assert.Contains("</html>", html);
        }

        [Fact]
        public void NoServedAssetReferencesAnExternalUrl()
        {
            // The whole point of a local hotseat client: it must work with the network
            // unplugged. One CDN reference breaks that, and shows up here as a URL.
            foreach (var file in ServedFiles())
            {
                var text = File.ReadAllText(file);
                var name = Path.GetFileName(file);

                Assert.False(text.Contains("http://"), $"{name} contains an http:// URL");
                Assert.False(text.Contains("https://"), $"{name} contains an https:// URL");
                Assert.False(text.Contains("//cdn."), $"{name} contains a protocol-relative CDN reference");
            }
        }

        [Fact]
        public void NoServedAssetLoadsAScriptStylesheetOrImage()
        {
            // A relative src="app.js" carries no scheme and would slip past the URL check
            // while still being a second file that can go missing.
            foreach (var file in ServedFiles())
            {
                var text = File.ReadAllText(file);
                var name = Path.GetFileName(file);

                Assert.DoesNotMatch(new Regex(@"<script[^>]*\ssrc\s*=", RegexOptions.IgnoreCase), text);
                Assert.DoesNotMatch(new Regex(@"<link[^>]*\shref\s*=", RegexOptions.IgnoreCase), text);
                Assert.DoesNotMatch(new Regex(@"<img[^>]*\ssrc\s*=", RegexOptions.IgnoreCase), text);
                Assert.DoesNotMatch(new Regex(@"@import", RegexOptions.IgnoreCase), text);
                Assert.DoesNotMatch(new Regex(@"<iframe", RegexOptions.IgnoreCase), text);
            }
        }

        private static string[] ServedFiles() =>
            Directory.GetFiles(Path.Combine(ClientDir, "wwwroot"), "*", SearchOption.AllDirectories);

        // ---- offline at build time ---------------------------------------------------

        [Fact]
        public void TheClientHasNoPackageReferences()
        {
            // This is what makes the build offline: Microsoft.NET.Sdk.Web resolves against
            // the installed shared framework, so nothing is fetched from NuGet. A single
            // PackageReference would reintroduce a network dependency on a clean machine.
            var project = XDocument.Load(Path.Combine(ClientDir, "Ironwake.Client.csproj"));

            var packages = project.Descendants("PackageReference")
                .Select(e => e.Attribute("Include")?.Value)
                .ToList();

            Assert.Empty(packages);
        }

        [Fact]
        public void TheClientReferencesTheEngineAndContentOnly()
        {
            var project = XDocument.Load(Path.Combine(ClientDir, "Ironwake.Client.csproj"));

            var referenced = project.Descendants("ProjectReference")
                .Select(e => Path.GetFileNameWithoutExtension(
                    e.Attribute("Include")?.Value.Replace('\\', '/')))
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(new[] { "Ironwake.Content", "Ironwake.Core" }, referenced);
        }

        // ---- the constraint that matters ---------------------------------------------

        [Fact]
        public void TheBrowserIsNeverGivenRulesToApply()
        {
            // The page must not decide legality. It receives action ids the engine produced
            // and posts one back; anything resembling a rule in here is the client computing
            // an outcome, which this project has been bitten by four times.
            var html = File.ReadAllText(IndexHtml);

            foreach (var forbidden in new[]
            {
                "DistanceTo", "ReachableFrom", "FindPath", "LineOfSight",
                "Wounding", "TargetFor", "ControllerOf", "IsMatchOver",
            })
            {
                Assert.False(html.Contains(forbidden),
                    $"the page references {forbidden} — rules belong in the engine, not the client");
            }

            // And it does post an engine-issued id rather than a constructed action.
            Assert.Contains("actionId", html);
        }

        [Fact]
        public void ThePageSaysThereIsNoUndo()
        {
            // Requirement: say so rather than faking it.
            var html = File.ReadAllText(IndexHtml);
            Assert.Contains("No undo", html);
        }
    }
}
