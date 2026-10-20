using System;
using System.IO;
using Microsoft.Extensions.Logging;

#nullable enable

namespace MajDHTProber.Services
{
    /// <summary>
    /// Stores the routing table snapshots that <see cref="MajDHTNetwork.MajDHTNode.ExportRoutingTable"/> produces, one file
    /// per identity name, so a restarted node can rejoin through the nodes it already knew.
    /// </summary>
    /// <remarks>
    /// A snapshot names other nodes' identifiers and endpoints only; it holds no key, signature or content. The library
    /// never writes it itself, and a restored snapshot must come from this trusted store, because whoever answers at a saved
    /// endpoint under the saved identity becomes a contact.
    /// </remarks>
    public sealed class RoutingSnapshotStore
    {
        /// <summary>The file extension of a snapshot.</summary>
        public const string Extension = ".routing";

        /// <summary>The directory holding snapshot files, or null for a store that keeps nothing.</summary>
        private readonly string? _directory;
        /// <summary>The diagnostics logger.</summary>
        private readonly ILogger _logger;

        /// <summary>Creates a store.</summary>
        /// <param name="directory">The directory for snapshot files, or null to persist nothing.</param>
        /// <param name="logger">The diagnostics logger.</param>
        public RoutingSnapshotStore(string? directory, ILogger logger)
        {
            _directory = directory;
            _logger = logger;
        }

        /// <summary>Gets whether snapshots are written to disk.</summary>
        public bool IsPersistent => _directory != null;

        /// <summary>Reads the stored snapshot of an identity.</summary>
        /// <param name="name">The identity name: lowercase letters, digits and hyphens.</param>
        /// <returns>The snapshot bytes, or null when none is stored, the store persists nothing or the file is unreadable.</returns>
        /// <exception cref="ArgumentException">The name is invalid.</exception>
        public byte[]? TryLoad(string name)
        {
            var path = PathOf(name);
            if (path == null || !File.Exists(path))
            {
                return null;
            }
            try
            {
                return File.ReadAllBytes(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "路由表文件 {FileName} 无法读取。", Path.GetFileName(path));
                return null;
            }
        }

        /// <summary>Writes the snapshot of an identity, replacing an older file only after the new one was written completely.</summary>
        /// <param name="name">The identity name.</param>
        /// <param name="snapshot">The exported snapshot.</param>
        /// <exception cref="ArgumentException">The name is invalid.</exception>
        /// <exception cref="IOException">The file could not be written.</exception>
        /// <exception cref="UnauthorizedAccessException">The directory is not writable.</exception>
        public void Save(string name, byte[] snapshot)
        {
            var path = PathOf(name);
            if (path == null)
            {
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllBytes(temporary, snapshot);
            File.Move(temporary, path, true);
            _logger.LogDebug("已写入路由表文件 {FileName}（{Bytes} 字节）。", Path.GetFileName(path), snapshot.Length);
        }

        /// <summary>Deletes the stored snapshot of an identity.</summary>
        /// <param name="name">The identity name.</param>
        /// <returns>Whether a snapshot file existed.</returns>
        /// <exception cref="ArgumentException">The name is invalid.</exception>
        /// <exception cref="IOException">The file could not be deleted.</exception>
        public bool Delete(string name)
        {
            var path = PathOf(name);
            if (path == null || !File.Exists(path))
            {
                return false;
            }
            File.Delete(path);
            _logger.LogInformation("已删除路由表文件 {FileName}。", Path.GetFileName(path));
            return true;
        }

        /// <summary>Gets the snapshot file path of an identity.</summary>
        /// <param name="name">The identity name.</param>
        /// <returns>The path, or null for a store that persists nothing.</returns>
        /// <exception cref="ArgumentException">The name is invalid.</exception>
        private string? PathOf(string name)
        {
            StorageName.Validate(name);
            return _directory == null ? null : Path.Combine(_directory, name + Extension);
        }
    }
}
