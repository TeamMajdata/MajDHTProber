using System;
using System.Security.Cryptography;
using System.Text;

#nullable enable

namespace MajDHTProber.Infrastructure
{
    /// <summary>Builds human-readable previews of downloaded content.</summary>
    public static class ContentPreview
    {
        /// <summary>A strict UTF-8 decoder that rejects invalid sequences.</summary>
        private static readonly UTF8Encoding s_strictUtf8 = new UTF8Encoding(false, true);

        /// <summary>Decodes content as text when it is valid UTF-8 without binary control characters.</summary>
        /// <param name="content">The content.</param>
        /// <param name="maximumBytes">The maximum number of leading bytes decoded.</param>
        /// <param name="text">The decoded text when successful.</param>
        /// <returns>Whether the content looks like text.</returns>
        public static bool TryDecodeText(ReadOnlySpan<byte> content, int maximumBytes, out string text)
        {
            text = string.Empty;
            var slice = content.Slice(0, Math.Min(content.Length, maximumBytes));
            // A preview may cut a multi-byte sequence; drop an incomplete trailing sequence before strict decoding.
            if (slice.Length < content.Length)
            {
                var trim = 0;
                while (trim < 3 && trim < slice.Length && (slice[slice.Length - 1 - trim] & 0xC0) == 0x80)
                {
                    trim++;
                }
                if (trim < slice.Length && (slice[slice.Length - 1 - trim] & 0xC0) == 0xC0)
                {
                    slice = slice.Slice(0, slice.Length - 1 - trim);
                }
            }
            try
            {
                text = s_strictUtf8.GetString(slice);
            }
            catch (DecoderFallbackException)
            {
                return false;
            }
            foreach (var character in text)
            {
                if (char.IsControl(character) && character != '\r' && character != '\n' && character != '\t')
                {
                    text = string.Empty;
                    return false;
                }
            }
            return true;
        }

        /// <summary>Computes the lowercase hexadecimal SHA-256 digest of content.</summary>
        /// <param name="content">The content.</param>
        /// <returns>The digest text.</returns>
        public static string Sha256(ReadOnlySpan<byte> content)
        {
            return Convert.ToHexStringLower(SHA256.HashData(content));
        }
    }
}
