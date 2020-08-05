using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TiffLibrary
{
    /// <summary>
    /// A writer class that write content into the TIFF steam.
    /// </summary>
    public sealed class TiffFileWriter : IDisposable, IAsyncDisposable
    {
        private TiffFileContentReaderWriter? _writer;
        private bool _leaveOpen;
        private long _position;
        private readonly bool _useBigTiff;
        private bool _requireBigTiff;
        private bool _completed;
        private TiffOperationContext? _operationContext;
        private long _imageFileDirectoryOffset;

        private const int SmallBufferSize = 32;

        internal TiffFileWriter(TiffFileContentReaderWriter writer, bool leaveOpen, bool useBigTiff)
        {
            _writer = writer ?? throw new ArgumentNullException(nameof(writer));
            _leaveOpen = leaveOpen;

            _position = useBigTiff ? 16 : 8;
            _useBigTiff = useBigTiff;
            _requireBigTiff = false;
            _completed = false;
            _operationContext = useBigTiff ? TiffOperationContext.BigTIFF : TiffOperationContext.StandardTIFF;
        }

        internal TiffOperationContext OperationContext => _operationContext ?? ThrowObjectDisposedException<TiffOperationContext>();
        internal TiffFileContentReaderWriter InnerWriter => _writer ?? ThrowObjectDisposedException<TiffFileContentReaderWriter>();

        /// <summary>
        /// Gets whether to use BigTIFF format.
        /// </summary>
        public bool UseBigTiff => _useBigTiff;

        /// <summary>
        /// The current position of the stream.
        /// </summary>
        public TiffStreamOffset Position => new TiffStreamOffset(_position);

        /// <summary>
        /// Uses the specified stream to create <see cref="TiffFileWriter"/>.
        /// </summary>
        /// <param name="stream">A seekable and writable stream to use.</param>
        /// <param name="leaveOpen">Whether to leave the stream open when <see cref="TiffFileWriter"/> is dispsoed.</param>
        /// <param name="useBigTiff">Whether to use BigTIFF format.</param>
        /// <returns>The create <see cref="TiffFileWriter"/>.</returns>
        public static Task<TiffFileWriter> OpenAsync(Stream stream, bool leaveOpen, bool useBigTiff = false)
        {
            if (stream is null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            if (!stream.CanSeek)
            {
                throw new ArgumentException("Stream must be seekable.", nameof(stream));
            }
            if (!stream.CanWrite)
            {
                throw new ArgumentException("Stream must be writable.", nameof(stream));
            }

            return OpenAsync(new TiffStreamContentReaderWriter(stream, leaveOpen), false, useBigTiff);
        }

        /// <summary>
        /// Opens the specified file for writing and creates <see cref="TiffFileWriter"/>.
        /// </summary>
        /// <param name="fileName">The file to write to.</param>
        /// <param name="useBigTiff">Whether to use BigTIFF format.</param>
        /// <returns>The create <see cref="TiffFileWriter"/>.</returns>
        public static Task<TiffFileWriter> OpenAsync(string fileName, bool useBigTiff = false)
        {
            if (fileName is null)
            {
                throw new ArgumentNullException(nameof(fileName));
            }

            var fs = new FileStream(fileName, FileMode.Create, FileAccess.ReadWrite);
            return OpenAsync(new TiffStreamContentReaderWriter(fs, false), false, useBigTiff);
        }

        /// <summary>
        /// Uses the specified content writer to create <see cref="TiffFileWriter"/>.
        /// </summary>
        /// <param name="writer">The content writer to use.</param>
        /// <param name="leaveOpen">Whether to leave the content writer open when <see cref="TiffFileWriter"/> is dispsoed.</param>
        /// <param name="useBigTiff">Whether to use BigTIFF format.</param>
        /// <returns>The create <see cref="TiffFileWriter"/>.</returns>
        public static async Task<TiffFileWriter> OpenAsync(TiffFileContentReaderWriter writer, bool leaveOpen, bool useBigTiff = false)
        {
            if (writer is null)
            {
                throw new ArgumentNullException(nameof(writer));
            }

            TiffFileContentReaderWriter? disposeInstance = writer;
            byte[] smallBuffer = ArrayPool<byte>.Shared.Rent(SmallBufferSize);
            try
            {
                smallBuffer.AsSpan().Clear();
                await writer.WriteAsync(0, new ArraySegment<byte>(smallBuffer, 0, useBigTiff ? 16 : 8), CancellationToken.None).ConfigureAwait(false);
                disposeInstance = null;
                return new TiffFileWriter(writer, leaveOpen, useBigTiff);
            }
            finally
            {
                if (!leaveOpen && !(disposeInstance is null))
                {
                    await disposeInstance.DisposeAsync().ConfigureAwait(false);
                }
            }

        }

        #region Allignment

        /// <summary>
        /// Align the current position to word boundary.
        /// </summary>
        /// <returns>A <see cref="ValueTask{TiffStreamOffset}"/> that completes when the align operation is completed. Returns the current position.</returns>
        public ValueTask<TiffStreamOffset> AlignToWordBoundaryAsync()
        {
            EnsureNotDisposed();

            long position = _position;
            if ((position & 0b1) != 0)
            {
                return new ValueTask<TiffStreamOffset>(InternalAlignToWordBoundaryAsync());
            }
            return new ValueTask<TiffStreamOffset>(new TiffStreamOffset(position));
        }

        private async Task<TiffStreamOffset> InternalAlignToWordBoundaryAsync()
        {
            Debug.Assert(_writer != null);

            int length = (int)_position & 0b1;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(SmallBufferSize);
            try
            {
                buffer[0] = 0;
                await _writer!.WriteAsync(_position, new ArraySegment<byte>(buffer, 0, length), CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
            return new TiffStreamOffset(AdvancePosition(length));
        }

        #endregion

        internal long AdvancePosition(int length)
        {
            return AdvancePosition(checked((uint)length));
        }

        internal long AdvancePosition(uint length)
        {
            long position = _position;
            position += length;
            if (position > uint.MaxValue)
            {
                _requireBigTiff = true;
            }
            return _position = position;
        }

        internal long AdvancePosition(long length)
        {
            long position = _position;
            position += length;
            if (position > uint.MaxValue)
            {
                _requireBigTiff = true;
            }
            return _position = position;
        }

        /// <summary>
        /// Seek to the specified position.
        /// </summary>
        /// <param name="position">The specified position in the stream.</param>
        public void Seek(TiffStreamOffset position)
        {
            EnsureNotDisposed();

            long pos = position.ToInt64();
            _position = pos;
        }

        #region IFDs

        /// <summary>
        /// Sets the first IFD offset to the specified offset.
        /// </summary>
        /// <param name="ifdOffset">The offset of the first IFD.</param>
        public void SetFirstImageFileDirectoryOffset(TiffStreamOffset ifdOffset)
        {
            _imageFileDirectoryOffset = ifdOffset;
        }

        /// <summary>
        /// Creates a <see cref="TiffImageFileDirectoryEntry"/> for writing a new IFD.
        /// </summary>
        /// <returns></returns>
        public TiffImageFileDirectoryWriter CreateImageFileDirectory()
        {
            return new TiffImageFileDirectoryWriter(this);
        }

        internal async Task UpdateImageFileDirectoryNextOffsetFieldAsync(TiffStreamOffset target, TiffStreamOffset ifdOffset)
        {
            EnsureNotDisposed();

            Debug.Assert(_writer != null);
            Debug.Assert(_operationContext != null);

            // Attemps to read 8 bytes even though the size of IFD may be less then 8 bytes.
            byte[] buffer = ArrayPool<byte>.Shared.Rent(SmallBufferSize);
            try
            {
                int rwCount = await _writer!.ReadAsync(target, new ArraySegment<byte>(buffer, 0, 8), CancellationToken.None).ConfigureAwait(false);
                if (!(_useBigTiff && rwCount == 8) && !(!_useBigTiff && rwCount >= 4))
                {
                    throw new InvalidDataException();
                }
                int count = ParseImageFileDirectoryCount(buffer.AsSpan(0, 8));

                // Prepare next ifd.
                if (_useBigTiff)
                {
                    rwCount = 8;
                    long offset = ifdOffset;
                    MemoryMarshal.Write(buffer, ref offset);
                }
                else
                {
                    rwCount = 4;
                    int offset32 = (int)ifdOffset;
                    MemoryMarshal.Write(buffer, ref offset32);
                }

                // Skip over IFD entries.
                int entryFieldLength = _useBigTiff ? 20 : 12;
                await _writer.WriteAsync(target + _operationContext!.ByteCountOfImageFileDirectoryCountField + count * entryFieldLength, new ArraySegment<byte>(buffer, 0, rwCount), CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        internal int ParseImageFileDirectoryCount(ReadOnlySpan<byte> buffer)
        {
            if (_useBigTiff)
            {
                return checked((int)MemoryMarshal.Read<ulong>(buffer));
            }
            return MemoryMarshal.Read<ushort>(buffer);
        }

        #endregion

        #region Primitives

        /// <summary>
        /// Writes a series of bytes into the TIFF stream.
        /// </summary>
        /// <param name="buffer">The bytes buffer.</param>
        /// <param name="index">The number of bytes to skip in the buffer.</param>
        /// <param name="count">The number of bytes to write.</param>
        /// <returns>A <see cref="Task"/> that completes when the bytes have been written.</returns>
        public async Task<TiffStreamOffset> WriteBytesAsync(byte[] buffer, int index, int count)
        {
            if (buffer is null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }
            if ((uint)index >= (uint)buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }
            if ((uint)(index + count) > (uint)buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            EnsureNotDisposed();
            EnsureNotCompleted();

            Debug.Assert(_writer != null);
            long position = _position;
            await _writer!.WriteAsync(position, new ArraySegment<byte>(buffer, index, count), CancellationToken.None).ConfigureAwait(false);
            AdvancePosition(count);

            return new TiffStreamOffset(position);
        }

        /// <summary>
        /// Writes a series of bytes into the TIFF stream.
        /// </summary>
        /// <param name="buffer">The bytes buffer.</param>
        /// <returns>A <see cref="Task"/> that completes when the bytes have been written.</returns>
        public async Task<TiffStreamOffset> WriteBytesAsync(ReadOnlyMemory<byte> buffer)
        {
            EnsureNotDisposed();
            EnsureNotCompleted();

            Debug.Assert(_writer != null);
            long position = _position;
            await _writer!.WriteAsync(position, buffer, CancellationToken.None).ConfigureAwait(false);
            AdvancePosition(buffer.Length);

            return new TiffStreamOffset(position);
        }

        /// <summary>
        /// Align to word boundary and writes a series of bytes into the TIFF stream.
        /// </summary>
        /// <param name="buffer">The bytes buffer.</param>
        /// <returns>A <see cref="Task"/> that completes when the bytes have been written.</returns>
        public Task<TiffStreamOffset> WriteAlignedBytesAsync(byte[] buffer)
        {
            if (buffer is null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }

            return WriteAlignedBytesAsync(buffer, 0, buffer.Length);
        }

        /// <summary>
        /// Align to word boundary and writes a series of bytes into the TIFF stream.
        /// </summary>
        /// <param name="buffer">The bytes buffer.</param>
        /// <param name="index">The number of bytes to skip in the buffer.</param>
        /// <param name="count">The number of bytes to write.</param>
        /// <returns>A <see cref="Task"/> that completes when the bytes have been written.</returns>
        public async Task<TiffStreamOffset> WriteAlignedBytesAsync(byte[] buffer, int index, int count)
        {
            if (buffer is null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }
            if ((uint)index >= (uint)buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }
            if ((uint)(index + count) > (uint)buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            EnsureNotDisposed();
            EnsureNotCompleted();

            Debug.Assert(_writer != null);
            long position = await AlignToWordBoundaryAsync().ConfigureAwait(false);
            await _writer!.WriteAsync(position, new ArraySegment<byte>(buffer, index, count), CancellationToken.None).ConfigureAwait(false);
            AdvancePosition(count);

            return new TiffStreamOffset(position);
        }

        /// <summary>
        /// Align to word boundary and writes a series of bytes into the TIFF stream.
        /// </summary>
        /// <param name="buffer">The bytes buffer.</param>
        /// <returns>A <see cref="Task"/> that completes when the bytes have been written.</returns>
        public async Task<TiffStreamOffset> WriteAlignedBytesAsync(ReadOnlyMemory<byte> buffer)
        {
            EnsureNotDisposed();
            EnsureNotCompleted();

            Debug.Assert(_writer != null);
            long position = await AlignToWordBoundaryAsync().ConfigureAwait(false);
            long length = buffer.Length;
            await _writer!.WriteAsync(position, buffer, CancellationToken.None).ConfigureAwait(false);
            AdvancePosition(length);

            return new TiffStreamOffset(position);
        }

        internal async Task<TiffStreamOffset> WriteAlignedBytesAsync(ReadOnlySequence<byte> buffer)
        {
            EnsureNotDisposed();
            EnsureNotCompleted();

            Debug.Assert(_writer != null);
            long position = await AlignToWordBoundaryAsync().ConfigureAwait(false);
            long length = buffer.Length;
            long offset = position;
            foreach (ReadOnlyMemory<byte> segment in buffer)
            {
                await _writer!.WriteAsync(offset, segment, CancellationToken.None).ConfigureAwait(false);
                offset += segment.Length;
            }
            AdvancePosition(length);

            return new TiffStreamOffset(position);
        }


        internal async Task<TiffStreamRegion> WriteAlignedValues(TiffValueCollection<string> values)
        {
            EnsureNotDisposed();
            EnsureNotCompleted();

            Debug.Assert(_writer != null);
            long position = await AlignToWordBoundaryAsync().ConfigureAwait(false);

            int maxByteCount = 0;
            foreach (string item in values)
            {
                maxByteCount = Math.Max(maxByteCount, Encoding.ASCII.GetMaxByteCount(item.Length));
            }

            long offset = position;
            int bytesWritten = 0;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(maxByteCount + 1);
            try
            {
                foreach (string item in values)
                {
                    int length = Encoding.ASCII.GetBytes(item, 0, item.Length, buffer, 0);
                    buffer[length] = 0;
                    await _writer!.WriteAsync(offset, new ArraySegment<byte>(buffer, 0, length + 1), CancellationToken.None).ConfigureAwait(false);
                    offset += length + 1;
                    AdvancePosition(length + 1);
                    bytesWritten += length + 1;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return new TiffStreamRegion(position, bytesWritten);
        }

        internal async Task<TiffStreamRegion> WriteAlignedValues(TiffValueCollection<ushort> values)
        {
            EnsureNotDisposed();
            EnsureNotCompleted();

            Debug.Assert(_writer != null);
            long position = await AlignToWordBoundaryAsync().ConfigureAwait(false);

            const int ElementSize = sizeof(ushort);
            int byteCount = ElementSize * values.Count;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(byteCount);
            try
            {
                MemoryMarshal.AsBytes(values.GetOrCreateArray().AsSpan()).CopyTo(buffer);
                await _writer!.WriteAsync(position, new ArraySegment<byte>(buffer, 0, byteCount), CancellationToken.None).ConfigureAwait(false);
                AdvancePosition(byteCount);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return new TiffStreamRegion(position, byteCount);
        }

        internal async Task<TiffStreamRegion> WriteAlignedValues(TiffValueCollection<short> values)
        {
            EnsureNotDisposed();
            EnsureNotCompleted();

            Debug.Assert(_writer != null);
            long position = await AlignToWordBoundaryAsync().ConfigureAwait(false);

            const int ElementSize = sizeof(short);
            int byteCount = ElementSize * values.Count;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(byteCount);
            try
            {
                MemoryMarshal.AsBytes(values.GetOrCreateArray().AsSpan()).CopyTo(buffer);
                await _writer!.WriteAsync(position, new ArraySegment<byte>(buffer, 0, byteCount), CancellationToken.None).ConfigureAwait(false);
                AdvancePosition(byteCount);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return new TiffStreamRegion(position, byteCount);
        }

        internal async Task<TiffStreamRegion> WriteAlignedValues(TiffValueCollection<uint> values)
        {
            EnsureNotDisposed();
            EnsureNotCompleted();

            Debug.Assert(_writer != null);
            long position = await AlignToWordBoundaryAsync().ConfigureAwait(false);

            const int ElementSize = sizeof(uint);
            int byteCount = ElementSize * values.Count;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(byteCount);
            try
            {
                MemoryMarshal.AsBytes(values.GetOrCreateArray().AsSpan()).CopyTo(buffer);
                await _writer!.WriteAsync(position, new ArraySegment<byte>(buffer, 0, byteCount), CancellationToken.None).ConfigureAwait(false);
                AdvancePosition(byteCount);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return new TiffStreamRegion(position, byteCount);
        }

        internal async Task<TiffStreamRegion> WriteAlignedValues(TiffValueCollection<int> values)
        {
            EnsureNotDisposed();
            EnsureNotCompleted();

            Debug.Assert(_writer != null);
            long position = await AlignToWordBoundaryAsync().ConfigureAwait(false);

            const int ElementSize = sizeof(int);
            int byteCount = ElementSize * values.Count;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(byteCount);
            try
            {
                MemoryMarshal.AsBytes(values.GetOrCreateArray().AsSpan()).CopyTo(buffer);
                await _writer!.WriteAsync(position, new ArraySegment<byte>(buffer, 0, byteCount), CancellationToken.None).ConfigureAwait(false);
                AdvancePosition(byteCount);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return new TiffStreamRegion(position, byteCount);
        }

        internal async Task<TiffStreamRegion> WriteAlignedValues(TiffValueCollection<ulong> values)
        {
            EnsureNotDisposed();
            EnsureNotCompleted();

            Debug.Assert(_writer != null);
            long position = await AlignToWordBoundaryAsync().ConfigureAwait(false);

            const int ElementSize = sizeof(ulong);
            int byteCount = ElementSize * values.Count;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(byteCount);
            try
            {
                MemoryMarshal.AsBytes(values.GetOrCreateArray().AsSpan()).CopyTo(buffer);
                await _writer!.WriteAsync(position, new ArraySegment<byte>(buffer, 0, byteCount), CancellationToken.None).ConfigureAwait(false);
                AdvancePosition(byteCount);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return new TiffStreamRegion(position, byteCount);
        }

        internal async Task<TiffStreamRegion> WriteAlignedValues(TiffValueCollection<long> values)
        {
            EnsureNotDisposed();
            EnsureNotCompleted();

            Debug.Assert(_writer != null);
            long position = await AlignToWordBoundaryAsync().ConfigureAwait(false);

            const int ElementSize = sizeof(long);
            int byteCount = ElementSize * values.Count;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(byteCount);
            try
            {
                MemoryMarshal.AsBytes(values.GetOrCreateArray().AsSpan()).CopyTo(buffer);
                await _writer!.WriteAsync(position, new ArraySegment<byte>(buffer, 0, byteCount), CancellationToken.None).ConfigureAwait(false);
                AdvancePosition(byteCount);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return new TiffStreamRegion(position, byteCount);
        }

        internal async Task<TiffStreamRegion> WriteAlignedValues(TiffValueCollection<float> values)
        {
            EnsureNotDisposed();
            EnsureNotCompleted();

            Debug.Assert(_writer != null);
            long position = await AlignToWordBoundaryAsync().ConfigureAwait(false);

            const int ElementSize = sizeof(float);
            int byteCount = ElementSize * values.Count;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(byteCount);
            try
            {
                MemoryMarshal.AsBytes(values.GetOrCreateArray().AsSpan()).CopyTo(buffer);
                await _writer!.WriteAsync(position, new ArraySegment<byte>(buffer, 0, byteCount), CancellationToken.None).ConfigureAwait(false);
                AdvancePosition(byteCount);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return new TiffStreamRegion(position, byteCount);
        }

        internal async Task<TiffStreamRegion> WriteAlignedValues(TiffValueCollection<double> values)
        {
            EnsureNotDisposed();
            EnsureNotCompleted();

            Debug.Assert(_writer != null);
            long position = await AlignToWordBoundaryAsync().ConfigureAwait(false);

            const int ElementSize = sizeof(double);
            int byteCount = ElementSize * values.Count;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(byteCount);
            try
            {
                MemoryMarshal.AsBytes(values.GetOrCreateArray().AsSpan()).CopyTo(buffer);
                await _writer!.WriteAsync(position, new ArraySegment<byte>(buffer, 0, byteCount), CancellationToken.None).ConfigureAwait(false);
                AdvancePosition(byteCount);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return new TiffStreamRegion(position, byteCount);
        }

        internal async Task<TiffStreamRegion> WriteAlignedValues(TiffValueCollection<TiffRational> values)
        {
            EnsureNotDisposed();
            EnsureNotCompleted();

            Debug.Assert(_writer != null);
            long position = await AlignToWordBoundaryAsync().ConfigureAwait(false);

            const int ElementSize = 2 * sizeof(uint);
            int byteCount = ElementSize * values.Count;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(byteCount);
            try
            {
                MemoryMarshal.AsBytes(values.GetOrCreateArray().AsSpan()).CopyTo(buffer);
                await _writer!.WriteAsync(position, new ArraySegment<byte>(buffer, 0, byteCount), CancellationToken.None).ConfigureAwait(false);
                AdvancePosition(byteCount);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return new TiffStreamRegion(position, byteCount);
        }

        internal async Task<TiffStreamRegion> WriteAlignedValues(TiffValueCollection<TiffSRational> values)
        {
            EnsureNotDisposed();
            EnsureNotCompleted();

            Debug.Assert(_writer != null);
            long position = await AlignToWordBoundaryAsync().ConfigureAwait(false);

            const int ElementSize = 2 * sizeof(int);
            int byteCount = ElementSize * values.Count;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(byteCount);
            try
            {
                MemoryMarshal.AsBytes(values.GetOrCreateArray().AsSpan()).CopyTo(buffer);
                await _writer!.WriteAsync(position, new ArraySegment<byte>(buffer, 0, byteCount), CancellationToken.None).ConfigureAwait(false);
                AdvancePosition(byteCount);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return new TiffStreamRegion(position, byteCount);
        }

        #endregion

        #region Tiff file header

        /// <summary>
        /// Flush the TIFF file header into the stream.
        /// </summary>
        /// <returns></returns>
        public async Task FlushAsync()
        {
            EnsureNotDisposed();
            EnsureNotCompleted();

            Debug.Assert(_writer != null);
            if (_requireBigTiff && !_useBigTiff)
            {
                throw new InvalidOperationException("Must use BigTIFF format. But it is disabled.");
            }

            byte[] buffer = ArrayPool<byte>.Shared.Rent(SmallBufferSize);
            try
            {
                Array.Clear(buffer, 0, 16);
                TiffFileHeader.Write(buffer, _imageFileDirectoryOffset, BitConverter.IsLittleEndian, _useBigTiff);
                await _writer!.WriteAsync(0, new ArraySegment<byte>(buffer, 0, _useBigTiff ? 16 : 8), CancellationToken.None).ConfigureAwait(false);
                await _writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

        }

        private void EnsureNotCompleted()
        {
            if (_completed)
            {
                ThrowWriterCompleted();
            }
        }

        private static void ThrowWriterCompleted()
        {
            throw new InvalidOperationException("Writer is completed.");
        }

        #endregion

        #region Dispose support

        private void EnsureNotDisposed()
        {
            if (_writer is null)
            {
                ThrowObjectDisposedException();
            }
        }

        [DoesNotReturn]
        private static void ThrowObjectDisposedException()
        {
            throw new ObjectDisposedException(nameof(TiffFileWriter));
        }

        [DoesNotReturn]
        private static T ThrowObjectDisposedException<T>()
        {
            throw new ObjectDisposedException(nameof(TiffFileWriter));
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (!_leaveOpen)
            {
                _writer?.Dispose();
            }
            _writer = null;
            _operationContext = null;
            _leaveOpen = true;
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (!_leaveOpen && !(_writer is null))
            {
                await _writer.DisposeAsync().ConfigureAwait(false);
            }
            _writer = null;
            _operationContext = null;
            _leaveOpen = true;
        }

        #endregion
    }
}
