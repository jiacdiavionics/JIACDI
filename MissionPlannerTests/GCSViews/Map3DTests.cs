using Microsoft.VisualStudio.TestTools.UnitTesting;
using MissionPlanner.ArduPilot;
using Newtonsoft.Json.Linq;
using OSGeo.GDAL;
using OSGeo.OSR;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace MissionPlanner.GCSViews.Tests
{
    [TestClass]
    public class Map3DTests
    {
        [TestMethod]
        public void VehicleTypesSelectExpectedModels()
        {
            Assert.AreEqual("fixedwing", Map3D.GetVehicleModelKind(
                MAVLink.MAV_TYPE.FIXED_WING, Firmwares.ArduPlane));
            Assert.AreEqual("fixedwing", Map3D.GetVehicleModelKind(
                MAVLink.MAV_TYPE.VTOL_QUADROTOR, Firmwares.ArduPlane));
            Assert.AreEqual("hexacopter", Map3D.GetVehicleModelKind(
                MAVLink.MAV_TYPE.HEXAROTOR, Firmwares.ArduCopter2));
            Assert.AreEqual("helicopter", Map3D.GetVehicleModelKind(
                MAVLink.MAV_TYPE.HELICOPTER, Firmwares.ArduCopter2));
            Assert.AreEqual("quadcopter", Map3D.GetVehicleModelKind(
                MAVLink.MAV_TYPE.QUADROTOR, Firmwares.ArduCopter2));
            Assert.AreEqual("fixedwing", Map3D.GetVehicleModelKind(
                MAVLink.MAV_TYPE.GROUND_ROVER, Firmwares.ArduPlane));
        }

        [TestMethod]
        public void RefreshRunsOnlyForVisibleMapSurface()
        {
            Assert.IsFalse(Map3D.ShouldRunRefresh(false, false, true, false));
            Assert.IsTrue(Map3D.ShouldRunRefresh(true, false, true, false));
            Assert.IsFalse(Map3D.ShouldRunRefresh(true, false, false, true));
            Assert.IsTrue(Map3D.ShouldRunRefresh(true, true, false, true));
            Assert.IsFalse(Map3D.ShouldRunRefresh(true, true, true, false));
        }

        [TestMethod]
        public void LocalResourcesRejectTraversal()
        {
            string[] invalidPaths =
            {
                "/cesium/../secret.txt",
                "/cesium/%2e%2e/secret.txt",
                "/cesium/%252e%252e/secret.txt",
                "/cesium/..%5csecret.txt",
                "/vehicles/C:%5cWindows%5cwin.ini",
                "/gmap/GoogleSatelliteMap/%2e%2e/secret.jpg",
                "/unknown/file.bin",
                "/cesium/"
            };

            foreach (string path in invalidPaths)
            {
                Assert.IsNull(Map3D.ResolveLocalMapResource(path), path);
            }
        }

        [TestMethod]
        public void LocalResourcesUseExpectedMimeTypes()
        {
            Assert.AreEqual("application/javascript", Map3D.GetContentType("Cesium.js"));
            Assert.AreEqual("application/json", Map3D.GetContentType("tileset.json"));
            Assert.AreEqual("application/wasm", Map3D.GetContentType("engine.wasm"));
            Assert.AreEqual("model/gltf-binary", Map3D.GetContentType("fixedwing.glb"));
            Assert.AreEqual("application/octet-stream", Map3D.GetContentType("N31E035.hgt"));
        }

        [TestMethod]
        public void FpvTargetsSixtyFramesPerSecond()
        {
            Assert.AreEqual(60, Hud3DFpvRenderer.TargetFramesPerSecond);
            Assert.AreEqual(16, Hud3DFpvRenderer.FrameIntervalMilliseconds);
            Assert.AreEqual(30, Hud3DFpvRenderer.TelemetryUpdatesPerSecond);
            Assert.AreEqual(33, Hud3DFpvRenderer.TelemetryUpdateIntervalMilliseconds);
            Assert.AreEqual(82, Hud3DFpvRenderer.ScreencastJpegQuality);
            Assert.IsTrue(Hud3DFpvRenderer.ScreencastWatchdogMilliseconds <= 1000);
            Assert.IsTrue(Hud3DFpvRenderer.ScreencastRetryMilliseconds <= 1500);
            Assert.IsTrue(Hud3DFpvRenderer.MaximumConsecutiveCaptureFailures > 1);

            string html = Map3D.BuildCesiumHtml(
                false,
                string.Empty,
                false,
                31.9539,
                35.9106,
                "GoogleSatelliteMap");
            StringAssert.Contains(html, "FPV_PRESENTATION_FRAME_MS = 1000 / 60");
            StringAssert.Contains(html, "targetFrameRate = fpvMode ? 60 : 45");
            StringAssert.Contains(html, "camera.frustum.fov = Cesium.Math.toRadians(74)");
            StringAssert.Contains(html, "handleLockedCameraKeyDown");
            StringAssert.Contains(html, "camera.lookAtTransform(targetFrame, currentOffset)");
            StringAssert.Contains(html, "observePromise(tileset.readyPromise");
            StringAssert.Contains(html, "id=\"lockButton\"");
            StringAssert.Contains(html, "aria-label=\"Drone lock on\"");
            StringAssert.Contains(html, "#lockButton.locked::before");
            Assert.IsFalse(html.Contains(">LOCK ON</button>"));
            Assert.IsFalse(html.Contains("tileset.readyPromise.then(function()"));
        }

        [TestMethod]
        public void Google3DRequiresBothPreferenceAndApiKey()
        {
            Assert.IsTrue(Map3D.ShouldUseGoogle3D("google", "AIza-test"));
            Assert.IsTrue(Map3D.ShouldUseGoogle3D("GOOGLE", "  AIza-test  "));
            Assert.IsFalse(Map3D.ShouldUseGoogle3D("offline", "AIza-test"));
            Assert.IsFalse(Map3D.ShouldUseGoogle3D("google", string.Empty));
            Assert.IsFalse(Map3D.ShouldUseGoogle3D(null, "AIza-test"));
        }

        [TestMethod]
        public void HybridMapHtmlContainsOnlineAndOfflinePaths()
        {
            string google = Map3D.BuildCesiumHtml(
                true,
                "AIza-test",
                true,
                31.9539,
                35.9106,
                "GoogleSatelliteMap");
            StringAssert.Contains(google, "ajax.googleapis.com/ajax/libs/cesiumjs/1.105");
            StringAssert.Contains(google, "tile.googleapis.com/v1/3dtiles/root.json");
            StringAssert.Contains(google, "showCreditsOnScreen: true");
            StringAssert.Contains(google, "\"preferredSource\":\"google\"");
            StringAssert.Contains(google, "\"googleApiKey\":\"AIza-test\"");

            string offline = Map3D.BuildCesiumHtml(
                false,
                string.Empty,
                true,
                32.0,
                36.0,
                "GoogleSatelliteMap");
            StringAssert.Contains(offline, "https://dimp3d.local/cesium/Cesium.js");
            StringAssert.Contains(offline, "\"preferredSource\":\"offline\"");
            StringAssert.Contains(offline, "\"offlineMapProvider\":\"GoogleSatelliteMap\"");
            StringAssert.Contains(offline, "gmap/' + providerName + '/{z}/{y}/{x}.jpg");
        }

        [TestMethod]
        public void OfflineTerrainUsesStandardSrtmNames()
        {
            Assert.AreEqual("N31E035.hgt", Map3DOfflineRegionCache.GetTerrainTileName(31, 35));
            Assert.AreEqual("S01W001.hgt", Map3DOfflineRegionCache.GetTerrainTileName(-1, -1));
            Assert.AreEqual("N00E000.hgt", Map3DOfflineRegionCache.GetTerrainTileName(0, 0));

            string directory = Path.Combine(
                Path.GetTempPath(),
                "dimp-hgt-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string valid = Path.Combine(directory, "N31E035.hgt");
                using (FileStream stream = File.Create(valid))
                {
                    stream.SetLength(1201L * 1201L * 2L);
                }
                string corrupt = Path.Combine(directory, "N31E036.hgt");
                File.WriteAllBytes(corrupt, new byte[64]);

                Assert.IsTrue(Map3DOfflinePackageCatalog.IsValidHgtFile(valid));
                Assert.IsFalse(Map3DOfflinePackageCatalog.IsValidHgtFile(corrupt));
                Assert.IsFalse(Map3DOfflinePackageCatalog.IsValidHgtFile(
                    Path.Combine(directory, "terrain.hgt")));
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [TestMethod]
        public void BundledBuildingTilesUsePhotoTexturesAndValidUvs()
        {
            string package = Path.Combine(FindRepositoryRoot(), "map3d", "buildings3d");
            string manifestPath = Path.Combine(package, "manifest.json");
            Assert.IsTrue(File.Exists(manifestPath), manifestPath);

            JObject manifest = JObject.Parse(File.ReadAllText(manifestPath));
            Assert.AreEqual("dimp-map3d-buildings-3dtiles-v2-textured",
                (string)manifest["format"]);
            Assert.IsTrue((int)manifest.SelectToken("materials.textureSize") >= 1024);
            string facade = (string)manifest.SelectToken("materials.facade");
            string roof = (string)manifest.SelectToken("materials.roof");
            Assert.IsTrue(File.Exists(Path.Combine(package, "textures", facade)), facade);
            Assert.IsTrue(File.Exists(Path.Combine(package, "textures", roof)), roof);

            string contentPath = Directory.GetFiles(
                Path.Combine(package, "content"), "*.b3dm", SearchOption.TopDirectoryOnly).First();
            JObject gltf;
            using (var stream = File.OpenRead(contentPath))
            using (var reader = new BinaryReader(stream, Encoding.UTF8, false))
            {
                CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("b3dm"), reader.ReadBytes(4));
                Assert.AreEqual(1U, reader.ReadUInt32());
                Assert.AreEqual(stream.Length, (long)reader.ReadUInt32());
                uint featureJsonLength = reader.ReadUInt32();
                uint featureBinaryLength = reader.ReadUInt32();
                uint batchJsonLength = reader.ReadUInt32();
                uint batchBinaryLength = reader.ReadUInt32();
                stream.Position = 28L + featureJsonLength + featureBinaryLength +
                    batchJsonLength + batchBinaryLength;

                CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("glTF"), reader.ReadBytes(4));
                Assert.AreEqual(2U, reader.ReadUInt32());
                uint glbLength = reader.ReadUInt32();
                Assert.IsTrue(glbLength <= stream.Length - stream.Position + 12L);
                uint jsonLength = reader.ReadUInt32();
                CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("JSON"), reader.ReadBytes(4));
                gltf = JObject.Parse(Encoding.UTF8.GetString(reader.ReadBytes((int)jsonLength)).Trim());
            }

            JArray accessors = (JArray)gltf["accessors"];
            foreach (JToken primitive in gltf.SelectTokens("meshes[*].primitives[*]"))
            {
                int positionAccessor = (int)primitive.SelectToken("attributes.POSITION");
                int textureAccessor = (int)primitive.SelectToken("attributes.TEXCOORD_0");
                Assert.AreEqual((int)accessors[positionAccessor]["count"],
                    (int)accessors[textureAccessor]["count"]);
                Assert.IsNotNull(primitive.SelectToken("material"));
            }

            CollectionAssert.AreEquivalent(
                new[] { "../textures/" + facade, "../textures/" + roof },
                gltf["images"].Select(image => (string)image["uri"]).ToArray());
            Assert.AreEqual(2,
                gltf.SelectTokens("materials[*].pbrMetallicRoughness.baseColorTexture").Count());
        }

        [TestMethod]
        public void ImportedMapPackagesGenerateLocalCesiumSources()
        {
            IReadOnlyList<Map3DOfflinePackage> packages = new[]
            {
                new Map3DOfflinePackage
                {
                    Id = "ortho-map",
                    Name = "Orthophoto",
                    Kind = Map3DPackageKinds.RasterImagery,
                    RelativePath = "data/area.tif",
                    Enabled = true
                },
                new Map3DOfflinePackage
                {
                    Id = "local-dem",
                    Name = "Elevation",
                    Kind = Map3DPackageKinds.RasterTerrain,
                    RelativePath = "data/area.tif",
                    Enabled = true
                },
                new Map3DOfflinePackage
                {
                    Id = "mesh-terrain",
                    Name = "Quantized mesh",
                    Kind = Map3DPackageKinds.CesiumTerrain,
                    RelativePath = "data/layer.json",
                    Enabled = true
                },
                new Map3DOfflinePackage
                {
                    Id = "city-model",
                    Name = "City",
                    Kind = Map3DPackageKinds.Cesium3DTiles,
                    RelativePath = "data/tileset.json",
                    Enabled = true
                },
                new Map3DOfflinePackage
                {
                    Id = "disabled-map",
                    Kind = Map3DPackageKinds.XyzImagery,
                    RelativePath = "data",
                    Enabled = false
                }
            };

            string html = Map3D.BuildCesiumHtml(
                false,
                string.Empty,
                false,
                31.9539,
                35.9106,
                "GoogleSatelliteMap",
                packages);

            StringAssert.Contains(html, "https://dimp3d.local/raster/ortho-map/{z}/{x}/{y}.png");
            StringAssert.Contains(html, "https://dimp3d.local/dem/local-dem/{z}/{x}/{y}.f32");
            StringAssert.Contains(html, "https://dimp3d.local/imports/mesh-terrain/data/");
            StringAssert.Contains(html, "https://dimp3d.local/imports/city-model/data/tileset.json");
            StringAssert.Contains(html, "properties['building:levels']");
            StringAssert.Contains(html, "levels * 3");
            Assert.IsFalse(html.Contains("disabled-map"));
        }

        [TestMethod]
        public void ImportedMapRoutesAndPackagePathsRejectTraversal()
        {
            Assert.IsTrue(Map3D.IsDynamicMapResourcePath("/raster/map/1/0/0.png"));
            Assert.IsTrue(Map3D.IsDynamicMapResourcePath("/dem/map/1/0/0.f32"));
            Assert.IsTrue(Map3D.IsDynamicMapResourcePath("/xyz/map/1/0/0"));
            Assert.IsFalse(Map3D.IsDynamicMapResourcePath("/imports/map/data/tileset.json"));

            string[] unsafeRoutes =
            {
                "/raster/map/1/../0.png",
                "/dem/map/not-a-level/0/0.f32",
                "/xyz/map/1/-1/0",
                "/raster/map/1/0/0.png/extra"
            };
            foreach (string route in unsafeRoutes)
            {
                Assert.IsNull(Map3D.GetDynamicMapResource(route), route);
            }

            string root = Path.Combine(Path.GetTempPath(), "dimp-map-package-root");
            Assert.AreEqual(
                Path.GetFullPath(Path.Combine(root, "data", "tile.png")),
                Map3DOfflinePackageCatalog.ResolveUnderRoot(root, "data/tile.png"));
            Assert.IsNull(Map3DOfflinePackageCatalog.ResolveUnderRoot(root, "../outside.bin"));
            Assert.IsNull(Map3DOfflinePackageCatalog.ResolveUnderRoot(root, "data/../../outside.bin"));
            Assert.IsNull(Map3DOfflinePackageCatalog.ResolveUnderRoot(root, @"C:\Windows\win.ini"));
        }

        [TestMethod]
        public void SitlHighFidelityLaunchUsesNativeRateAndSafeWindowsQuoting()
        {
            Assert.AreEqual(1200, SITL.HighFidelitySimulationRate);
            Assert.AreEqual(
                "\"C:\\Users\\HP\\Map Data\\defaults.parm\"",
                SITL.QuoteCommandLineValue(@"C:\Users\HP\Map Data\defaults.parm"));
            Assert.AreEqual(
                "\"C:\\Map Data\\\\\"",
                SITL.QuoteCommandLineValue(@"C:\Map Data\"));
            Assert.AreEqual("\"a\\\"b\"", SITL.QuoteCommandLineValue("a\"b"));

            string arguments = SITL.BuildSITLArguments(
                "plane:dimp-airframe.json",
                "31.9539,35.9106,800,0",
                1,
                SITL.HighFidelitySimulationRate,
                "--defaults \"C:\\Map Data\\plane.parm\"");
            StringAssert.Contains(arguments, "-Mplane:dimp-airframe.json");
            StringAssert.Contains(arguments, "--rate 1200");
            StringAssert.Contains(arguments, "--defaults \"C:\\Map Data\\plane.parm\"");
        }

        [TestMethod]
        public void SitlFrameSelectionAndAerodynamicModelAreValidated()
        {
            string root = FindRepositoryRoot();
            string sitl = Path.Combine(root, "sitl");
            string manifest = Path.Combine(sitl, "vehicleinfo.json");
            IReadOnlyList<string> frames = SITL.GetBundledFrameNames(manifest);

            CollectionAssert.Contains(frames.ToArray(), "plane");
            CollectionAssert.Contains(frames.ToArray(), "hexa");
            CollectionAssert.Contains(frames.ToArray(), "heli");
            CollectionAssert.DoesNotContain(frames.ToArray(), "IrisRos");
            Assert.IsTrue(SITL.IsFrameCompatibleWithExecutable(
                manifest, "plane", Path.Combine(sitl, "ArduPlane.exe")));
            Assert.IsFalse(SITL.IsFrameCompatibleWithExecutable(
                manifest, "hexa", Path.Combine(sitl, "ArduPlane.exe")));
            Assert.IsTrue(SITL.IsFrameCompatibleWithExecutable(
                manifest, "hexa", Path.Combine(sitl, "ArduCopter.exe")));

            string modelPath = Path.Combine(sitl, "models", "skywalker_2013.json");
            string validationError;
            Assert.IsTrue(SITL.ValidateAerodynamicModelJson(modelPath, out validationError),
                validationError);

            string directoryName = SITL.GetSimulationDirectoryName(
                @"plane:C:\external\airframe.json",
                Path.Combine(sitl, "ArduPlane.exe"));
            Assert.IsTrue(directoryName.Length <= 64);
            Assert.IsFalse(directoryName.Contains(":"));
            Assert.IsFalse(directoryName.Contains("\\"));
            Assert.IsFalse(directoryName.Contains(".."));
        }

        [TestMethod]
        [TestCategory("Integration")]
        public void GeoreferencedRasterPackagesRenderImageryAndTerrainTiles()
        {
            string root = FindRepositoryRoot();
            EnsureGdalRuntimeForTests(root);
            var runtime = new GDAL.GDAL();
            Gdal.AllRegister();

            string temporaryRoot = Path.Combine(
                Path.GetTempPath(),
                "dimp-raster-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryRoot);
            Map3DOfflinePackage imageryPackage = null;
            Map3DOfflinePackage terrainPackage = null;
            try
            {
                string imageryPath = Path.Combine(temporaryRoot, "world-imagery.tif");
                CreateTestRaster(imageryPath, 3, DataType.GDT_Byte);
                Map3DRasterInfo imageryInfo = Map3DRasterTileService.Inspect(imageryPath);
                Assert.AreEqual(3, imageryInfo.BandCount);
                Assert.AreEqual(-180, imageryInfo.West, 0.01);
                Assert.AreEqual(180, imageryInfo.East, 0.01);

                imageryPackage = Map3DOfflinePackageCatalog.ImportFile(
                    imageryPath,
                    Map3DRasterImportRole.Imagery);
                byte[] imageryTile = Map3DRasterTileService.GetImageryTile(
                    imageryPackage, 0, 0, 0);
                Assert.IsNotNull(imageryTile);
                Assert.IsTrue(imageryTile.Length > 100);
                CollectionAssert.AreEqual(
                    new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 },
                    imageryTile.Take(8).ToArray());
                Assert.AreEqual(256, ReadBigEndianInt32(imageryTile, 16));
                Assert.AreEqual(256, ReadBigEndianInt32(imageryTile, 20));

                string terrainPath = Path.Combine(temporaryRoot, "world-terrain.tif");
                CreateTestRaster(terrainPath, 1, DataType.GDT_Float32);
                terrainPackage = Map3DOfflinePackageCatalog.ImportFile(
                    terrainPath,
                    Map3DRasterImportRole.Terrain);
                byte[] terrainTile = Map3DRasterTileService.GetTerrainTile(
                    terrainPackage, 0, 1, 0);
                Assert.IsNotNull(terrainTile);
                Assert.AreEqual(
                    Map3DRasterTileService.TerrainTileSize *
                    Map3DRasterTileService.TerrainTileSize * sizeof(float),
                    terrainTile.Length);
                var heights = new float[terrainTile.Length / sizeof(float)];
                Buffer.BlockCopy(terrainTile, 0, heights, 0, terrainTile.Length);
                Assert.IsTrue(heights.Any(height => Math.Abs(height - 123.5f) < 0.1f));
            }
            finally
            {
                if (imageryPackage != null)
                {
                    Map3DOfflinePackageCatalog.Remove(imageryPackage.Id);
                }
                if (terrainPackage != null)
                {
                    Map3DOfflinePackageCatalog.Remove(terrainPackage.Id);
                }
                Map3DRasterTileService.Invalidate();
                Directory.Delete(temporaryRoot, true);
            }
        }

        [TestMethod]
        public void CoreSitlVehiclesResolveBundledDefaults()
        {
            string root = FindRepositoryRoot();
            string sitl = Path.Combine(root, "sitl");
            string manifest = Path.Combine(sitl, "vehicleinfo.json");

            CollectionAssert.AreEqual(
                new[] { Path.Combine(sitl, "models", "plane.parm") },
                SITL.ResolveDefaultParameterFiles(manifest, sitl, "plane",
                    Path.Combine(sitl, "ArduPlane.exe")).ToArray());
            CollectionAssert.AreEqual(
                new[] { Path.Combine(sitl, "default_params", "copter.parm") },
                SITL.ResolveDefaultParameterFiles(manifest, sitl, "+",
                    Path.Combine(sitl, "ArduCopter.exe")).ToArray());
            CollectionAssert.AreEqual(
                new[] { Path.Combine(sitl, "default_params", "copter-heli.parm") },
                SITL.ResolveDefaultParameterFiles(manifest, sitl, "heli",
                    Path.Combine(sitl, "ArduHeli.exe")).ToArray());
            CollectionAssert.AreEqual(
                new[] { Path.Combine(sitl, "default_params", "rover.parm") },
                SITL.ResolveDefaultParameterFiles(manifest, sitl, "rover",
                    Path.Combine(sitl, "ArduRover.exe")).ToArray());
        }

        [TestMethod]
        public void SitlManifestSupportsLayeredFrameDefaults()
        {
            string root = FindRepositoryRoot();
            string sitl = Path.Combine(root, "sitl");
            IReadOnlyList<string> defaults = SITL.ResolveDefaultParameterFiles(
                Path.Combine(sitl, "vehicleinfo.json"),
                sitl,
                "hexax",
                Path.Combine(sitl, "ArduCopter.exe"));

            CollectionAssert.AreEqual(new[]
            {
                Path.Combine(sitl, "default_params", "copter.parm"),
                Path.Combine(sitl, "default_params", "copter-hexa.parm"),
                Path.Combine(sitl, "default_params", "copter-X.parm")
            }, defaults.ToArray());
        }

        [TestMethod]
        public void SitlManifestRejectsEscapingDefaultPaths()
        {
            string temporaryRoot = Path.Combine(Path.GetTempPath(), "dimp-sitl-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryRoot);
            string manifest = Path.Combine(temporaryRoot, "vehicleinfo.json");

            try
            {
                File.WriteAllText(manifest,
                    "{\"ArduPlane\":{\"frames\":{\"plane\":{\"default_params_filename\":\"../outside.parm\"}}}}");

                IReadOnlyList<string> defaults = SITL.ResolveDefaultParameterFiles(
                    manifest,
                    temporaryRoot,
                    "plane",
                    Path.Combine(temporaryRoot, "ArduPlane.exe"));

                Assert.AreEqual(0, defaults.Count);
            }
            finally
            {
                Directory.Delete(temporaryRoot, true);
            }
        }

        [TestMethod]
        public void SitlStateResetsOncePerSimulatorDefaultsVersion()
        {
            string temporaryRoot = Path.Combine(Path.GetTempPath(), "dimp-sitl-state-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryRoot);
            string executable = Path.Combine(temporaryRoot, "ArduPlane.exe");
            string defaults = Path.Combine(temporaryRoot, "plane.parm");

            try
            {
                File.WriteAllText(executable, "simulator-v1");
                File.WriteAllText(defaults, "INS_ACCOFFS_X 0.001");
                string first = SITL.BuildSITLStateFingerprint(executable, defaults);

                Assert.IsTrue(SITL.ShouldResetSITLState(temporaryRoot, first));
                SITL.WriteSITLStateFingerprint(temporaryRoot, first);
                Assert.IsFalse(SITL.ShouldResetSITLState(temporaryRoot, first));

                File.AppendAllText(defaults, Environment.NewLine + "INS_GYR_CAL 0");
                string second = SITL.BuildSITLStateFingerprint(executable, defaults);
                Assert.AreNotEqual(first, second);
                Assert.IsTrue(SITL.ShouldResetSITLState(temporaryRoot, second));
            }
            finally
            {
                Directory.Delete(temporaryRoot, true);
            }
        }

        [TestMethod]
        public void VehiclePositionRequiresGpsFixAndValidCoordinates()
        {
            Assert.IsFalse(VehicleTelemetryValidation.HasUsablePosition(0, 31.9539, 35.9106));
            Assert.IsFalse(VehicleTelemetryValidation.HasUsablePosition(2, 31.9539, 35.9106));
            Assert.IsFalse(VehicleTelemetryValidation.HasUsablePosition(3, 0, 0));
            Assert.IsFalse(VehicleTelemetryValidation.HasUsablePosition(3, 91, 35.9106));
            Assert.IsFalse(VehicleTelemetryValidation.HasUsablePosition(3, double.NaN, 35.9106));
            Assert.IsTrue(VehicleTelemetryValidation.HasUsablePosition(3, 31.9539, 35.9106));
            Assert.IsTrue(VehicleTelemetryValidation.HasUsablePosition(3, 0, 35.9106));
            Assert.IsTrue(VehicleTelemetryValidation.HasUsablePosition(3, 31.9539, 0));
        }

        [TestMethod]
        public void DisarmedVehicleCannotBeVisuallyExtrapolated()
        {
            var state = new CurrentState
            {
                armed = false,
                groundspeed = 21.8f,
                climbrate = 4.2f
            };

            Assert.AreEqual(0, VehicleTelemetryValidation.GetVisualGroundSpeed(state));
            Assert.AreEqual(0, VehicleTelemetryValidation.GetVisualClimbRate(state));

            state.armed = true;
            Assert.AreEqual(21.8f, VehicleTelemetryValidation.GetVisualGroundSpeed(state));
            Assert.AreEqual(4.2f, VehicleTelemetryValidation.GetVisualClimbRate(state));
        }

        [TestMethod]
        public void SitlReadinessRequiresHealthyAhrsAndGps()
        {
            var state = new CurrentState
            {
                gpsstatus = 3,
                lat = 31.9539,
                lng = 35.9106
            };

            Assert.IsFalse(SITL.IsSITLReady(state));
            state.sensors_health.gyro = true;
            state.sensors_health.accelerometer = true;
            state.sensors_health.ahrs = true;
            Assert.IsTrue(SITL.IsSITLReady(state));

            Assert.IsTrue(SITL.IsTransientSITLStartupMessage("Unhealthy AHRS"));
            Assert.IsTrue(SITL.IsTransientSITLStartupMessage("EKF3 waiting for GPS checks"));
            Assert.IsFalse(SITL.IsTransientSITLStartupMessage("PreArm: Airspeed 1 not healthy"));
        }

        [TestMethod]
        public void HostedTabsRestoreOriginalOrderAndSelection()
        {
            RunInSta(() =>
            {
                TabControl original = new TabControl();
                TabControl temporary = new TabControl();
                TabPage first = new TabPage("First");
                TabPage target = new TabPage("Target");
                TabPage selected = new TabPage("Selected");

                try
                {
                    original.TabPages.AddRange(new[] { first, target, selected });
                    original.SelectedTab = selected;
                    IntPtr originalHandle = original.Handle;
                    IntPtr temporaryHandle = temporary.Handle;
                    Assert.AreNotEqual(IntPtr.Zero, originalHandle);
                    Assert.AreNotEqual(IntPtr.Zero, temporaryHandle);
                    Assert.AreSame(original, target.Parent);

                    ThreeScreenManager.HostedTabState state =
                        ThreeScreenManager.HostedTabState.Capture(target);
                    state.HostIn(temporary);
                    state.Restore();
                    state.RestoreSelection();

                    Assert.AreEqual(1, original.TabPages.IndexOf(target));
                    Assert.AreSame(selected, original.SelectedTab);
                    Assert.AreEqual(0, temporary.TabPages.Count);
                }
                finally
                {
                    temporary.Dispose();
                    original.Dispose();
                }
            });
        }

        [TestMethod]
        public void DetachedHostedTabSurvivesTemporaryWindowDisposal()
        {
            RunInSta(() =>
            {
                TabPage target = new TabPage("Hidden Action Panel");
                TabControl temporary = new TabControl();

                try
                {
                    ThreeScreenManager.HostedTabState state =
                        ThreeScreenManager.HostedTabState.Capture(target);
                    state.HostIn(temporary);
                    state.Restore();

                    Assert.IsNull(target.Parent);
                    temporary.Dispose();
                    Assert.IsFalse(target.IsDisposed);
                }
                finally
                {
                    temporary.Dispose();
                    target.Dispose();
                }
            });
        }

        [TestMethod]
        public void OfflinePackageManagerCanCreateAndShow()
        {
            RunInSta(() =>
            {
                using (var manager = new Map3DOfflinePackageManagerForm())
                {
                    manager.Show();
                    Application.DoEvents();
                    Assert.IsTrue(manager.Visible);
                    Assert.IsTrue(manager.Controls.Count > 0);
                    manager.Close();
                }
            });
        }

        [TestMethod]
        public void BundledSitlPayloadIsComplete()
        {
            string root = FindRepositoryRoot();
            string sitl = Path.Combine(root, "sitl");

            Assert.IsTrue(Directory.Exists(sitl), sitl);
            Assert.IsTrue(SITL.HasSITLDependencies(sitl));

            foreach (string image in SITL.BundledVehicleImages)
            {
                Assert.IsTrue(File.Exists(Path.Combine(sitl, image)), image);
            }

            foreach (string dependency in SITL.RequiredDependencyFiles)
            {
                Assert.IsTrue(File.Exists(Path.Combine(sitl, dependency)), dependency);
            }

            string[] supportFiles =
            {
                "sim_vehicle.py",
                "vehicleinfo.py",
                "vehicleinfo.json",
                "models/plane.parm",
                "models/skywalker_2013.json",
                "default_params/copter.parm",
                "default_params/copter-heli.parm",
                "default_params/rover.parm"
            };
            foreach (string supportFile in supportFiles)
            {
                Assert.IsTrue(File.Exists(Path.Combine(sitl, supportFile)), supportFile);
            }
        }

        private static string FindRepositoryRoot()
        {
            DirectoryInfo directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "MissionPlanner.csproj")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            Assert.Fail("Unable to locate the DIMP repository root.");
            return null;
        }

        private static void EnsureGdalRuntimeForTests(string root)
        {
            string source = Path.Combine(root, "bin", "Release", "net461", "gdal");
            string destination = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "gdal");
            Assert.IsTrue(Directory.Exists(source), source);

            foreach (string directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            {
                string relative = directory.Substring(source.Length).TrimStart(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
                Directory.CreateDirectory(Path.Combine(destination, relative));
            }
            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string relative = file.Substring(source.Length).TrimStart(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
                string target = Path.Combine(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, true);
            }
        }

        private static void CreateTestRaster(string path, int bandCount, DataType dataType)
        {
            const int width = 360;
            const int height = 180;
            OSGeo.GDAL.Driver driver = Gdal.GetDriverByName("GTiff");
            Assert.IsNotNull(driver);
            using (Dataset dataset = driver.Create(path, width, height, bandCount, dataType, null))
            {
                Assert.IsNotNull(dataset);
                dataset.SetGeoTransform(new[] { -180.0, 1.0, 0.0, 90.0, 0.0, -1.0 });
                using (var reference = new SpatialReference(string.Empty))
                {
                    Assert.AreEqual(0, reference.ImportFromEPSG(4326));
                    string projection;
                    Assert.AreEqual(0, reference.ExportToWkt(out projection));
                    dataset.SetProjection(projection);
                }

                int pixels = width * height;
                if (dataType == DataType.GDT_Float32)
                {
                    float[] values = Enumerable.Repeat(123.5f, pixels).ToArray();
                    Assert.AreEqual(CPLErr.CE_None, dataset.GetRasterBand(1).WriteRaster(
                        0, 0, width, height, values, width, height, 0, 0));
                    dataset.GetRasterBand(1).SetUnitType("m");
                }
                else
                {
                    byte[][] values =
                    {
                        Enumerable.Repeat((byte)200, pixels).ToArray(),
                        Enumerable.Repeat((byte)100, pixels).ToArray(),
                        Enumerable.Repeat((byte)50, pixels).ToArray()
                    };
                    ColorInterp[] interpretations =
                    {
                        ColorInterp.GCI_RedBand,
                        ColorInterp.GCI_GreenBand,
                        ColorInterp.GCI_BlueBand
                    };
                    for (int index = 0; index < bandCount; index++)
                    {
                        Band band = dataset.GetRasterBand(index + 1);
                        band.SetRasterColorInterpretation(interpretations[index]);
                        Assert.AreEqual(CPLErr.CE_None, band.WriteRaster(
                            0, 0, width, height, values[index], width, height, 0, 0));
                    }
                }

                dataset.FlushCache();
            }
        }

        private static int ReadBigEndianInt32(byte[] values, int offset)
        {
            return values[offset] << 24 |
                   values[offset + 1] << 16 |
                   values[offset + 2] << 8 |
                   values[offset + 3];
        }

        private static void RunInSta(Action action)
        {
            Exception failure = null;
            Thread thread = new Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (failure != null)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }
    }
}
