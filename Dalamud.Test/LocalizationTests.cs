using System.IO;
using System.Reflection;

using CheapLoc;

using Dalamud.Interface.Internal;
using Dalamud.Interface.Internal.Windows.PluginInstaller;
using Dalamud.Plugin.Internal.Types.Manifest;

using Xunit;

namespace Dalamud.Test
{
    public class LocalizationTests
    {
        private readonly Localization localization;
        private string currentLangCode = "en";

        public LocalizationTests()
        {
            var workingDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
            this.localization = new Localization(workingDir, "dalamud_");
            this.localization.LocalizationChanged += code => this.currentLangCode = code;
        }

        [Fact]
        public void SetupWithFallbacks_EventInvoked()
        {
            this.localization.SetupWithFallbacks();
            Assert.Equal("en", this.currentLangCode);
        }

        [Fact]
        public void CoreLookupTracksLanguageChangesAndPreservesPluginOwnership()
        {
            var coreAssembly = typeof(Localization).Assembly;
            var pluginAssembly = typeof(LocalizationTests).Assembly;
            try
            {
                Loc.Setup("""{"InstallerHeader":{"message":"Core title"},"InstallerCategoryAll":{"message":"Core category"}}""", coreAssembly);
                Loc.Setup("""{"InstallerHeader":{"message":"Plugin title"}}""", pluginAssembly);

                Assert.Equal("Core title", PluginInstallerWindow.Locs.WindowTitle);
                Assert.Equal("Core category", PluginCategoryManager.Locs.Category_All);
                Assert.Equal("Plugin title", Localization.Localize("InstallerHeader", "Fallback"));

                Loc.Setup("""{"InstallerHeader":{"message":"Changed title"},"InstallerCategoryAll":{"message":"Changed category"}}""", coreAssembly);
                Assert.Equal("Changed title", PluginInstallerWindow.Locs.WindowTitle);
                Assert.Equal("Changed category", PluginCategoryManager.Locs.Category_All);
                Assert.Equal("Plugin title", Localization.Localize("InstallerHeader", "Fallback"));

                Loc.SetupWithFallbacks(coreAssembly);
                Assert.Equal("Plugin Installer", PluginInstallerWindow.Locs.WindowTitle);
                Assert.Equal("All", PluginCategoryManager.Locs.Category_All);
            }
            finally
            {
                Loc.SetupWithFallbacks(coreAssembly);
                Loc.SetupWithFallbacks(pluginAssembly);
            }
        }

        [Fact]
        public void CoreCategoryOrderKeepsItsLanguageAfterWarmup()
        {
            var assembly = typeof(Localization).Assembly;
            var manager = new PluginCategoryManager();
            var manifest = new RemotePluginManifest { Name = "Fixture", InternalName = "Fixture", CategoryTags = ["ui", "utility"] };
            try
            {
                Loc.Setup("""{"InstallerCategoryUI":{"message":"Zed"},"InstallerCategoryUtility":{"message":"Aye"}}""", assembly);
                for (var i = 0; i < 20000; i++)
                    manager.BuildCategories([manifest]);

                Assert.Equal(
                    new[] { PluginCategoryManager.CategoryKind.All, PluginCategoryManager.CategoryKind.AvailableForTesting, PluginCategoryManager.CategoryKind.Utility, PluginCategoryManager.CategoryKind.Ui, PluginCategoryManager.CategoryKind.Hidden },
                    manager.CurrentGroup.Categories);
            }
            finally
            {
                Loc.SetupWithFallbacks(assembly);
            }
        }
    }
}
