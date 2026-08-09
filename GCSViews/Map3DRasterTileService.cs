using OSGeo.GDAL;
using OSGeo.OSR;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace MissionPlanner.GCSViews
{
    internal sealed class Map3DRasterInfo
    {
        internal int BandCount { get; set; }
        internal DataType DataType { get; set; }
        internal int Width { get; set; }
        internal int Height { get; set; }
        internal double West { get; set; }
        internal double South { get; set; }
        internal double East { get; set; }
        internal double North { get; set; }
        internal string DriverName { get; set; }
        internal string Projection { get; set; }

        internal bool LooksLikeTerrain => BandCount == 1 && DataType != DataType.GDT_Byte;
    }

    /// <summary>
    /// Serves georeferenced raster packages as cached Cesium imagery or heightmap tiles.
    /// GDAL handles the source format and coordinate transformation; generated tiles stay local.
    /// </summary>
    internal static class Map3DRasterTileService
    {
        private const int ImageryTileSize = 256;
        internal const int TerrainTileSize = 65;
        private static readonly object RuntimeLock = new object();
        private static readonly Dictionary<string, RasterContext> Contexts =
            new Dictionary<string, RasterContext>(StringComparer.OrdinalIgnoreCase);
        private static bool gdalReady;

        internal static Map3DRasterInfo Inspect(string path)
        {
            using (RasterContext context = CreateContext(path))
            {
                return context.Info;
            }
        }

        internal static byte[] GetImageryTile(
            Map3DOfflinePackage package,
            int zoom,
            int x,
            int y)
        {
            if (package == null || zoom < 0 || zoom > 22 || !IsValidTile(zoom, x, y))
            {
                return null;
            }

            string cachePath = Map3DOfflinePackageCatalog.GetPackageCachePath(
                package,
                "imagery",
                zoom,
                x,
                y,
                ".png");
            if (File.Exists(cachePath))
            {
                return File.ReadAllBytes(cachePath);
            }

            string sourcePath = Map3DOfflinePackageCatalog.GetPackageDataPath(package);
            RasterContext context = GetContext(sourcePath);
            GeoBounds bounds = GetWebMercatorTileBounds(zoom, x, y);
            byte[] tile;
            lock (context.SyncRoot)
            {
                tile = RenderImageryTile(context, bounds);
            }

            if (tile != null && tile.Length > 0)
            {
                WriteCacheFile(cachePath, tile);
            }

            return tile;
        }

        internal static byte[] GetTerrainTile(
            Map3DOfflinePackage package,
            int level,
            int x,
            int y)
        {
            if (package == null || level < 0 || level > 18 || !IsValidGeographicTile(level, x, y))
            {
                return null;
            }

            string cachePath = Map3DOfflinePackageCatalog.GetPackageCachePath(
                package,
                "terrain",
                level,
                x,
                y,
                ".f32");
            if (File.Exists(cachePath))
            {
                return File.ReadAllBytes(cachePath);
            }

            string sourcePath = Map3DOfflinePackageCatalog.GetPackageDataPath(package);
            RasterContext context = GetContext(sourcePath);
            GeoBounds bounds = GetGeographicTileBounds(level, x, y);
            byte[] tile;
            lock (context.SyncRoot)
            {
                tile = RenderTerrainTile(context, bounds);
            }

            if (tile != null && tile.Length > 0)
            {
                WriteCacheFile(cachePath, tile);
            }

            return tile;
        }

        internal static void Invalidate(string sourcePath = null)
        {
            lock (RuntimeLock)
            {
                IEnumerable<string> keys = string.IsNullOrWhiteSpace(sourcePath)
                    ? Contexts.Keys.ToArray()
                    : Contexts.Keys.Where(key => string.Equals(key, Path.GetFullPath(sourcePath),
                        StringComparison.OrdinalIgnoreCase)).ToArray();
                foreach (string key in keys)
                {
                    Contexts[key].Dispose();
                    Contexts.Remove(key);
                }
            }
        }

        private static RasterContext GetContext(string path)
        {
            string fullPath = Path.GetFullPath(path);
            lock (RuntimeLock)
            {
                RasterContext context;
                if (Contexts.TryGetValue(fullPath, out context) && context.MatchesSource())
                {
                    return context;
                }

                context?.Dispose();
                Contexts.Remove(fullPath);
                context = CreateContext(fullPath);
                Contexts[fullPath] = context;
                return context;
            }
        }

        private static RasterContext CreateContext(string path)
        {
            EnsureGdal();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                throw new FileNotFoundException("Offline raster data was not found.", path);
            }

            Dataset dataset = Gdal.Open(path, Access.GA_ReadOnly);
            if (dataset == null || dataset.RasterCount < 1)
            {
                dataset?.Dispose();
                throw new InvalidDataException(
                    "GDAL could not open this file as raster map data. Vector MBTiles are not supported.");
            }

            try
            {
                string projection = dataset.GetProjectionRef();
                if (string.IsNullOrWhiteSpace(projection))
                {
                    throw new InvalidDataException(
                        "The raster has no coordinate reference system. Use a georeferenced map file.");
                }

                double[] transform = new double[6];
                dataset.GetGeoTransform(transform);
                double[] inverse = new double[6];
                if (Gdal.InvGeoTransform(transform, inverse) == 0)
                {
                    throw new InvalidDataException("The raster geotransform cannot be inverted.");
                }

                var sourceReference = new SpatialReference(projection);
                var wgs84 = new SpatialReference(string.Empty);
                wgs84.ImportFromEPSG(4326);
                var wgsToSource = new CoordinateTransformation(wgs84, sourceReference);
                var sourceToWgs = new CoordinateTransformation(sourceReference, wgs84);

                GeoBounds bounds = CalculateDatasetBounds(dataset, transform, sourceToWgs);
                Band firstBand = dataset.GetRasterBand(1);
                Driver driver = dataset.GetDriver();
                var info = new Map3DRasterInfo
                {
                    BandCount = dataset.RasterCount,
                    DataType = firstBand.DataType,
                    Width = dataset.RasterXSize,
                    Height = dataset.RasterYSize,
                    West = bounds.West,
                    South = bounds.South,
                    East = bounds.East,
                    North = bounds.North,
                    DriverName = driver == null ? "Raster" : driver.LongName,
                    Projection = projection
                };

                return new RasterContext(path, dataset, inverse, wgsToSource, sourceToWgs, info);
            }
            catch
            {
                dataset.Dispose();
                throw;
            }
        }

        private static void EnsureGdal()
        {
            lock (RuntimeLock)
            {
                if (gdalReady)
                {
                    return;
                }

                // The wrapper's static constructor selects the matching x86/x64 native runtime.
                var runtime = new GDAL.GDAL();
                Gdal.AllRegister();
                gdalReady = true;
            }
        }

        private static GeoBounds CalculateDatasetBounds(
            Dataset dataset,
            double[] transform,
            CoordinateTransformation sourceToWgs)
        {
            double[][] corners =
            {
                TransformPixel(transform, sourceToWgs, 0, 0),
                TransformPixel(transform, sourceToWgs, dataset.RasterXSize, 0),
                TransformPixel(transform, sourceToWgs, 0, dataset.RasterYSize),
                TransformPixel(transform, sourceToWgs, dataset.RasterXSize, dataset.RasterYSize)
            };
            return new GeoBounds(
                corners.Min(point => point[0]),
                corners.Min(point => point[1]),
                corners.Max(point => point[0]),
                corners.Max(point => point[1]));
        }

        private static double[] TransformPixel(
            double[] transform,
            CoordinateTransformation sourceToWgs,
            double pixelX,
            double pixelY)
        {
            double sourceX;
            double sourceY;
            Gdal.ApplyGeoTransform(transform, pixelX, pixelY, out sourceX, out sourceY);
            double[] point = { sourceX, sourceY, 0 };
            sourceToWgs.TransformPoint(point);
            return point;
        }

        private static byte[] RenderImageryTile(RasterContext context, GeoBounds bounds)
        {
            SourceWindow window;
            if (!TryGetSourceWindow(context, bounds, ImageryTileSize, ImageryTileSize, out window))
            {
                return null;
            }

            int pixelCount = window.DestinationWidth * window.DestinationHeight;
            var bandBuffers = new Dictionary<ColorInterp, byte[]>();
            var fallbackBands = new List<byte[]>();
            for (int bandIndex = 1; bandIndex <= Math.Min(4, context.Dataset.RasterCount); bandIndex++)
            {
                Band band = context.Dataset.GetRasterBand(bandIndex);
                var values = new byte[pixelCount];
                CPLErr result = band.ReadRaster(
                    window.SourceX,
                    window.SourceY,
                    window.SourceWidth,
                    window.SourceHeight,
                    values,
                    window.DestinationWidth,
                    window.DestinationHeight,
                    0,
                    0);
                if (result != CPLErr.CE_None)
                {
                    return null;
                }

                ColorInterp interpretation = band.GetRasterColorInterpretation();
                if (interpretation != ColorInterp.GCI_Undefined)
                {
                    bandBuffers[interpretation] = values;
                }
                fallbackBands.Add(values);
            }

            byte[] red = GetColorBand(bandBuffers, ColorInterp.GCI_RedBand, fallbackBands, 0);
            byte[] green = GetColorBand(bandBuffers, ColorInterp.GCI_GreenBand, fallbackBands,
                fallbackBands.Count > 1 ? 1 : 0);
            byte[] blue = GetColorBand(bandBuffers, ColorInterp.GCI_BlueBand, fallbackBands,
                fallbackBands.Count > 2 ? 2 : 0);
            byte[] alpha = GetColorBand(bandBuffers, ColorInterp.GCI_AlphaBand, fallbackBands, -1);

            using (var bitmap = new Bitmap(ImageryTileSize, ImageryTileSize,
                       PixelFormat.Format32bppArgb))
            {
                Rectangle rectangle = new Rectangle(0, 0, ImageryTileSize, ImageryTileSize);
                BitmapData data = bitmap.LockBits(rectangle, ImageLockMode.WriteOnly,
                    PixelFormat.Format32bppArgb);
                try
                {
                    byte[] pixels = new byte[data.Stride * ImageryTileSize];
                    for (int row = 0; row < window.DestinationHeight; row++)
                    {
                        for (int column = 0; column < window.DestinationWidth; column++)
                        {
                            int sourceIndex = row * window.DestinationWidth + column;
                            int destinationX = window.DestinationX + column;
                            int destinationY = window.DestinationY + row;
                            int destinationIndex = destinationY * data.Stride + destinationX * 4;
                            pixels[destinationIndex] = blue[sourceIndex];
                            pixels[destinationIndex + 1] = green[sourceIndex];
                            pixels[destinationIndex + 2] = red[sourceIndex];
                            pixels[destinationIndex + 3] = alpha == null ? (byte)255 : alpha[sourceIndex];
                        }
                    }
                    Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
                }
                finally
                {
                    bitmap.UnlockBits(data);
                }

                using (var stream = new MemoryStream())
                {
                    bitmap.Save(stream, ImageFormat.Png);
                    return stream.ToArray();
                }
            }
        }

        private static byte[] GetColorBand(
            IDictionary<ColorInterp, byte[]> bands,
            ColorInterp interpretation,
            IList<byte[]> fallback,
            int fallbackIndex)
        {
            byte[] result;
            if (bands.TryGetValue(interpretation, out result))
            {
                return result;
            }

            return fallbackIndex >= 0 && fallbackIndex < fallback.Count
                ? fallback[fallbackIndex]
                : null;
        }

        private static byte[] RenderTerrainTile(RasterContext context, GeoBounds bounds)
        {
            SourceWindow window;
            if (!TryGetSourceWindow(context, bounds, TerrainTileSize, TerrainTileSize, out window))
            {
                return null;
            }

            Band band = context.Dataset.GetRasterBand(1);
            var sourceValues = new float[window.DestinationWidth * window.DestinationHeight];
            CPLErr result = band.ReadRaster(
                window.SourceX,
                window.SourceY,
                window.SourceWidth,
                window.SourceHeight,
                sourceValues,
                window.DestinationWidth,
                window.DestinationHeight,
                0,
                0);
            if (result != CPLErr.CE_None)
            {
                return null;
            }

            double noData;
            int hasNoData;
            band.GetNoDataValue(out noData, out hasNoData);
            double scale;
            int hasScale;
            band.GetScale(out scale, out hasScale);
            double offset;
            int hasOffset;
            band.GetOffset(out offset, out hasOffset);
            if (hasScale == 0) scale = 1;
            if (hasOffset == 0) offset = 0;
            string units = band.GetUnitType() ?? string.Empty;
            double unitScale = units.IndexOf("foot", StringComparison.OrdinalIgnoreCase) >= 0 ||
                               string.Equals(units, "ft", StringComparison.OrdinalIgnoreCase)
                ? 0.3048
                : 1.0;

            var heights = Enumerable.Repeat(float.NaN, TerrainTileSize * TerrainTileSize).ToArray();
            int validCount = 0;
            for (int row = 0; row < window.DestinationHeight; row++)
            {
                for (int column = 0; column < window.DestinationWidth; column++)
                {
                    int sourceIndex = row * window.DestinationWidth + column;
                    float raw = sourceValues[sourceIndex];
                    if (float.IsNaN(raw) || float.IsInfinity(raw) ||
                        hasNoData != 0 && Math.Abs(raw - noData) < 0.0001)
                    {
                        continue;
                    }

                    double height = (raw * scale + offset) * unitScale;
                    if (height < -12000 || height > 100000)
                    {
                        continue;
                    }

                    int destination = (window.DestinationY + row) * TerrainTileSize +
                                      window.DestinationX + column;
                    heights[destination] = (float)height;
                    validCount++;
                }
            }

            if (validCount == 0)
            {
                return null;
            }

            var bytes = new byte[heights.Length * sizeof(float)];
            Buffer.BlockCopy(heights, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        private static bool TryGetSourceWindow(
            RasterContext context,
            GeoBounds bounds,
            int outputWidth,
            int outputHeight,
            out SourceWindow window)
        {
            window = default(SourceWindow);
            double[][] geoCorners =
            {
                new[] { bounds.West, bounds.North, 0 },
                new[] { bounds.East, bounds.North, 0 },
                new[] { bounds.West, bounds.South, 0 },
                new[] { bounds.East, bounds.South, 0 }
            };
            var pixelCorners = new List<double[]>();
            foreach (double[] point in geoCorners)
            {
                context.WgsToSource.TransformPoint(point);
                double pixelX;
                double pixelY;
                Gdal.ApplyGeoTransform(context.InverseTransform, point[0], point[1],
                    out pixelX, out pixelY);
                if (!double.IsNaN(pixelX) && !double.IsInfinity(pixelX) &&
                    !double.IsNaN(pixelY) && !double.IsInfinity(pixelY))
                {
                    pixelCorners.Add(new[] { pixelX, pixelY });
                }
            }

            if (pixelCorners.Count != geoCorners.Length)
            {
                return false;
            }

            double rawLeft = pixelCorners.Min(point => point[0]);
            double rawRight = pixelCorners.Max(point => point[0]);
            double rawTop = pixelCorners.Min(point => point[1]);
            double rawBottom = pixelCorners.Max(point => point[1]);
            if (rawRight - rawLeft < 0.0001 || rawBottom - rawTop < 0.0001)
            {
                return false;
            }

            double clippedLeft = Math.Max(0, rawLeft);
            double clippedRight = Math.Min(context.Dataset.RasterXSize, rawRight);
            double clippedTop = Math.Max(0, rawTop);
            double clippedBottom = Math.Min(context.Dataset.RasterYSize, rawBottom);
            if (clippedRight <= clippedLeft || clippedBottom <= clippedTop)
            {
                return false;
            }

            int sourceX = Math.Max(0, (int)Math.Floor(clippedLeft));
            int sourceY = Math.Max(0, (int)Math.Floor(clippedTop));
            int sourceRight = Math.Min(context.Dataset.RasterXSize, (int)Math.Ceiling(clippedRight));
            int sourceBottom = Math.Min(context.Dataset.RasterYSize, (int)Math.Ceiling(clippedBottom));
            int destinationX = Math.Max(0, Math.Min(outputWidth - 1, (int)Math.Round(
                (clippedLeft - rawLeft) / (rawRight - rawLeft) * outputWidth)));
            int destinationY = Math.Max(0, Math.Min(outputHeight - 1, (int)Math.Round(
                (clippedTop - rawTop) / (rawBottom - rawTop) * outputHeight)));
            int destinationRight = Math.Max(destinationX + 1, Math.Min(outputWidth, (int)Math.Round(
                (clippedRight - rawLeft) / (rawRight - rawLeft) * outputWidth)));
            int destinationBottom = Math.Max(destinationY + 1, Math.Min(outputHeight, (int)Math.Round(
                (clippedBottom - rawTop) / (rawBottom - rawTop) * outputHeight)));

            window = new SourceWindow
            {
                SourceX = sourceX,
                SourceY = sourceY,
                SourceWidth = Math.Max(1, sourceRight - sourceX),
                SourceHeight = Math.Max(1, sourceBottom - sourceY),
                DestinationX = destinationX,
                DestinationY = destinationY,
                DestinationWidth = Math.Max(1, destinationRight - destinationX),
                DestinationHeight = Math.Max(1, destinationBottom - destinationY)
            };
            return true;
        }

        private static GeoBounds GetWebMercatorTileBounds(int zoom, int x, int y)
        {
            double tileCount = Math.Pow(2, zoom);
            double west = x / tileCount * 360.0 - 180.0;
            double east = (x + 1) / tileCount * 360.0 - 180.0;
            double north = TileYToLatitude(y, tileCount);
            double south = TileYToLatitude(y + 1, tileCount);
            return new GeoBounds(west, south, east, north);
        }

        private static double TileYToLatitude(double y, double tileCount)
        {
            double mercator = Math.PI * (1 - 2 * y / tileCount);
            return Math.Atan(Math.Sinh(mercator)) * 180.0 / Math.PI;
        }

        private static GeoBounds GetGeographicTileBounds(int level, int x, int y)
        {
            int xTiles = 2 << level;
            int yTiles = 1 << level;
            double width = 360.0 / xTiles;
            double height = 180.0 / yTiles;
            double west = -180.0 + x * width;
            double east = west + width;
            double north = 90.0 - y * height;
            double south = north - height;
            return new GeoBounds(west, south, east, north);
        }

        private static bool IsValidTile(int zoom, int x, int y)
        {
            int count = 1 << zoom;
            return x >= 0 && y >= 0 && x < count && y < count;
        }

        private static bool IsValidGeographicTile(int level, int x, int y)
        {
            return x >= 0 && y >= 0 && x < (2 << level) && y < (1 << level);
        }

        private static void WriteCacheFile(string path, byte[] contents)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
                File.WriteAllBytes(temporary, contents);
                if (File.Exists(path))
                {
                    File.Delete(temporary);
                }
                else
                {
                    File.Move(temporary, path);
                }
            }
            catch
            {
                // A cache write failure must not prevent the map tile from rendering.
            }
        }

        private struct GeoBounds
        {
            internal readonly double West;
            internal readonly double South;
            internal readonly double East;
            internal readonly double North;

            internal GeoBounds(double west, double south, double east, double north)
            {
                West = west;
                South = south;
                East = east;
                North = north;
            }
        }

        private struct SourceWindow
        {
            internal int SourceX;
            internal int SourceY;
            internal int SourceWidth;
            internal int SourceHeight;
            internal int DestinationX;
            internal int DestinationY;
            internal int DestinationWidth;
            internal int DestinationHeight;
        }

        private sealed class RasterContext : IDisposable
        {
            private readonly long sourceLength;
            private readonly DateTime sourceWriteTimeUtc;

            internal object SyncRoot { get; } = new object();
            internal string SourcePath { get; }
            internal Dataset Dataset { get; }
            internal double[] InverseTransform { get; }
            internal CoordinateTransformation WgsToSource { get; }
            internal CoordinateTransformation SourceToWgs { get; }
            internal Map3DRasterInfo Info { get; }

            internal RasterContext(
                string sourcePath,
                Dataset dataset,
                double[] inverseTransform,
                CoordinateTransformation wgsToSource,
                CoordinateTransformation sourceToWgs,
                Map3DRasterInfo info)
            {
                SourcePath = Path.GetFullPath(sourcePath);
                Dataset = dataset;
                InverseTransform = inverseTransform;
                WgsToSource = wgsToSource;
                SourceToWgs = sourceToWgs;
                Info = info;
                var file = new FileInfo(SourcePath);
                sourceLength = file.Length;
                sourceWriteTimeUtc = file.LastWriteTimeUtc;
            }

            internal bool MatchesSource()
            {
                try
                {
                    var file = new FileInfo(SourcePath);
                    return file.Exists && file.Length == sourceLength &&
                           file.LastWriteTimeUtc == sourceWriteTimeUtc;
                }
                catch
                {
                    return false;
                }
            }

            public void Dispose()
            {
                WgsToSource?.Dispose();
                SourceToWgs?.Dispose();
                Dataset?.Dispose();
            }
        }
    }
}
