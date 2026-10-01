using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace OneText.Tests
{
    /// <summary>
    /// The Addressables source, in a project that has Addressables: it is the
    /// one the settings get when they ask for it — in edit mode as well as in
    /// play — and an address that names nothing comes back as nothing, with
    /// the handle given back rather than left pinning a bundle.
    ///
    /// Compiled only where <c>com.unity.addressables</c> is installed, like the
    /// assembly it tests. Loading a real addressable font needs a group and a
    /// build of the catalog, which is a project's setup and not this package's.
    /// </summary>
    public class AddressablesFontSourceTests
    {
        private OneTextSettings _previous;
        private OneTextSettings _settings;

        [SetUp]
        public void SetUp()
        {
            _previous = OneTextSettings.Instance;
            _settings = ScriptableObject.CreateInstance<OneTextSettings>();
            OneTextSettings.Instance = _settings;
            FontResidency.Source = null;
        }

        [TearDown]
        public void TearDown()
        {
            FontResidency.Source = null;
            OneTextSettings.Instance = _previous;
            OneTextSettings.Invalidate();
            Object.DestroyImmediate(_settings);
        }

        [Test]
        public void SettingsThatSayAddressables_GetTheAddressablesSource_InEditMode()
        {
            Assume.That(Application.isPlaying, Is.False);
            _settings.FontSource = OneFontSourceKind.Addressables;
            Assert.IsInstanceOf<AddressablesFontSource>(FontResidency.Source,
                "edit mode fell back to Resources: the source registers only at play start");
        }

        [Test]
        public void SettingsThatSayResources_KeepTheResourcesSource()
        {
            _settings.FontSource = OneFontSourceKind.Resources;
            Assert.IsInstanceOf<ResourcesFontSource>(FontResidency.Source);
        }

        [Test]
        public void AnAddressThatNamesNothing_LoadsNothing_AndReleasingItIsHarmless()
        {
            // Addressables reports an unknown key itself, as an error; that
            // report is its business, the null coming back is this one's.
            LogAssert.ignoreFailingMessages = true;
            var source = new AddressablesFontSource();
            Assert.IsNull(source.LoadNow("OneText/NoSuchFont"));
            Assert.DoesNotThrow(() => source.Release("OneText/NoSuchFont", null));
        }
    }
}
