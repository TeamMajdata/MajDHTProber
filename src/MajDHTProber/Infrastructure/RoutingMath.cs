using System;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;

#nullable enable

namespace MajDHTProber.Infrastructure
{
    /// <summary>XOR-distance helpers for 256-bit routing identifiers.</summary>
    public static class RoutingMath
    {
        /// <summary>The length of a routing identifier in hexadecimal characters.</summary>
        public const int IdLength = 64;

        /// <summary>Checks whether text is a lowercase 256-bit hexadecimal identifier.</summary>
        /// <param name="text">The text.</param>
        /// <returns>Whether the text has 64 lowercase hexadecimal digits.</returns>
        public static bool IsRoutingId(string? text)
        {
            if (text == null || text.Length != IdLength)
            {
                return false;
            }
            foreach (var character in text)
            {
                if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f')))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>Creates a random routing identifier, for example to explore an arbitrary region of the key space.</summary>
        /// <returns>A random lowercase identifier.</returns>
        public static string RandomId()
        {
            return Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        }

        /// <summary>Counts the leading bits two identifiers share, which is 256 minus the bit length of their XOR distance.</summary>
        /// <param name="left">The first identifier.</param>
        /// <param name="right">The second identifier.</param>
        /// <returns>The shared prefix length from 0 to 256.</returns>
        /// <exception cref="FormatException">An identifier is not 64 hexadecimal digits.</exception>
        public static int CommonPrefixBits(string left, string right)
        {
            if (!IsRoutingId(left) || !IsRoutingId(right))
            {
                throw new FormatException("Routing identifiers must be 64 lowercase hexadecimal digits.");
            }
            for (var index = 0; index < IdLength; index += 16)
            {
                var a = ulong.Parse(left.AsSpan(index, 16), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var b = ulong.Parse(right.AsSpan(index, 16), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var distance = a ^ b;
                if (distance != 0)
                {
                    return (index * 4) + BitOperations.LeadingZeroCount(distance);
                }
            }
            return 256;
        }
    }
}
