using System;

#nullable enable

namespace MajDHTProber.Services
{
    /// <summary>Validates the names under which per-identity files, such as seeds and routing snapshots, are stored.</summary>
    internal static class StorageName
    {
        /// <summary>The maximum name length.</summary>
        private const int MaxLength = 64;

        /// <summary>Restricts names to characters that are safe in file names.</summary>
        /// <param name="name">The storage name.</param>
        /// <exception cref="ArgumentException">The name is empty, too long or contains other characters than lowercase letters, digits and hyphens.</exception>
        internal static void Validate(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > MaxLength)
            {
                throw new ArgumentException("A storage name of 1 to 64 characters is required.", nameof(name));
            }
            foreach (var character in name)
            {
                if (!((character >= 'a' && character <= 'z') || (character >= '0' && character <= '9') || character == '-'))
                {
                    throw new ArgumentException("Storage names may contain lowercase letters, digits and hyphens only.", nameof(name));
                }
            }
        }
    }
}
