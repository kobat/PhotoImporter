using System;
using PhotoImporter.App;
using Xunit;

namespace PhotoImporter.Core.Tests
{
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class JapaneseLocalizationCollection : ICollectionFixture<JapaneseLocalizationFixture>
    {
        public const string Name = "Japanese localization";
    }

    public sealed class JapaneseLocalizationFixture : IDisposable
    {
        private readonly string _previousPreference;

        public JapaneseLocalizationFixture()
        {
            _previousPreference = AppLocalization.Preference;
            AppLocalization.Configure(AppLocalization.Japanese);
        }

        public void Dispose()
        {
            AppLocalization.Configure(_previousPreference);
        }
    }
}
