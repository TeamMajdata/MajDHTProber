using System;
using System.Globalization;
using MajDHTProber.Infrastructure;
using MajDHTProber.Models;

#nullable enable

namespace MajDHTProber.ViewModels
{
    /// <summary>One remembered record of the active session.</summary>
    public sealed class RecordItemViewModel : TonedViewModel
    {
        /// <summary>Creates a row.</summary>
        /// <param name="entry">The remembered record.</param>
        /// <param name="localNodeId">The active node identifier, used to recognize own publications.</param>
        /// <param name="now">The current time.</param>
        public RecordItemViewModel(LocalRecordEntry entry, string? localNodeId, DateTimeOffset now)
        {
            Entry = entry;
            var record = entry.Record;
            KeyText = entry.KeyText;
            Name = record.Key.Name;
            PublisherText = Display.ShortId(record.Key.PublisherId, 8, 4);
            IsOwn = record.Key.PublisherId == localNodeId;
            OriginText = Display.Origin(entry.Origin);
            HashText = record.IsWithdrawn ? Display.None : Display.ShortId(record.FileHash, 8, 6);
            SizeText = record.IsWithdrawn ? Display.None : Display.Bytes(record.Length);
            BlocksText = record.IsWithdrawn ? Display.None : record.BlockCount.ToString(CultureInfo.InvariantCulture) + " × " + Display.Bytes(record.BlockSize);
            RevisionText = record.Revision.ToString(CultureInfo.InvariantCulture);
            ExpiresText = Display.Future(record.ExpiresAt, now);
            ReplicasText = entry.Replicas.HasValue ? entry.Replicas.Value.ToString(CultureInfo.InvariantCulture) : Display.None;
            if (record.IsWithdrawn)
            {
                StatusText = "已撤回";
                Tone = StatusTone.Danger;
            }
            else if (record.IsExpired)
            {
                StatusText = "已过期";
                Tone = StatusTone.Warning;
            }
            else if (entry.ProvidingStopped)
            {
                StatusText = "已停止提供";
                Tone = StatusTone.Neutral;
            }
            else if (entry.Origin == RecordOrigin.Resolved)
            {
                StatusText = "仅元数据";
                Tone = StatusTone.Info;
            }
            else
            {
                StatusText = "本地可提供";
                Tone = StatusTone.Success;
            }
        }

        /// <summary>Gets the remembered record.</summary>
        public LocalRecordEntry Entry { get; }

        /// <summary>Gets the canonical key text.</summary>
        public string KeyText { get; }

        /// <summary>Gets the key name.</summary>
        public string Name { get; }

        /// <summary>Gets the shortened publisher identifier.</summary>
        public string PublisherText { get; }

        /// <summary>Gets whether the active node published the key.</summary>
        public bool IsOwn { get; }

        /// <summary>Gets the origin text.</summary>
        public string OriginText { get; }

        /// <summary>Gets the shortened complete-file hash.</summary>
        public string HashText { get; }

        /// <summary>Gets the file size.</summary>
        public string SizeText { get; }

        /// <summary>Gets the block geometry.</summary>
        public string BlocksText { get; }

        /// <summary>Gets the revision.</summary>
        public string RevisionText { get; }

        /// <summary>Gets the lease expiry relative to now.</summary>
        public string ExpiresText { get; }

        /// <summary>Gets the replica count of the last announcement.</summary>
        public string ReplicasText { get; }

        /// <summary>Gets the serving status.</summary>
        public string StatusText { get; }
    }
}
