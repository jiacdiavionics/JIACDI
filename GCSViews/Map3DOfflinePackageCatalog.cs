using MissionPlanner.Utilities;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;

namespace MissionPlanner.GCSViews
{
    internal enum Map3DRasterImportRole
    {
        Auto,
        Imagery,
        Terrain
    }

    internal static class Map3DPackageKinds
    {
        internal const string RasterImagery = "raster-imagery";
        internal const string RasterTerrain = "raster-terrain";
        internal const string XyzImagery = "xyz-imagery";
        internal const string SrtmHgt = "srtm-hgt";
        internal const string CesiumTerrain = "cesium-terrain";
        internal const string Cesium3DTiles = "3d-tiles";
        internal const string GeoJsonBuildings = "geojson-buildings";
        internal const string Kml = "kml";
    }

    internal sealed class Map3DOfflinePackage
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("kind")]
        public string Kind { get; set; }

        [JsonProperty("relativePath")]
        public string RelativePath { get; set; }

        [JsonProperty("enabled")]
        public bool Enabled { get; set; } = true;

        [JsonProperty("importedUtc")]
        public DateTime ImportedUtc { get; set; }

        [JsonProperty("west", NullValueHandling = NullValueHandling.Ignore)]
        public double? West { get; set; }

        [JsonProperty("south", NullValueHandling = NullValueHandling.Ignore)]
        public double? South { get; set; }

        [JsonProperty("east", NullValueHandling = NullValueHandling.Ignore)]
        public double? East { get; set; }

        [JsonProperty("north", NullValueHandling = NullValueHandling.Ignore)]
        public double? North { get; set; }

        [JsonProperty("minZoom", NullValueHandling = NullValueHandling.Ignore)]
        public int? MinZoom { get; set; }

        [JsonProperty("maxZoom", NullValueHandling = NullValueHandling.Ignore)]
        public int? MaxZoom { get; set; }

        [JsonProperty("tileScheme", NullValueHandling = NullValueHandling.Ignore)]
        public string TileScheme { get; set; }

        [JsonIgnore]
        internal bool HasBounds => West.HasValue && South.HasValue && East.HasValue && North.HasValue;

        internal Map3DOfflinePackage Clone()
        {
            return (Map3DOfflinePackage)MemberwiseClone();
        }
    }

    internal static class Map3DOfflinePackageCatalog
    {
        private const long MaximumArchiveBytes = 50L * 1024 * 1024 * 1024;
        private const int MaximumArchiveEntries = 250000;
        private const long Srtm3FileBytes = 1201L * 1201L * 2L;
        private const long Srtm1FileBytes = 3601L * 3601L * 2L;
        private static readonly object SyncRoot = new object();
        private static readonly Regex HgtNamePattern = new Regex(
            "^[NS]\\d{2}[EW]\\d{3}\\.hgt$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static List<Map3DOfflinePackage> packages;

        internal static event EventHandler CatalogChanged;

        internal static string RootDirectory => Path.Combine(
            Settings.GetDataDirectory(),
            "map3d",
            "imports");

        private static string CatalogPath => Path.Combine(RootDirectory, "catalog.json");

        internal static IReadOnlyList<Map3DOfflinePackage> GetPackages(bool enabledOnly = false)
        {
            lock (SyncRoot)
            {
                EnsureLoaded();
                return packages
                    .Where(package => !enabledOnly || package.Enabled)
                    .Select(package => package.Clone())
                    .OrderByDescending(package => package.ImportedUtc)
                    .ToArray();
            }
        }

        internal static Map3DOfflinePackage GetPackage(string id, bool requireEnabled = true)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            lock (SyncRoot)
            {
                EnsureLoaded();
                Map3DOfflinePackage package = packages.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase) &&
                    (!requireEnabled || candidate.Enabled));
                return package?.Clone();
            }
        }

        internal static Map3DOfflinePackage ImportFile(
            string sourcePath,
            Map3DRasterImportRole rasterRole = Map3DRasterImportRole.Auto)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                throw new FileNotFoundException("The selected offline map file was not found.", sourcePath);
            }

            string extension = Path.GetExtension(sourcePath).ToLowerInvariant();
            switch (extension)
            {
                case ".hgt":
                    return ImportHgtFiles(new[] { sourcePath }, Path.GetFileNameWithoutExtension(sourcePath));
                case ".zip":
                    return ImportArchive(sourcePath, rasterRole);
                case ".geojson":
                    return ImportSingleFile(sourcePath, Map3DPackageKinds.GeoJsonBuildings);
                case ".kml":
                case ".kmz":
                    return ImportSingleFile(sourcePath, Map3DPackageKinds.Kml);
                case ".json":
                    return ImportJsonFile(sourcePath);
                default:
                    return ImportRaster(sourcePath, rasterRole);
            }
        }

        internal static Map3DOfflinePackage ImportFolder(string sourceDirectory)
        {
            if (string.IsNullOrWhiteSpace(sourceDirectory) || !Directory.Exists(sourceDirectory))
            {
                throw new DirectoryNotFoundException("The selected offline map folder was not found.");
            }

            string[] hgtFiles = Directory.GetFiles(sourceDirectory, "*.hgt", SearchOption.AllDirectories);
            string layerJson = FindNamedFile(sourceDirectory, "layer.json");
            string tilesetJson = FindNamedFile(sourceDirectory, "tileset.json");
            if (!string.IsNullOrEmpty(layerJson))
            {
                return ImportDirectoryPackage(sourceDirectory, layerJson,
                    Map3DPackageKinds.CesiumTerrain);
            }
            if (!string.IsNullOrEmpty(tilesetJson))
            {
                return ImportDirectoryPackage(sourceDirectory, tilesetJson,
                    Map3DPackageKinds.Cesium3DTiles);
            }
            if (hgtFiles.Length > 0)
            {
                return ImportHgtFiles(hgtFiles, new DirectoryInfo(sourceDirectory).Name);
            }
            if (LooksLikeXyzDirectory(sourceDirectory))
            {
                return ImportXyzDirectory(sourceDirectory);
            }

            throw new InvalidDataException(
                "No supported offline map package was found. Select a folder containing layer.json, " +
                "tileset.json, HGT terrain files, or an XYZ z/x/y image tile tree.");
        }

        internal static void SetEnabled(string id, bool enabled)
        {
            lock (SyncRoot)
            {
                EnsureLoaded();
                Map3DOfflinePackage package = packages.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
                if (package == null || package.Enabled == enabled)
                {
                    return;
                }

                package.Enabled = enabled;
                Save();
            }

            Map3DRasterTileService.Invalidate();
            CatalogChanged?.Invoke(null, EventArgs.Empty);
        }

        internal static void Remove(string id)
        {
            string packageDirectory;
            lock (SyncRoot)
            {
                EnsureLoaded();
                Map3DOfflinePackage package = packages.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
                if (package == null)
                {
                    return;
                }

                packageDirectory = GetPackageDirectory(package);
                packages.Remove(package);
                Save();
            }

            Map3DRasterTileService.Invalidate();
            if (IsPathUnderRoot(packageDirectory, RootDirectory) && Directory.Exists(packageDirectory))
            {
                Directory.Delete(packageDirectory, true);
            }
            CatalogChanged?.Invoke(null, EventArgs.Empty);
        }

        internal static string ResolvePackageResource(string packageId, string relativePath)
        {
            Map3DOfflinePackage package = GetPackage(packageId);
            if (package == null || string.IsNullOrWhiteSpace(relativePath))
            {
                return null;
            }

            string root = GetPackageDirectory(package);
            return ResolveUnderRoot(root, relativePath);
        }

        internal static string ResolveHgtFile(string fileName)
        {
            string safeName = Path.GetFileName(fileName ?? string.Empty);
            if (!HgtNamePattern.IsMatch(safeName))
            {
                return null;
            }

            foreach (Map3DOfflinePackage package in GetPackages(true)
                         .Where(package => package.Kind == Map3DPackageKinds.SrtmHgt))
            {
                string root = GetPackageDataPath(package);
                string match = Directory.Exists(root)
                    ? Directory.GetFiles(root, safeName, SearchOption.AllDirectories)
                        .FirstOrDefault(IsValidHgtFile)
                    : null;
                if (!string.IsNullOrEmpty(match))
                {
                    return match;
                }
            }

            return null;
        }

        internal static string ResolveXyzTile(Map3DOfflinePackage package, int zoom, int x, int y)
        {
            if (package == null || package.Kind != Map3DPackageKinds.XyzImagery ||
                zoom < 0 || zoom > 22)
            {
                return null;
            }

            int count = 1 << zoom;
            if (x < 0 || x >= count || y < 0 || y >= count)
            {
                return null;
            }
            if (string.Equals(package.TileScheme, "tms", StringComparison.OrdinalIgnoreCase))
            {
                y = count - 1 - y;
            }

            string directory = Path.Combine(
                GetPackageDataPath(package),
                zoom.ToString(),
                x.ToString());
            if (!Directory.Exists(directory))
            {
                return null;
            }

            string[] extensions = { ".png", ".jpg", ".jpeg", ".webp" };
            return extensions.Select(extension => Path.Combine(directory, y + extension))
                .FirstOrDefault(File.Exists);
        }

        internal static string GetPackageDataPath(Map3DOfflinePackage package)
        {
            if (package == null)
            {
                return null;
            }

            return ResolveUnderRoot(GetPackageDirectory(package), package.RelativePath);
        }

        internal static string GetPackageCachePath(
            Map3DOfflinePackage package,
            string cacheKind,
            int level,
            int x,
            int y,
            string extension)
        {
            string root = Path.Combine(GetPackageDirectory(package), "cache", cacheKind);
            return Path.Combine(root, level.ToString(), x.ToString(), y + extension);
        }

        internal static string GetPackageDirectory(Map3DOfflinePackage package)
        {
            if (package == null || string.IsNullOrWhiteSpace(package.Id) ||
                package.Id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                package.Id == "." || package.Id == "..")
            {
                return null;
            }

            return ResolveUnderRoot(RootDirectory, package.Id);
        }

        private static Map3DOfflinePackage ImportRaster(
            string sourcePath,
            Map3DRasterImportRole role)
        {
            Map3DRasterInfo info = Map3DRasterTileService.Inspect(sourcePath);
            if (role == Map3DRasterImportRole.Auto)
            {
                role = info.LooksLikeTerrain
                    ? Map3DRasterImportRole.Terrain
                    : Map3DRasterImportRole.Imagery;
            }

            string id = CreatePackageId(Path.GetFileNameWithoutExtension(sourcePath));
            string directory = CreatePackageDirectory(id);
            string destination = Path.Combine(directory, "data", MakeSafeFileName(Path.GetFileName(sourcePath)));
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            File.Copy(sourcePath, destination, false);

            var package = new Map3DOfflinePackage
            {
                Id = id,
                Name = Path.GetFileNameWithoutExtension(sourcePath),
                Kind = role == Map3DRasterImportRole.Terrain
                    ? Map3DPackageKinds.RasterTerrain
                    : Map3DPackageKinds.RasterImagery,
                RelativePath = MakeRelativePath(directory, destination),
                ImportedUtc = DateTime.UtcNow,
                Enabled = true,
                West = info.West,
                South = info.South,
                East = info.East,
                North = info.North,
                MinZoom = role == Map3DRasterImportRole.Terrain ? 5 : 0,
                MaxZoom = role == Map3DRasterImportRole.Terrain ? 18 : 22
            };
            AddPackage(package);
            return package.Clone();
        }

        private static Map3DOfflinePackage ImportSingleFile(string sourcePath, string kind)
        {
            string id = CreatePackageId(Path.GetFileNameWithoutExtension(sourcePath));
            string directory = CreatePackageDirectory(id);
            string destination = Path.Combine(directory, "data", MakeSafeFileName(Path.GetFileName(sourcePath)));
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            File.Copy(sourcePath, destination, false);
            var package = new Map3DOfflinePackage
            {
                Id = id,
                Name = Path.GetFileNameWithoutExtension(sourcePath),
                Kind = kind,
                RelativePath = MakeRelativePath(directory, destination),
                ImportedUtc = DateTime.UtcNow,
                Enabled = true
            };
            AddPackage(package);
            return package.Clone();
        }

        private static Map3DOfflinePackage ImportJsonFile(string sourcePath)
        {
            string name = Path.GetFileName(sourcePath);
            if (string.Equals(name, "tileset.json", StringComparison.OrdinalIgnoreCase))
            {
                return ImportDirectoryPackage(Path.GetDirectoryName(sourcePath), sourcePath,
                    Map3DPackageKinds.Cesium3DTiles);
            }
            if (string.Equals(name, "layer.json", StringComparison.OrdinalIgnoreCase))
            {
                return ImportDirectoryPackage(Path.GetDirectoryName(sourcePath), sourcePath,
                    Map3DPackageKinds.CesiumTerrain);
            }

            using (var reader = File.OpenText(sourcePath))
            using (var jsonReader = new JsonTextReader(reader))
            {
                JObject document = JObject.Load(jsonReader);
                if (string.Equals(document["type"]?.Value<string>(), "FeatureCollection",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return ImportSingleFile(sourcePath, Map3DPackageKinds.GeoJsonBuildings);
                }
                if (document["asset"] != null && document["root"] != null)
                {
                    return ImportDirectoryPackage(Path.GetDirectoryName(sourcePath), sourcePath,
                        Map3DPackageKinds.Cesium3DTiles);
                }
                if (document["format"] != null && document["tiles"] != null)
                {
                    return ImportDirectoryPackage(Path.GetDirectoryName(sourcePath), sourcePath,
                        Map3DPackageKinds.CesiumTerrain);
                }
            }

            throw new InvalidDataException(
                "The JSON file is not GeoJSON, a Cesium 3D Tiles tileset, or a Cesium terrain layer.");
        }

        private static Map3DOfflinePackage ImportDirectoryPackage(
            string sourceDirectory,
            string descriptorPath,
            string kind)
        {
            string id = CreatePackageId(new DirectoryInfo(sourceDirectory).Name);
            string directory = CreatePackageDirectory(id);
            string dataDirectory = Path.Combine(directory, "data");
            CopyDirectory(sourceDirectory, dataDirectory);
            string descriptorRelative = MakeRelativePath(sourceDirectory, descriptorPath);
            string destinationDescriptor = ResolveUnderRoot(dataDirectory, descriptorRelative);
            var package = new Map3DOfflinePackage
            {
                Id = id,
                Name = new DirectoryInfo(sourceDirectory).Name,
                Kind = kind,
                RelativePath = MakeRelativePath(directory, destinationDescriptor),
                ImportedUtc = DateTime.UtcNow,
                Enabled = true
            };
            AddPackage(package);
            return package.Clone();
        }

        private static Map3DOfflinePackage ImportHgtFiles(IEnumerable<string> files, string name)
        {
            string[] validFiles = files
                .Where(IsValidHgtFile)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (validFiles.Length == 0)
            {
                throw new InvalidDataException(
                    "No valid SRTM HGT files were found. Expected names such as N31E035.hgt.");
            }

            string id = CreatePackageId(name);
            string directory = CreatePackageDirectory(id);
            string dataDirectory = Path.Combine(directory, "data");
            Directory.CreateDirectory(dataDirectory);
            foreach (string file in validFiles)
            {
                File.Copy(file, Path.Combine(dataDirectory, Path.GetFileName(file)), false);
            }

            var package = new Map3DOfflinePackage
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(name) ? "SRTM terrain" : name,
                Kind = Map3DPackageKinds.SrtmHgt,
                RelativePath = "data",
                ImportedUtc = DateTime.UtcNow,
                Enabled = true
            };
            ApplyHgtBounds(package, validFiles.Select(Path.GetFileName));
            AddPackage(package);
            return package.Clone();
        }

        internal static bool IsValidHgtFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) ||
                !HgtNamePattern.IsMatch(Path.GetFileName(path)))
            {
                return false;
            }

            long length = new FileInfo(path).Length;
            return length == Srtm3FileBytes || length == Srtm1FileBytes;
        }

        private static Map3DOfflinePackage ImportArchive(
            string sourcePath,
            Map3DRasterImportRole rasterRole)
        {
            string id = CreatePackageId(Path.GetFileNameWithoutExtension(sourcePath));
            string directory = CreatePackageDirectory(id);
            string dataDirectory = Path.Combine(directory, "data");
            Directory.CreateDirectory(dataDirectory);
            try
            {
                ExtractArchive(sourcePath, dataDirectory);
                string layerJson = FindNamedFile(dataDirectory, "layer.json");
                string tilesetJson = FindNamedFile(dataDirectory, "tileset.json");
                string[] hgtFiles = Directory.GetFiles(dataDirectory, "*.hgt", SearchOption.AllDirectories)
                    .Where(IsValidHgtFile)
                    .ToArray();
                string geoJson = Directory.GetFiles(dataDirectory, "*.geojson", SearchOption.AllDirectories)
                    .FirstOrDefault();
                string kml = Directory.GetFiles(dataDirectory, "*.kml", SearchOption.AllDirectories)
                    .FirstOrDefault();

                var package = new Map3DOfflinePackage
                {
                    Id = id,
                    Name = Path.GetFileNameWithoutExtension(sourcePath),
                    ImportedUtc = DateTime.UtcNow,
                    Enabled = true
                };
                if (!string.IsNullOrEmpty(layerJson))
                {
                    package.Kind = Map3DPackageKinds.CesiumTerrain;
                    package.RelativePath = MakeRelativePath(directory, layerJson);
                }
                else if (!string.IsNullOrEmpty(tilesetJson))
                {
                    package.Kind = Map3DPackageKinds.Cesium3DTiles;
                    package.RelativePath = MakeRelativePath(directory, tilesetJson);
                }
                else if (hgtFiles.Length > 0)
                {
                    package.Kind = Map3DPackageKinds.SrtmHgt;
                    package.RelativePath = "data";
                    ApplyHgtBounds(package, hgtFiles.Select(Path.GetFileName));
                }
                else if (!string.IsNullOrEmpty(geoJson))
                {
                    package.Kind = Map3DPackageKinds.GeoJsonBuildings;
                    package.RelativePath = MakeRelativePath(directory, geoJson);
                }
                else if (!string.IsNullOrEmpty(kml))
                {
                    package.Kind = Map3DPackageKinds.Kml;
                    package.RelativePath = MakeRelativePath(directory, kml);
                }
                else if (LooksLikeXyzDirectory(dataDirectory))
                {
                    ConfigureXyzPackage(package, directory, FindXyzRoot(dataDirectory));
                }
                else
                {
                    string raster = Directory.GetFiles(dataDirectory, "*.*", SearchOption.AllDirectories)
                        .FirstOrDefault(file => IsLikelyRasterExtension(Path.GetExtension(file)));
                    if (raster == null)
                    {
                        throw new InvalidDataException("The archive contains no supported offline map data.");
                    }

                    Map3DRasterInfo info = Map3DRasterTileService.Inspect(raster);
                    Map3DRasterImportRole resolvedRole = rasterRole == Map3DRasterImportRole.Auto
                        ? (info.LooksLikeTerrain
                            ? Map3DRasterImportRole.Terrain
                            : Map3DRasterImportRole.Imagery)
                        : rasterRole;
                    package.Kind = resolvedRole == Map3DRasterImportRole.Terrain
                        ? Map3DPackageKinds.RasterTerrain
                        : Map3DPackageKinds.RasterImagery;
                    package.RelativePath = MakeRelativePath(directory, raster);
                    package.West = info.West;
                    package.South = info.South;
                    package.East = info.East;
                    package.North = info.North;
                }

                AddPackage(package);
                return package.Clone();
            }
            catch
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }
                throw;
            }
        }

        private static Map3DOfflinePackage ImportXyzDirectory(string sourceDirectory)
        {
            string id = CreatePackageId(new DirectoryInfo(sourceDirectory).Name);
            string directory = CreatePackageDirectory(id);
            string dataDirectory = Path.Combine(directory, "data");
            CopyDirectory(sourceDirectory, dataDirectory);
            var package = new Map3DOfflinePackage
            {
                Id = id,
                Name = new DirectoryInfo(sourceDirectory).Name,
                ImportedUtc = DateTime.UtcNow,
                Enabled = true
            };
            ConfigureXyzPackage(package, directory, FindXyzRoot(dataDirectory));
            AddPackage(package);
            return package.Clone();
        }

        private static void ConfigureXyzPackage(
            Map3DOfflinePackage package,
            string packageDirectory,
            string xyzRoot)
        {
            int[] zooms = Directory.GetDirectories(xyzRoot)
                .Select(Path.GetFileName)
                .Select(value =>
                {
                    int parsed;
                    return int.TryParse(value, out parsed) ? parsed : -1;
                })
                .Where(value => value >= 0 && value <= 22)
                .ToArray();
            package.Kind = Map3DPackageKinds.XyzImagery;
            package.RelativePath = MakeRelativePath(packageDirectory, xyzRoot);
            package.TileScheme = "xyz";
            package.MinZoom = zooms.Length == 0 ? 0 : zooms.Min();
            package.MaxZoom = zooms.Length == 0 ? 22 : zooms.Max();
        }

        private static void ApplyHgtBounds(Map3DOfflinePackage package, IEnumerable<string> fileNames)
        {
            var coordinates = new List<Tuple<int, int>>();
            foreach (string fileName in fileNames)
            {
                Match match = Regex.Match(Path.GetFileName(fileName),
                    "^(?<ns>[NS])(?<lat>\\d{2})(?<ew>[EW])(?<lng>\\d{3})\\.hgt$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!match.Success) continue;
                int lat = int.Parse(match.Groups["lat"].Value);
                int lng = int.Parse(match.Groups["lng"].Value);
                if (match.Groups["ns"].Value.Equals("S", StringComparison.OrdinalIgnoreCase)) lat = -lat;
                if (match.Groups["ew"].Value.Equals("W", StringComparison.OrdinalIgnoreCase)) lng = -lng;
                coordinates.Add(Tuple.Create(lat, lng));
            }
            if (coordinates.Count == 0) return;
            package.West = coordinates.Min(value => value.Item2);
            package.South = coordinates.Min(value => value.Item1);
            package.East = coordinates.Max(value => value.Item2) + 1;
            package.North = coordinates.Max(value => value.Item1) + 1;
        }

        private static void AddPackage(Map3DOfflinePackage package)
        {
            lock (SyncRoot)
            {
                EnsureLoaded();
                packages.Add(package);
                Save();
            }
            Map3DRasterTileService.Invalidate();
            CatalogChanged?.Invoke(null, EventArgs.Empty);
        }

        private static void EnsureLoaded()
        {
            if (packages != null)
            {
                return;
            }

            Directory.CreateDirectory(RootDirectory);
            try
            {
                packages = File.Exists(CatalogPath)
                    ? JsonConvert.DeserializeObject<List<Map3DOfflinePackage>>(
                          File.ReadAllText(CatalogPath)) ?? new List<Map3DOfflinePackage>()
                    : new List<Map3DOfflinePackage>();
                packages = packages.Where(IsValidPackageRecord).ToList();
            }
            catch
            {
                packages = new List<Map3DOfflinePackage>();
            }
        }

        private static bool IsValidPackageRecord(Map3DOfflinePackage package)
        {
            return package != null && !string.IsNullOrWhiteSpace(package.Id) &&
                   !string.IsNullOrWhiteSpace(package.Kind) &&
                   !string.IsNullOrWhiteSpace(package.RelativePath) &&
                   GetPackageDirectory(package) != null;
        }

        private static void Save()
        {
            Directory.CreateDirectory(RootDirectory);
            string temporary = CatalogPath + ".tmp";
            File.WriteAllText(temporary, JsonConvert.SerializeObject(packages, Formatting.Indented));
            if (File.Exists(CatalogPath))
            {
                File.Replace(temporary, CatalogPath, null);
            }
            else
            {
                File.Move(temporary, CatalogPath);
            }
        }

        private static string CreatePackageId(string name)
        {
            string safe = Regex.Replace((name ?? "map").ToLowerInvariant(), "[^a-z0-9]+", "-")
                .Trim('-');
            if (string.IsNullOrWhiteSpace(safe)) safe = "map";
            if (safe.Length > 32) safe = safe.Substring(0, 32).TrimEnd('-');
            return safe + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" +
                   Guid.NewGuid().ToString("N").Substring(0, 6);
        }

        private static string CreatePackageDirectory(string id)
        {
            string directory = ResolveUnderRoot(RootDirectory, id);
            if (directory == null)
            {
                throw new InvalidDataException("Unable to create a safe offline map package path.");
            }
            Directory.CreateDirectory(directory);
            return directory;
        }

        private static string MakeSafeFileName(string fileName)
        {
            fileName = Path.GetFileName(fileName ?? string.Empty);
            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                fileName = fileName.Replace(invalid, '_');
            }
            return string.IsNullOrWhiteSpace(fileName) ? "map.dat" : fileName;
        }

        private static string MakeRelativePath(string root, string path)
        {
            string normalizedRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            string normalizedPath = Path.GetFullPath(path);
            if (!normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The package file is outside its package directory.");
            }
            return normalizedPath.Substring(normalizedRoot.Length)
                .Replace(Path.DirectorySeparatorChar, '/');
        }

        internal static string ResolveUnderRoot(string root, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(relativePath) ||
                Path.IsPathRooted(relativePath) || relativePath.IndexOf(':') >= 0)
            {
                return null;
            }
            string[] segments = relativePath.Replace('\\', '/').Split(
                new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0 || segments.Any(segment => segment == "." || segment == ".."))
            {
                return null;
            }
            string normalizedRoot = Path.GetFullPath(root);
            string fullPath = Path.GetFullPath(Path.Combine(normalizedRoot,
                string.Join(Path.DirectorySeparatorChar.ToString(), segments)));
            string rootPrefix = normalizedRoot.TrimEnd(Path.DirectorySeparatorChar,
                                    Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) ? fullPath : null;
        }

        private static bool IsPathUnderRoot(string path, string root)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root)) return false;
            string normalizedRoot = Path.GetFullPath(root).TrimEnd(
                                        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                                    Path.DirectorySeparatorChar;
            string normalizedPath = Path.GetFullPath(path).TrimEnd(
                                        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                                    Path.DirectorySeparatorChar;
            return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
        }

        private static string FindNamedFile(string root, string fileName)
        {
            return Directory.GetFiles(root, fileName, SearchOption.AllDirectories).FirstOrDefault();
        }

        private static bool LooksLikeXyzDirectory(string root)
        {
            return FindXyzRoot(root) != null;
        }

        private static string FindXyzRoot(string root)
        {
            IEnumerable<string> candidates = new[] { root }.Concat(
                Directory.GetDirectories(root, "*", SearchOption.AllDirectories).Take(500));
            foreach (string candidate in candidates)
            {
                string zoomDirectory = Directory.GetDirectories(candidate)
                    .FirstOrDefault(path =>
                    {
                        int zoom;
                        return int.TryParse(Path.GetFileName(path), out zoom) && zoom >= 0 && zoom <= 22;
                    });
                if (zoomDirectory == null) continue;
                string xDirectory = Directory.GetDirectories(zoomDirectory)
                    .FirstOrDefault(path => int.TryParse(Path.GetFileName(path), out _));
                if (xDirectory == null) continue;
                if (Directory.GetFiles(xDirectory).Any(file =>
                        new[] { ".png", ".jpg", ".jpeg", ".webp" }
                            .Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)))
                {
                    return candidate;
                }
            }
            return null;
        }

        private static bool IsLikelyRasterExtension(string extension)
        {
            return new[]
            {
                ".tif", ".tiff", ".mbtiles", ".gpkg", ".dt0", ".dt1", ".dt2",
                ".dem", ".asc", ".bil", ".vrt", ".img"
            }.Contains(extension ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (string directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            {
                var info = new DirectoryInfo(directory);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                Directory.CreateDirectory(Path.Combine(destination, MakeRelativePath(source, directory)));
            }
            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                var info = new FileInfo(file);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                string target = Path.Combine(destination,
                    MakeRelativePath(source, file).Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, false);
            }
        }

        private static void ExtractArchive(string sourcePath, string destination)
        {
            string normalizedDestination = Path.GetFullPath(destination)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            long totalBytes = 0;
            using (ZipArchive archive = ZipFile.OpenRead(sourcePath))
            {
                if (archive.Entries.Count > MaximumArchiveEntries)
                {
                    throw new InvalidDataException("The map archive contains too many files.");
                }
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    totalBytes += entry.Length;
                    if (totalBytes > MaximumArchiveBytes)
                    {
                        throw new InvalidDataException("The extracted map archive is larger than 50 GB.");
                    }
                    string fullPath = Path.GetFullPath(Path.Combine(destination,
                        entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                    if (!fullPath.StartsWith(normalizedDestination, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("The map archive contains an unsafe path.");
                    }
                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        Directory.CreateDirectory(fullPath);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
                    entry.ExtractToFile(fullPath, false);
                }
            }
        }
    }
}
