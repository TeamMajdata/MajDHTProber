using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MajDHTNetwork;
using Microsoft.Extensions.Logging;

#nullable enable

namespace MajDHTProber.Services
{
    /// <summary>Describes an identity search that started or finished.</summary>
    public sealed class IdentityGenerationEventArgs : EventArgs
    {
        /// <summary>Creates the event data.</summary>
        /// <param name="name">The identity name.</param>
        /// <param name="workBits">The required identity work.</param>
        /// <param name="elapsed">The search duration once finished, or null when the search started.</param>
        public IdentityGenerationEventArgs(string name, int workBits, TimeSpan? elapsed)
        {
            Name = name;
            WorkBits = workBits;
            Elapsed = elapsed;
        }

        /// <summary>Gets the identity name.</summary>
        public string Name { get; }

        /// <summary>Gets the required identity work.</summary>
        public int WorkBits { get; }

        /// <summary>Gets the search duration once finished, or null when the search started.</summary>
        public TimeSpan? Elapsed { get; }
    }

    /// <summary>
    /// Provides named node identities that reach a required identity work, generating them on a background thread and
    /// optionally storing their secret seeds on disk so node identifiers survive restarts.
    /// </summary>
    /// <remarks>
    /// Generating an identity with the default 12 bits of identity work can take seconds to tens of seconds, so identities
    /// are cached per name and concurrent requests share one search. Seeds are secrets: they are never logged.
    /// </remarks>
    public sealed class IdentityVault : IDisposable
    {
        /// <summary>The length of an identity seed in bytes.</summary>
        private const int SeedLength = 32;

        /// <summary>The directory holding seed files, or null for memory-only identities.</summary>
        private readonly string? _directory;
        /// <summary>The diagnostics logger.</summary>
        private readonly ILogger _logger;
        /// <summary>Protects the identity cache.</summary>
        private readonly Lock _gate = new Lock();
        /// <summary>The loaded or pending identities keyed by name.</summary>
        private readonly Dictionary<string, Task<DHTIdentity>> _identities = new Dictionary<string, Task<DHTIdentity>>(StringComparer.Ordinal);
        /// <summary>Cancels pending searches when the vault is disposed.</summary>
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();

        /// <summary>Creates a vault.</summary>
        /// <param name="directory">The directory for seed files, or null to keep every identity in memory only.</param>
        /// <param name="logger">The diagnostics logger.</param>
        public IdentityVault(string? directory, ILogger logger)
        {
            _directory = directory;
            _logger = logger;
        }

        /// <summary>Raised on a background thread when a new identity search starts.</summary>
        public event EventHandler<IdentityGenerationEventArgs>? GenerationStarted;

        /// <summary>Raised on a background thread when an identity search finishes successfully.</summary>
        public event EventHandler<IdentityGenerationEventArgs>? GenerationFinished;

        /// <summary>Gets an identity by name, loading or generating it when needed.</summary>
        /// <param name="name">The identity name: lowercase letters, digits and hyphens.</param>
        /// <param name="workBits">The minimum identity work, from 0 to 20.</param>
        /// <param name="persist">Whether the seed is read from and written to disk.</param>
        /// <param name="cancellationToken">Cancels waiting; a shared search continues for other callers.</param>
        /// <returns>An identity reaching at least <paramref name="workBits"/>.</returns>
        /// <exception cref="ArgumentException">The name is invalid.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The work is outside 0 to 20.</exception>
        /// <exception cref="OperationCanceledException">Waiting was cancelled or the vault was disposed.</exception>
        /// <exception cref="IOException">A seed file could not be written.</exception>
        public async Task<DHTIdentity> GetOrCreateAsync(string name, int workBits, bool persist, CancellationToken cancellationToken = default)
        {
            StorageName.Validate(name);
            if (workBits < 0 || workBits > 20)
            {
                throw new ArgumentOutOfRangeException(nameof(workBits));
            }
            while (true)
            {
                Task<DHTIdentity> pending;
                lock (_gate)
                {
                    if (!_identities.TryGetValue(name, out var existing) || existing.IsFaulted || existing.IsCanceled
                        || (existing.IsCompletedSuccessfully && existing.Result.IdentityWork < workBits))
                    {
                        existing = Task.Run(() => LoadOrCreate(name, workBits, persist, _lifetime.Token), _lifetime.Token);
                        _identities[name] = existing;
                    }
                    pending = existing;
                }
                var identity = await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (identity.IdentityWork >= workBits)
                {
                    return identity;
                }
                // A concurrent request created the identity with less work; the next iteration replaces it.
            }
        }

        /// <summary>Returns the node identifier of a cached identity without loading or generating it.</summary>
        /// <param name="name">The identity name.</param>
        /// <returns>The node identifier, or null when the identity is not loaded yet.</returns>
        public string? TryGetCachedNodeId(string name)
        {
            lock (_gate)
            {
                return _identities.TryGetValue(name, out var task) && task.IsCompletedSuccessfully ? task.Result.NodeId : null;
            }
        }

        /// <summary>Forgets a cached identity and deletes its stored seed, so the next request generates a new identity.</summary>
        /// <param name="name">The identity name.</param>
        /// <returns>Whether a cached identity or a stored seed existed.</returns>
        /// <exception cref="ArgumentException">The name is invalid.</exception>
        /// <exception cref="IOException">The seed file could not be deleted.</exception>
        public bool Delete(string name)
        {
            StorageName.Validate(name);
            bool removed;
            lock (_gate)
            {
                removed = _identities.Remove(name);
            }
            var path = SeedPath(name);
            if (path != null && File.Exists(path))
            {
                File.Delete(path);
                removed = true;
            }
            if (removed)
            {
                _logger.LogInformation("已删除身份 {Name}，下次启动将生成新身份。", name);
            }
            return removed;
        }

        /// <summary>Cancels pending identity searches.</summary>
        public void Dispose()
        {
            _lifetime.Cancel();
            _lifetime.Dispose();
        }

        /// <summary>Loads a stored identity that reaches the work, or generates and optionally stores a new one.</summary>
        /// <param name="name">The identity name.</param>
        /// <param name="workBits">The minimum identity work.</param>
        /// <param name="persist">Whether to read and write the seed file.</param>
        /// <param name="cancellationToken">Cancels the search.</param>
        /// <returns>The identity.</returns>
        /// <exception cref="OperationCanceledException">The search was cancelled.</exception>
        /// <exception cref="IOException">The seed file could not be written.</exception>
        private DHTIdentity LoadOrCreate(string name, int workBits, bool persist, CancellationToken cancellationToken)
        {
            var path = persist ? SeedPath(name) : null;
            if (path != null && File.Exists(path))
            {
                var stored = TryLoad(path);
                if (stored != null && stored.IdentityWork >= workBits)
                {
                    _logger.LogDebug("已加载已保存的身份 {Name}（工作量 {Work} 位）。", name, stored.IdentityWork);
                    return stored;
                }
                _logger.LogWarning("已保存的身份 {Name} 无效或工作量低于 {Bits} 位，将重新生成。", name, workBits);
            }
            Raise(GenerationStarted, new IdentityGenerationEventArgs(name, workBits, null));
            _logger.LogInformation("正在生成身份 {Name}（工作量 {Bits} 位），这可能需要数秒到数十秒。", name, workBits);
            var started = DateTimeOffset.UtcNow;
            var identity = DHTIdentity.Create(workBits, cancellationToken);
            var elapsed = DateTimeOffset.UtcNow - started;
            _logger.LogInformation("身份 {Name} 已生成，耗时 {Seconds:F1} 秒。", name, elapsed.TotalSeconds);
            if (path != null)
            {
                Store(path, identity);
            }
            Raise(GenerationFinished, new IdentityGenerationEventArgs(name, workBits, elapsed));
            return identity;
        }

        /// <summary>Notifies listeners while isolating listener failures from the identity search.</summary>
        /// <param name="handler">The event handlers.</param>
        /// <param name="args">The event data.</param>
        private void Raise(EventHandler<IdentityGenerationEventArgs>? handler, IdentityGenerationEventArgs args)
        {
            try
            {
                handler?.Invoke(this, args);
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "身份生成事件的监听器出错。");
            }
        }

        /// <summary>Reads a seed file, returning null when its content is not a seed.</summary>
        /// <param name="path">The seed file path.</param>
        /// <returns>The restored identity, or null.</returns>
        private DHTIdentity? TryLoad(string path)
        {
            byte[]? seed = null;
            try
            {
                seed = File.ReadAllBytes(path);
                return seed.Length == SeedLength ? new DHTIdentity(seed) : null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "身份文件无法读取。");
                return null;
            }
            finally
            {
                if (seed != null)
                {
                    CryptographicOperations.ZeroMemory(seed);
                }
            }
        }

        /// <summary>Writes a seed file, replacing an older file only after the new seed was written.</summary>
        /// <param name="path">The seed file path.</param>
        /// <param name="identity">The identity whose seed is stored.</param>
        /// <exception cref="IOException">The file could not be written.</exception>
        private static void Store(string path, DHTIdentity identity)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var seed = identity.ExportSeed();
            try
            {
                var temporary = path + ".tmp";
                File.WriteAllBytes(temporary, seed);
                File.Move(temporary, path, true);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(seed);
            }
        }

        /// <summary>Gets the seed file path of an identity.</summary>
        /// <param name="name">The identity name.</param>
        /// <returns>The path, or null for a memory-only vault.</returns>
        private string? SeedPath(string name)
        {
            return _directory == null ? null : Path.Combine(_directory, name + ".seed");
        }
    }
}
