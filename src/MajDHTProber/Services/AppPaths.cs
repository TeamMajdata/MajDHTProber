using System;
using System.IO;

#nullable enable

namespace MajDHTProber.Services
{
    /// <summary>Locates the per-user directory that holds the prober settings and stored node identities.</summary>
    public sealed class AppPaths
    {
        /// <summary>The environment variable that overrides the data directory, for portable or isolated runs.</summary>
        public const string HomeVariable = "MAJDHTPROBER_HOME";

        /// <summary>Creates paths below an explicit root directory.</summary>
        /// <param name="root">The data directory.</param>
        /// <exception cref="ArgumentException">The root is empty.</exception>
        public AppPaths(string root)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                throw new ArgumentException("A data directory is required.", nameof(root));
            }
            Root = root;
        }

        /// <summary>Gets the data directory.</summary>
        public string Root { get; }

        /// <summary>Gets the settings file path.</summary>
        public string SettingsFile => Path.Combine(Root, "settings.json");

        /// <summary>Gets the directory holding stored identity seeds.</summary>
        public string IdentityDirectory => Path.Combine(Root, "identities");

        /// <summary>Creates the default paths: the override variable when set, otherwise the roaming application data folder.</summary>
        /// <returns>The default paths.</returns>
        public static AppPaths CreateDefault()
        {
            var overridden = Environment.GetEnvironmentVariable(HomeVariable);
            if (!string.IsNullOrWhiteSpace(overridden))
            {
                return new AppPaths(overridden);
            }
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return new AppPaths(Path.Combine(appData, "MajDHTProber"));
        }
    }
}
