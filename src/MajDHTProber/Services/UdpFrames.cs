using System;
using System.Security.Cryptography;

#nullable enable

namespace MajDHTProber.Services
{
    /// <summary>
    /// Encodes and decodes the UDP transport frames of the protocol's cookie handshake, as specified in the UDP section
    /// of the protocol document: a client broadcasts or sends a Hello frame carrying the digest of its sealed request,
    /// the node answers with a Challenge carrying a source-bound cookie, the client sends the cookie together with the
    /// sealed request, and the node answers with a Response correlated by the same digest.
    /// </summary>
    /// <remarks>
    /// The library keeps its own implementation of this handshake inside <c>SocketTransport</c>, which cannot broadcast;
    /// the LAN scan therefore speaks the documented frame format on a socket of its own.
    /// </remarks>
    internal static class UdpFrames
    {
        /// <summary>The length of the fixed transport header: the magic bytes, the version and the kind.</summary>
        public const int HeaderLength = 5;
        /// <summary>The length of a request digest and of a cookie.</summary>
        public const int DigestLength = 32;
        /// <summary>The exact length of a Hello and of a Challenge frame.</summary>
        public const int HelloLength = HeaderLength + 2 * DigestLength;
        /// <summary>The frame kind of the client Hello.</summary>
        public const byte HelloKind = 0;
        /// <summary>The frame kind of the node Challenge.</summary>
        public const byte ChallengeKind = 1;
        /// <summary>The frame kind of the cookie-bearing Request.</summary>
        public const byte RequestKind = 2;
        /// <summary>The frame kind of a legacy, uncorrelated Response.</summary>
        public const byte LegacyResponseKind = 3;
        /// <summary>The frame kind of a Response correlated by the request digest.</summary>
        public const byte ResponseKind = 4;
        /// <summary>The absolute datagram limit of the transport.</summary>
        public const int MaxDatagram = 60037;

        /// <summary>Creates the Hello frame announcing a sealed request.</summary>
        /// <param name="digest">The 32-byte SHA-256 digest of the sealed request.</param>
        /// <returns>The 69-byte frame: header, digest and zero padding.</returns>
        /// <exception cref="ArgumentException">The digest is not 32 bytes long.</exception>
        public static byte[] Hello(ReadOnlySpan<byte> digest)
        {
            RequireDigest(digest);
            var frame = new byte[HelloLength];
            WriteHeader(frame, HelloKind);
            digest.CopyTo(frame.AsSpan(HeaderLength, DigestLength));
            return frame;
        }

        /// <summary>Reads the cookie of a Challenge that answers the Hello of a sealed request.</summary>
        /// <param name="frame">The received datagram.</param>
        /// <param name="digest">The digest the Challenge must echo.</param>
        /// <param name="cookie">Receives the 32-byte cookie.</param>
        /// <returns>Whether the datagram is a Challenge for that request.</returns>
        public static bool TryReadChallenge(ReadOnlySpan<byte> frame, ReadOnlySpan<byte> digest, out byte[] cookie)
        {
            cookie = Array.Empty<byte>();
            if (!IsFrame(frame, ChallengeKind) || frame.Length != HelloLength
                || !CryptographicOperations.FixedTimeEquals(frame.Slice(HeaderLength + DigestLength, DigestLength), digest))
            {
                return false;
            }
            cookie = frame.Slice(HeaderLength, DigestLength).ToArray();
            return true;
        }

        /// <summary>Creates the Request frame that carries a node's cookie and the sealed request.</summary>
        /// <param name="cookie">The 32-byte cookie from the node's Challenge.</param>
        /// <param name="envelope">The sealed request whose digest the Hello announced.</param>
        /// <returns>The frame: header, cookie and envelope.</returns>
        /// <exception cref="ArgumentException">The cookie is not 32 bytes long or the envelope is empty.</exception>
        public static byte[] Request(ReadOnlySpan<byte> cookie, ReadOnlySpan<byte> envelope)
        {
            RequireDigest(cookie);
            if (envelope.Length == 0 || envelope.Length + HeaderLength + DigestLength > MaxDatagram)
            {
                throw new ArgumentException("The sealed request is empty or too large for one datagram.", nameof(envelope));
            }
            var frame = new byte[HeaderLength + DigestLength + envelope.Length];
            WriteHeader(frame, RequestKind);
            cookie.CopyTo(frame.AsSpan(HeaderLength, DigestLength));
            envelope.CopyTo(frame.AsSpan(HeaderLength + DigestLength));
            return frame;
        }

        /// <summary>Extracts the sealed response from a Response frame that answers a sealed request.</summary>
        /// <param name="frame">The received datagram.</param>
        /// <param name="digest">The digest of the sealed request.</param>
        /// <param name="envelope">Receives the sealed response.</param>
        /// <returns>
        /// Whether the datagram is a Response correlated with that digest, or a legacy Response, which carries no digest.
        /// </returns>
        public static bool TryReadResponse(ReadOnlyMemory<byte> frame, ReadOnlySpan<byte> digest, out ReadOnlyMemory<byte> envelope)
        {
            envelope = ReadOnlyMemory<byte>.Empty;
            var bytes = frame.Span;
            if (IsFrame(bytes, ResponseKind) && bytes.Length > HeaderLength + DigestLength
                && CryptographicOperations.FixedTimeEquals(bytes.Slice(HeaderLength, DigestLength), digest))
            {
                envelope = frame.Slice(HeaderLength + DigestLength);
                return true;
            }
            if (IsFrame(bytes, LegacyResponseKind) && bytes.Length > HeaderLength)
            {
                envelope = frame.Slice(HeaderLength);
                return true;
            }
            return false;
        }

        /// <summary>Checks the magic bytes, the version, the kind and the absolute size of a frame.</summary>
        /// <param name="frame">The received datagram.</param>
        /// <param name="kind">The expected kind.</param>
        /// <returns>Whether the header matches.</returns>
        public static bool IsFrame(ReadOnlySpan<byte> frame, byte kind)
        {
            return frame.Length >= HeaderLength && frame.Length <= MaxDatagram
                && frame[0] == 0x4d && frame[1] == 0x44 && frame[2] == 0x55 && frame[3] == 1 && frame[4] == kind;
        }

        /// <summary>Writes the transport header.</summary>
        /// <param name="frame">The frame, at least five bytes long.</param>
        /// <param name="kind">The frame kind.</param>
        private static void WriteHeader(Span<byte> frame, byte kind)
        {
            frame[0] = 0x4d;
            frame[1] = 0x44;
            frame[2] = 0x55;
            frame[3] = 1;
            frame[4] = kind;
        }

        /// <summary>Rejects a digest or cookie of the wrong length.</summary>
        /// <param name="value">The digest or cookie.</param>
        /// <exception cref="ArgumentException">The value is not 32 bytes long.</exception>
        private static void RequireDigest(ReadOnlySpan<byte> value)
        {
            if (value.Length != DigestLength)
            {
                throw new ArgumentException("A digest or cookie must contain 32 bytes.");
            }
        }
    }
}
