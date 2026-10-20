using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace MajDHTProber.Infrastructure
{
    /// <summary>
    /// A write-only stream that forwards writes to another stream and reports the total number of written bytes, so
    /// downloads through APIs without progress callbacks can still show progress.
    /// </summary>
    public sealed class ProgressStream : Stream
    {
        /// <summary>The destination stream.</summary>
        private readonly Stream _inner;
        /// <summary>Receives the running total after every write.</summary>
        private readonly IProgress<long>? _progress;
        /// <summary>Whether disposing this stream leaves the destination open.</summary>
        private readonly bool _leaveOpen;
        /// <summary>The running total of written bytes.</summary>
        private long _written;

        /// <summary>Wraps a writable stream.</summary>
        /// <param name="inner">The destination stream.</param>
        /// <param name="progress">Receives the running total after every write, or null.</param>
        /// <param name="leaveOpen">Whether disposing this stream leaves the destination open.</param>
        /// <exception cref="ArgumentException">The destination is not writable.</exception>
        public ProgressStream(Stream inner, IProgress<long>? progress, bool leaveOpen = true)
        {
            if (!inner.CanWrite)
            {
                throw new ArgumentException("A writable stream is required.", nameof(inner));
            }
            _inner = inner;
            _progress = progress;
            _leaveOpen = leaveOpen;
        }

        /// <summary>Gets the total number of bytes written so far.</summary>
        public long BytesWritten => Interlocked.Read(ref _written);

        /// <summary>Gets false; the stream is write-only.</summary>
        public override bool CanRead => false;

        /// <summary>Gets false; the stream is forward-only.</summary>
        public override bool CanSeek => false;

        /// <summary>Gets whether the destination is writable.</summary>
        public override bool CanWrite => _inner.CanWrite;

        /// <summary>Gets the number of bytes written so far.</summary>
        public override long Length => BytesWritten;

        /// <summary>Gets the number of bytes written so far; setting is not supported.</summary>
        /// <exception cref="NotSupportedException">The position is set.</exception>
        public override long Position
        {
            get => BytesWritten;
            set => throw new NotSupportedException();
        }

        /// <summary>Flushes the destination.</summary>
        public override void Flush()
        {
            _inner.Flush();
        }

        /// <summary>Flushes the destination asynchronously.</summary>
        /// <param name="cancellationToken">Cancels flushing.</param>
        /// <returns>Completion after flushing.</returns>
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            return _inner.FlushAsync(cancellationToken);
        }

        /// <summary>Reading is not supported.</summary>
        /// <param name="buffer">The ignored buffer.</param>
        /// <param name="offset">The ignored offset.</param>
        /// <param name="count">The ignored count.</param>
        /// <returns>Never returns.</returns>
        /// <exception cref="NotSupportedException">Always.</exception>
        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        /// <summary>Seeking is not supported.</summary>
        /// <param name="offset">The ignored offset.</param>
        /// <param name="origin">The ignored origin.</param>
        /// <returns>Never returns.</returns>
        /// <exception cref="NotSupportedException">Always.</exception>
        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        /// <summary>Setting the length is not supported.</summary>
        /// <param name="value">The ignored length.</param>
        /// <exception cref="NotSupportedException">Always.</exception>
        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        /// <summary>Writes bytes and reports the new total.</summary>
        /// <param name="buffer">The source buffer.</param>
        /// <param name="offset">The offset of the first byte.</param>
        /// <param name="count">The number of bytes.</param>
        public override void Write(byte[] buffer, int offset, int count)
        {
            _inner.Write(buffer, offset, count);
            Advance(count);
        }

        /// <summary>Writes bytes and reports the new total.</summary>
        /// <param name="buffer">The bytes to write.</param>
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _inner.Write(buffer);
            Advance(buffer.Length);
        }

        /// <summary>Writes bytes asynchronously and reports the new total.</summary>
        /// <param name="buffer">The source buffer.</param>
        /// <param name="offset">The offset of the first byte.</param>
        /// <param name="count">The number of bytes.</param>
        /// <param name="cancellationToken">Cancels writing.</param>
        /// <returns>Completion after writing.</returns>
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        /// <summary>Writes bytes asynchronously and reports the new total.</summary>
        /// <param name="buffer">The bytes to write.</param>
        /// <param name="cancellationToken">Cancels writing.</param>
        /// <returns>Completion after writing.</returns>
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            Advance(buffer.Length);
        }

        /// <summary>Disposes the destination unless it should stay open.</summary>
        /// <param name="disposing">Whether managed resources are released.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && !_leaveOpen)
            {
                _inner.Dispose();
            }
            base.Dispose(disposing);
        }

        /// <summary>Adds written bytes to the total and reports it.</summary>
        /// <param name="count">The number of bytes written.</param>
        private void Advance(int count)
        {
            var total = Interlocked.Add(ref _written, count);
            _progress?.Report(total);
        }
    }
}
