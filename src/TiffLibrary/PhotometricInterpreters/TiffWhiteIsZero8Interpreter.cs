﻿using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using TiffLibrary.ImageDecoder;
using TiffLibrary.PixelBuffer;
using TiffLibrary.PixelFormats;

namespace TiffLibrary.PhotometricInterpreters
{
    /// <summary>
    /// A middleware to read 8-bit WhiteIsZero pixels from uncompressed data to destination buffer writer.
    /// </summary>
    public sealed class TiffWhiteIsZero8Interpreter : ITiffImageDecoderMiddleware
    {
        /// <summary>
        /// A shared instance of <see cref="TiffWhiteIsZero8Interpreter"/>.
        /// </summary>
        public static TiffWhiteIsZero8Interpreter Instance { get; } = new TiffWhiteIsZero8Interpreter();

        /// <inheritdoc />
        public ValueTask InvokeAsync(TiffImageDecoderContext context, ITiffImageDecoderPipelineNode next)
        {
            if (context is null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (next is null)
            {
                throw new ArgumentNullException(nameof(next));
            }

            int bytesPerScanline = context.SourceImageSize.Width;
            Memory<byte> source = context.UncompressedData.Slice(context.SourceReadOffset.Y * bytesPerScanline);
            ReadOnlySpan<byte> sourceSpan = source.Span;

            using TiffPixelBufferWriter<TiffGray8> writer = context.GetWriter<TiffGray8>();

            int rows = context.ReadSize.Height;
            for (int row = 0; row < rows; row++)
            {
                using TiffPixelSpanHandle<TiffGray8> pixelSpanHandle = writer.GetRowSpan(row);
                ReadOnlySpan<byte> rowSourceSpan = sourceSpan.Slice(context.SourceReadOffset.X, context.ReadSize.Width);
                Span<byte> rowDestinationSpan = MemoryMarshal.AsBytes(pixelSpanHandle.GetSpan());
                InvertCopy(rowSourceSpan, rowDestinationSpan);
                sourceSpan = sourceSpan.Slice(bytesPerScanline);
            }

            return next.RunAsync(context);
        }

        private static unsafe void InvertCopy(ReadOnlySpan<byte> source, Span<byte> destination)
        {
            if (destination.Length < source.Length)
            {
                throw new InvalidOperationException("destination too short.");
            }

            int count8 = source.Length / 8;
            ReadOnlySpan<ulong> source8 = MemoryMarshal.Cast<byte, ulong>(source.Slice(0, 8 * count8));
            Span<ulong> destination8 = MemoryMarshal.Cast<byte, ulong>(destination.Slice(0, 8 * count8));
            for (int i = 0; i < source8.Length; i++)
            {
                destination8[i] = ~source8[i];
            }

            for (int i = 8 * count8; i < source.Length; i++)
            {
                destination[i] = (byte)~source[i];
            }
        }
    }
}
