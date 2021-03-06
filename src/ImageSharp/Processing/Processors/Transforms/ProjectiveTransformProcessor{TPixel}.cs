// Copyright (c) Six Labors and contributors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Numerics;

using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.ParallelUtils;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.Primitives;

namespace SixLabors.ImageSharp.Processing.Processors.Transforms
{
    /// <summary>
    /// Provides the base methods to perform non-affine transforms on an image.
    /// </summary>
    /// <typeparam name="TPixel">The pixel format.</typeparam>
    internal class ProjectiveTransformProcessor<TPixel> : TransformProcessor<TPixel>
        where TPixel : struct, IPixel<TPixel>
    {
        private Size targetSize;
        private readonly IResampler resampler;
        private Matrix4x4 transformMatrix;

        /// <summary>
        /// Initializes a new instance of the <see cref="ProjectiveTransformProcessor{TPixel}"/> class.
        /// </summary>
        /// <param name="definition">The <see cref="ProjectiveTransformProcessor"/> defining the processor parameters.</param>
        /// <param name="source">The source <see cref="Image{TPixel}"/> for the current processor instance.</param>
        /// <param name="sourceRectangle">The source area to process for the current processor instance.</param>
        public ProjectiveTransformProcessor(ProjectiveTransformProcessor definition, Image<TPixel> source, Rectangle sourceRectangle)
            : base(source, sourceRectangle)
        {
            this.targetSize = definition.TargetDimensions;
            this.transformMatrix = definition.TransformMatrix;
            this.resampler = definition.Sampler;
        }

        protected override Size GetTargetSize() => this.targetSize;

        /// <inheritdoc/>
        protected override void OnFrameApply(ImageFrame<TPixel> source, ImageFrame<TPixel> destination)
        {
            // Handle transforms that result in output identical to the original.
            if (this.transformMatrix.Equals(default) || this.transformMatrix.Equals(Matrix4x4.Identity))
            {
                // The clone will be blank here copy all the pixel data over
                source.GetPixelSpan().CopyTo(destination.GetPixelSpan());
                return;
            }

            int width = this.targetSize.Width;
            Rectangle sourceBounds = this.SourceRectangle;
            var targetBounds = new Rectangle(Point.Empty, this.targetSize);
            Configuration configuration = this.Configuration;

            // Convert from screen to world space.
            Matrix4x4.Invert(this.transformMatrix, out Matrix4x4 matrix);

            if (this.resampler is NearestNeighborResampler)
            {
                ParallelHelper.IterateRows(
                    targetBounds,
                    configuration,
                    rows =>
                        {
                            for (int y = rows.Min; y < rows.Max; y++)
                            {
                                Span<TPixel> destRow = destination.GetPixelRowSpan(y);

                                for (int x = 0; x < width; x++)
                                {
                                    Vector2 point = TransformUtils.ProjectiveTransform2D(x, y, matrix);
                                    int px = (int)MathF.Round(point.X);
                                    int py = (int)MathF.Round(point.Y);

                                    if (sourceBounds.Contains(px, py))
                                    {
                                        destRow[x] = source[px, py];
                                    }
                                }
                            }
                        });

                return;
            }

            var kernel = new TransformKernelMap(configuration, source.Size(), destination.Size(), this.resampler);

            try
            {
                ParallelHelper.IterateRowsWithTempBuffer<Vector4>(
                    targetBounds,
                    configuration,
                    (rows, vectorBuffer) =>
                        {
                            Span<Vector4> vectorSpan = vectorBuffer.Span;
                            for (int y = rows.Min; y < rows.Max; y++)
                            {
                                Span<TPixel> targetRowSpan = destination.GetPixelRowSpan(y);
                                PixelOperations<TPixel>.Instance.ToVector4(configuration, targetRowSpan, vectorSpan);
                                ref float ySpanRef = ref kernel.GetYStartReference(y);
                                ref float xSpanRef = ref kernel.GetXStartReference(y);

                                for (int x = 0; x < width; x++)
                                {
                                    // Use the single precision position to calculate correct bounding pixels
                                    // otherwise we get rogue pixels outside of the bounds.
                                    Vector2 point = TransformUtils.ProjectiveTransform2D(x, y, matrix);
                                    kernel.Convolve(
                                        point,
                                        x,
                                        ref ySpanRef,
                                        ref xSpanRef,
                                        source.PixelBuffer,
                                        vectorSpan);
                                }

                                PixelOperations<TPixel>.Instance.FromVector4Destructive(
                                    configuration,
                                    vectorSpan,
                                    targetRowSpan);
                            }
                        });
            }
            finally
            {
                kernel.Dispose();
            }
        }
    }
}
