using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using MajDHTNetwork;

#nullable enable

namespace MajDHTProber.Services
{
    /// <summary>
    /// Parses what the user enters to identify content: a canonical key (<c>publisher:name</c>) or a record code, which is
    /// the Base64 form of a signed record's bytes and lets a record be passed between provers without resolving it.
    /// </summary>
    public static class RecordInput
    {
        /// <summary>Formats a record as a record code.</summary>
        /// <param name="record">The signed record.</param>
        /// <returns>The Base64 text of the record bytes.</returns>
        public static string ToCode(DHTRecord record)
        {
            return Convert.ToBase64String(record.ToBytes());
        }

        /// <summary>Parses a record code.</summary>
        /// <param name="text">The Base64 text.</param>
        /// <param name="record">The verified record when successful.</param>
        /// <returns>Whether the text is a valid signed record.</returns>
        public static bool TryParseCode(string? text, [NotNullWhen(true)] out DHTRecord? record)
        {
            record = null;
            var trimmed = text?.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                return false;
            }
            var buffer = new byte[(trimmed.Length * 3 / 4) + 3];
            if (!Convert.TryFromBase64String(trimmed, buffer, out var written))
            {
                return false;
            }
            try
            {
                record = DHTRecord.Parse(buffer.AsSpan(0, written));
                return true;
            }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentException or FormatException)
            {
                return false;
            }
        }

        /// <summary>Parses a canonical key.</summary>
        /// <param name="text">The <c>publisher:name</c> text.</param>
        /// <param name="key">The key when successful.</param>
        /// <returns>Whether the text is a valid key.</returns>
        public static bool TryParseKey(string? text, out DHTKey key)
        {
            key = default;
            var trimmed = text?.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.IndexOf(':') != 64)
            {
                return false;
            }
            try
            {
                key = DHTKey.Parse(trimmed);
                return key.IsValid;
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidDataException)
            {
                return false;
            }
        }

        /// <summary>Parses a key or a record code.</summary>
        /// <param name="text">The user input.</param>
        /// <param name="key">The key, set for both kinds of input when successful.</param>
        /// <param name="record">The record when the input is a record code, otherwise null.</param>
        /// <param name="error">A user-facing reason when parsing fails; empty on success.</param>
        /// <returns>Whether the input identifies content.</returns>
        public static bool TryParse(string? text, out DHTKey key, out DHTRecord? record, out string error)
        {
            record = null;
            error = string.Empty;
            if (TryParseKey(text, out key))
            {
                return true;
            }
            if (TryParseCode(text, out record))
            {
                key = record.Key;
                return true;
            }
            error = string.IsNullOrWhiteSpace(text)
                ? "请输入键（发布者ID:名称）或记录码。"
                : "无法识别：键的格式应为 64 位十六进制发布者 ID + 冒号 + 名称，记录码应为 Base64。";
            return false;
        }
    }
}
