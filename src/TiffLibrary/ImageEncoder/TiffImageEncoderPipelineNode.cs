﻿using System.Threading.Tasks;

namespace TiffLibrary.ImageEncoder
{
    internal sealed class TiffImageEncoderPipelineNode<TPixel> : ITiffImageEncoderPipelineNode<TPixel> where TPixel : unmanaged
    {
        public ITiffImageEncoderMiddleware<TPixel> Middleware { get; set; }
        public ITiffImageEncoderPipelineNode<TPixel>? Next { get; set; }

        public TiffImageEncoderPipelineNode(ITiffImageEncoderMiddleware<TPixel> middleware)
        {
            Middleware = middleware;
        }

        public ValueTask RunAsync(TiffImageEncoderContext<TPixel> context)
        {
            ITiffImageEncoderMiddleware<TPixel> middleware = Middleware;
            ITiffImageEncoderPipelineNode<TPixel>? next = Next;

            context.CancellationToken.ThrowIfCancellationRequested();

            if (next is null)
            {
                return middleware.InvokeAsync(context, EmptyImplementation.Instance);
            }
            else
            {
                return middleware.InvokeAsync(context, next);
            }
        }

        sealed class EmptyImplementation : ITiffImageEncoderPipelineNode<TPixel>
        {
            public static ITiffImageEncoderPipelineNode<TPixel> Instance { get; } = new EmptyImplementation();

            public ValueTask RunAsync(TiffImageEncoderContext<TPixel> context)
            {
                return default;
            }
        }

    }
}
