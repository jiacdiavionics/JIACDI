using MissionPlanner.Utilities;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace MissionPlanner.GCSViews
{
    internal sealed class Map3DOfflinePreparationResult
    {
        public int TerrainTilesReady { get; set; }
        public int TerrainTilesQueued { get; set; }
        public int CachedMapTiles { get; set; }

        public string ToStatusText()
        {
            string terrain = TerrainTilesQueued == 0
                ? TerrainTilesReady + " terrain tile(s) ready"
                : TerrainTilesReady + " terrain tile(s) ready, " + TerrainTilesQueued + " queued";
            return "Offline area saved: " + terrain + ", " + CachedMapTiles + " viewed map tile(s) found";
        }
    }

    internal static class Map3DOfflineRegionCache
    {
        private const int MinimumCoverageZoom = 12;
        private const int MaximumCoverageZoom = 16;
        private static readonly object SyncRoot = new object();
        private static readonly Dictionary<string, DateTime> QueuedTerrainTiles =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        internal static Map3DOfflinePreparationResult PrepareArea(
            double latitude,
            double longitude,
            double radiusKilometers,
            string mapProvider)
        {
            ValidateCoordinates(latitude, longitude);
            radiusKilometers = Math.Max(0.5, Math.Min(100, radiusKilometers));

            string terrainDirectory = Path.Combine(Settings.GetDataDirectory(), "srtm");
            Directory.CreateDirectory(terrainDirectory);
            srtm.datadirectory = terrainDirectory;

            Map3DOfflinePreparationResult result = new Map3DOfflinePreparationResult();
            foreach (TerrainTile tile in GetTerrainTiles(latitude, longitude, radiusKilometers))
            {
                string path = Path.Combine(terrainDirectory, tile.FileName);
                if (File.Exists(path) && new FileInfo(path).Length > 0)
                {
                    result.TerrainTilesReady++;
                    continue;
                }

                if (QueueTerrainTile(tile))
                {
                    result.TerrainTilesQueued++;
                }
            }

            result.CachedMapTiles = CountCachedMapTiles(
                latitude,
                longitude,
                radiusKilometers,
                mapProvider);
            SaveRegion(latitude, longitude, radiusKilometers, mapProvider, result);
            return result;
        }

        internal static void RememberVisitedLocation(double latitude, double longitude)
        {
            ValidateCoordinates(latitude, longitude);
            TerrainTile tile = CreateTerrainTile(
                Math.Max(-90, Math.Min(89, (int)Math.Floor(latitude))),
                Math.Max(-180, Math.Min(179, (int)Math.Floor(longitude))));
            string path = Path.Combine(Settings.GetDataDirectory(), "srtm", tile.FileName);
            if (!File.Exists(path))
            {
                QueueTerrainTile(tile);
            }
        }

        internal static string GetTerrainTileName(int latitudeDegree, int longitudeDegree)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}{1:00}{2}{3:000}.hgt",
                latitudeDegree >= 0 ? "N" : "S",
                Math.Abs(latitudeDegree),
                longitudeDegree >= 0 ? "E" : "W",
                Math.Abs(longitudeDegree));
        }

        private static bool QueueTerrainTile(TerrainTile tile)
        {
            lock (SyncRoot)
            {
                DateTime queuedAt;
                if (QueuedTerrainTiles.TryGetValue(tile.FileName, out queuedAt) &&
                    queuedAt.AddMinutes(10) > DateTime.UtcNow)
                {
                    return false;
                }

                QueuedTerrainTiles[tile.FileName] = DateTime.UtcNow;
            }

            srtm.getAltitude(tile.LatitudeDegree + 0.5, tile.LongitudeDegree + 0.5, 16);
            return true;
        }

        private static IEnumerable<TerrainTile> GetTerrainTiles(
            double latitude,
            double longitude,
            double radiusKilometers)
        {
            double latitudeDelta = radiusKilometers / 110.574;
            double longitudeScale = Math.Max(0.05, Math.Cos(latitude * Math.PI / 180.0));
            double longitudeDelta = radiusKilometers / (111.320 * longitudeScale);
            int minimumLatitude = Math.Max(-89, (int)Math.Floor(latitude - latitudeDelta));
            int maximumLatitude = Math.Min(89, (int)Math.Floor(latitude + latitudeDelta));
            int minimumLongitude = Math.Max(-180, (int)Math.Floor(longitude - longitudeDelta));
            int maximumLongitude = Math.Min(179, (int)Math.Floor(longitude + longitudeDelta));

            for (int lat = minimumLatitude; lat <= maximumLatitude; lat++)
            {
                for (int lng = minimumLongitude; lng <= maximumLongitude; lng++)
                {
                    yield return CreateTerrainTile(lat, lng);
                }
            }
        }

        private static TerrainTile CreateTerrainTile(int latitudeDegree, int longitudeDegree)
        {
            return new TerrainTile
            {
                LatitudeDegree = latitudeDegree,
                LongitudeDegree = longitudeDegree,
                FileName = GetTerrainTileName(latitudeDegree, longitudeDegree)
            };
        }

        private static int CountCachedMapTiles(
            double latitude,
            double longitude,
            double radiusKilometers,
            string mapProvider)
        {
            mapProvider = GetSafeProviderName(mapProvider);
            if (string.IsNullOrEmpty(mapProvider))
            {
                return 0;
            }

            string root = Path.Combine(
                Settings.GetDataDirectory(),
                "gmapcache",
                "TileDBv3",
                "en",
                mapProvider);
            if (!Directory.Exists(root))
            {
                return 0;
            }

            double latitudeDelta = radiusKilometers / 110.574;
            double longitudeScale = Math.Max(0.05, Math.Cos(latitude * Math.PI / 180.0));
            double longitudeDelta = radiusKilometers / (111.320 * longitudeScale);
            double north = Math.Min(85.05112878, latitude + latitudeDelta);
            double south = Math.Max(-85.05112878, latitude - latitudeDelta);
            double west = Math.Max(-180, longitude - longitudeDelta);
            double east = Math.Min(180, longitude + longitudeDelta);
            int count = 0;

            for (int zoom = MinimumCoverageZoom; zoom <= MaximumCoverageZoom; zoom++)
            {
                int minimumX = LongitudeToTileX(west, zoom);
                int maximumX = LongitudeToTileX(east, zoom);
                int minimumY = LatitudeToTileY(north, zoom);
                int maximumY = LatitudeToTileY(south, zoom);

                for (int y = minimumY; y <= maximumY; y++)
                {
                    for (int x = minimumX; x <= maximumX; x++)
                    {
                        if (File.Exists(Path.Combine(root, zoom.ToString(CultureInfo.InvariantCulture),
                            y.ToString(CultureInfo.InvariantCulture), x.ToString(CultureInfo.InvariantCulture) + ".jpg")))
                        {
                            count++;
                        }
                    }
                }
            }

            return count;
        }

        private static int LongitudeToTileX(double longitude, int zoom)
        {
            int tileCount = 1 << zoom;
            int value = (int)Math.Floor((longitude + 180.0) / 360.0 * tileCount);
            return Math.Max(0, Math.Min(tileCount - 1, value));
        }

        private static int LatitudeToTileY(double latitude, int zoom)
        {
            latitude = Math.Max(-85.05112878, Math.Min(85.05112878, latitude));
            double latitudeRadians = latitude * Math.PI / 180.0;
            int tileCount = 1 << zoom;
            int value = (int)Math.Floor((1.0 -
                Math.Log(Math.Tan(latitudeRadians) + 1.0 / Math.Cos(latitudeRadians)) / Math.PI) /
                2.0 * tileCount);
            return Math.Max(0, Math.Min(tileCount - 1, value));
        }

        private static void SaveRegion(
            double latitude,
            double longitude,
            double radiusKilometers,
            string mapProvider,
            Map3DOfflinePreparationResult result)
        {
            string directory = Path.Combine(Settings.GetDataDirectory(), "map3d");
            string path = Path.Combine(directory, "offline-regions.json");
            Directory.CreateDirectory(directory);

            lock (SyncRoot)
            {
                List<OfflineRegionRecord> regions = LoadRegions(path);
                OfflineRegionRecord existing = regions.FirstOrDefault(region =>
                    Math.Abs(region.Latitude - latitude) < 0.0001 &&
                    Math.Abs(region.Longitude - longitude) < 0.0001 &&
                    Math.Abs(region.RadiusKilometers - radiusKilometers) < 0.01);
                if (existing == null)
                {
                    existing = new OfflineRegionRecord();
                    regions.Add(existing);
                }

                existing.Latitude = latitude;
                existing.Longitude = longitude;
                existing.RadiusKilometers = radiusKilometers;
                existing.MapProvider = GetSafeProviderName(mapProvider);
                existing.TerrainTilesReady = result.TerrainTilesReady;
                existing.TerrainTilesQueued = result.TerrainTilesQueued;
                existing.CachedMapTiles = result.CachedMapTiles;
                existing.SavedUtc = DateTime.UtcNow;

                File.WriteAllText(path, JsonConvert.SerializeObject(regions, Formatting.Indented));
            }
        }

        private static List<OfflineRegionRecord> LoadRegions(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    return JsonConvert.DeserializeObject<List<OfflineRegionRecord>>(File.ReadAllText(path)) ??
                           new List<OfflineRegionRecord>();
                }
            }
            catch
            {
            }

            return new List<OfflineRegionRecord>();
        }

        private static string GetSafeProviderName(string mapProvider)
        {
            if (string.IsNullOrWhiteSpace(mapProvider))
            {
                return string.Empty;
            }

            string value = mapProvider.Trim();
            return value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || value == "." || value == ".."
                ? string.Empty
                : value;
        }

        private static void ValidateCoordinates(double latitude, double longitude)
        {
            if (double.IsNaN(latitude) || double.IsInfinity(latitude) || latitude < -90 || latitude > 90 ||
                double.IsNaN(longitude) || double.IsInfinity(longitude) || longitude < -180 || longitude > 180)
            {
                throw new ArgumentOutOfRangeException("latitude", "Map coordinates are outside the valid range.");
            }
        }

        private sealed class TerrainTile
        {
            public int LatitudeDegree { get; set; }
            public int LongitudeDegree { get; set; }
            public string FileName { get; set; }
        }

        private sealed class OfflineRegionRecord
        {
            public double Latitude { get; set; }
            public double Longitude { get; set; }
            public double RadiusKilometers { get; set; }
            public string MapProvider { get; set; }
            public int TerrainTilesReady { get; set; }
            public int TerrainTilesQueued { get; set; }
            public int CachedMapTiles { get; set; }
            public DateTime SavedUtc { get; set; }
        }
    }
}
