using System;
using MajDHTNetwork;

#nullable enable

namespace MajDHTProber.Models
{
    /// <summary>Describes how a hosted node came to hold a signed record.</summary>
    public enum RecordOrigin
    {
        /// <summary>The node published the content itself.</summary>
        Published,
        /// <summary>The node published a second name for already published content.</summary>
        Alias,
        /// <summary>The node imported a local copy of another publisher's content.</summary>
        Imported,
        /// <summary>The node downloaded and verified the content.</summary>
        Downloaded,
        /// <summary>The node resolved the record without downloading its content.</summary>
        Resolved,
        /// <summary>The node signed or received a withdrawal of the key.</summary>
        Withdrawal,
    }

    /// <summary>A signed record remembered by a hosted node session, together with how it was obtained.</summary>
    public sealed record LocalRecordEntry
    {
        /// <summary>Gets the signed record.</summary>
        public required DHTRecord Record { get; init; }

        /// <summary>Gets how the record was obtained.</summary>
        public required RecordOrigin Origin { get; init; }

        /// <summary>Gets when the record was obtained.</summary>
        public required DateTimeOffset At { get; init; }

        /// <summary>Gets the number of remote replicas reported by the last announcement, if known.</summary>
        public int? Replicas { get; init; }

        /// <summary>Gets whether the session stopped serving the content locally.</summary>
        public bool ProvidingStopped { get; init; }

        /// <summary>Gets the canonical key text, used as the identity of the entry.</summary>
        public string KeyText => Record.Key.ToString();
    }
}
