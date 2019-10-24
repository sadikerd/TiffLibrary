﻿using System;
using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace TiffLibrary
{
    internal sealed class TiffStreamContentSource : TiffFileContentSource
    {
        private Stream _stream;
        private ContentReader _reader;
        private readonly bool _leaveOpen;

        public TiffStreamContentSource(Stream stream, bool leaveOpen)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            _reader = new ContentReader(stream, true);
            _leaveOpen = leaveOpen;
        }

        public override ValueTask<TiffFileContentReader> OpenReaderAsync()
        {
            ContentReader reader = _reader;
            if (reader is null)
            {
                throw new ObjectDisposedException(nameof(TiffStreamContentSource));
            }
            return new ValueTask<TiffFileContentReader>(reader);
        }

        protected override void Dispose(bool disposing)
        {
            if (!(_stream is null) && !_leaveOpen)
            {
                _stream.Dispose();
            }
            _stream = null;
            _reader = null;
        }

#if !NO_ASYNC_DISPOSABLE_ON_STREAM

        public override async ValueTask DisposeAsync()
        {
            if (!(_stream is null) && !_leaveOpen)
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
            }
            _stream = null;
            _reader = null;
        }
#endif

        internal sealed class ContentReader : TiffFileContentReader
        {
            private Stream _stream;
            private readonly bool _leaveOpen;

            public ContentReader(Stream stream, bool leaveOpen)
            {
                _stream = stream;
                _leaveOpen = leaveOpen;
            }

#if NO_ASYNC_DISPOSABLE_ON_STREAM
            public override ValueTask DisposeAsync()
            {
                if (!(_stream is null) && !_leaveOpen)
                {
                    _stream.Dispose();
                    _stream = null;
                }
                return default;
            }
#else
            public override async ValueTask DisposeAsync()
            {
                if (!(_stream is null) && !_leaveOpen)
                {
                    await _stream.DisposeAsync().ConfigureAwait(false);
                _stream = null;
                }
            }
#endif

            protected override void Dispose(bool disposing)
            {
                if (disposing && !(_stream is null) && !_leaveOpen)
                {
                    _stream.Dispose();
                    _stream = null;
                }
            }

            public override ValueTask<int> ReadAsync(long offset, ArraySegment<byte> buffer)
            {
                Stream stream = _stream;
                if (stream is null)
                {
                    throw new ObjectDisposedException(nameof(ContentReader));
                }
                stream.Seek(offset, SeekOrigin.Begin);
                return new ValueTask<int>(stream.ReadAsync(buffer.Array, buffer.Offset, buffer.Count));
            }

            public override ValueTask<int> ReadAsync(long offset, Memory<byte> buffer)
            {
#if !NO_FAST_SPAN
                Stream stream = _stream;
                if (stream is null)
                {
                    throw new ObjectDisposedException(nameof(ContentReader));
                }
                stream.Seek(offset, SeekOrigin.Begin);
                return stream.ReadAsync(buffer);
#else
                return new ValueTask<int>(ReadFromStreamSlow());

                async Task<int> ReadFromStreamSlow()
                {
                    Stream stream = _stream;
                    if (stream is null)
                    {
                        throw new ObjectDisposedException(nameof(ContentReader));
                    }
                    stream.Seek(offset, SeekOrigin.Begin);
                    if (MemoryMarshal.TryGetArray(buffer, out ArraySegment<byte> arraySegment))
                    {
                        return await stream.ReadAsync(arraySegment.Array, arraySegment.Offset, arraySegment.Count).ConfigureAwait(false);
                    }

                    // Slow path
                    byte[] temp = ArrayPool<byte>.Shared.Rent(buffer.Length);
                    try
                    {
                        int count = await stream.ReadAsync(temp, 0, buffer.Length).ConfigureAwait(false);
                        temp.AsMemory(0, count).CopyTo(buffer);
                        return count;
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(temp);
                    }
                }
#endif
            }


        }
    }
}
