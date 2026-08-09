param(
    [switch]$IncludeIntegrationTests,
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath failed with exit code $LASTEXITCODE"
    }
}

function Require-File {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required release file is missing: $Path"
    }
}

function Require-Directory {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "Required release directory is missing: $Path"
    }
}

Write-Host "Restoring and building the DIMP shipping solution..."
Invoke-Native dotnet @("restore", "DIMP.sln")
Invoke-Native dotnet @("build", "DIMP.sln", "-c", "Release", "--no-restore", "-v:minimal")

$testAssembly = Join-Path $root "MissionPlannerTests\bin\Release\net472\MissionPlannerTests.dll"
Require-File $testAssembly
Write-Host "Running offline regression tests..."
Invoke-Native dotnet @(
    "vstest",
    $testAssembly,
    "--TestCaseFilter:TestCategory!=Integration",
    "--Logger:console;verbosity=normal"
)

if ($IncludeIntegrationTests) {
    Write-Host "Running network integration tests..."
    Invoke-Native dotnet @(
        "vstest",
        $testAssembly,
        "--TestCaseFilter:TestCategory=Integration",
        "--Logger:console;verbosity=normal"
    )
}

Write-Host "Auditing the shipping dependency graph..."
$audit = (& dotnet list MissionPlanner.csproj package --vulnerable --include-transitive 2>&1 | Out-String)
Write-Host $audit
if ($LASTEXITCODE -ne 0 -or $audit -notmatch "has no vulnerable packages") {
    throw "The shipping dependency audit did not pass."
}

Write-Host "Checking embedded Map3D JavaScript syntax..."
$node = Get-Command node -ErrorAction SilentlyContinue
if ($null -eq $node) {
    $bundledNode = Join-Path $env:USERPROFILE ".cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe"
    if (Test-Path -LiteralPath $bundledNode -PathType Leaf) {
        $node = Get-Item -LiteralPath $bundledNode
    }
}

if ($null -eq $node) {
    Write-Warning "Node.js was not found; skipping the optional Map3D JavaScript syntax check."
}
else {
    $mapSource = Get-Content (Join-Path $root "GCSViews\Map3D.cs") -Raw
    $scriptMatches = [regex]::Matches($mapSource, "(?s)<script>(.*?)</script>")
    if ($scriptMatches.Count -eq 0) {
        throw "No inline Map3D JavaScript was found."
    }

    $javascript = (($scriptMatches | ForEach-Object { $_.Groups[1].Value }) -join [Environment]::NewLine).Replace('""', '"')
    $temporaryScript = Join-Path ([IO.Path]::GetTempPath()) ("dimp-map3d-" + [Guid]::NewGuid().ToString("N") + ".js")
    try {
        [IO.File]::WriteAllText($temporaryScript, $javascript, [Text.UTF8Encoding]::new($false))
        Invoke-Native $node.FullName @("--check", $temporaryScript)
    }
    finally {
        Remove-Item -LiteralPath $temporaryScript -Force -ErrorAction SilentlyContinue
    }
}

$releaseRoot = Join-Path $root "bin\Release\net461"
$requiredReleaseFiles = @(
    "DIMP.exe",
    "Windows11.mpsystheme",
    "Tools\scrcpy\adb.exe",
    "Tools\scrcpy\scrcpy.exe"
)
foreach ($file in $requiredReleaseFiles) {
    Require-File (Join-Path $releaseRoot $file)
}

$requiredModels = @("fixedwing.glb", "quadcopter.glb", "hexacopter.glb", "helicopter.glb")
foreach ($model in $requiredModels) {
    Require-File (Join-Path $root ("map3d\vehicles\" + $model))
}

$requiredSitlFiles = @(
    "ArduCopter.exe", "ArduHeli.exe", "ArduPlane.exe", "ArduRover.exe",
    "cygatomic-1.dll", "cyggcc_s-1.dll", "cyggcc_s-seh-1.dll", "cyggomp-1.dll",
    "cygiconv-2.dll", "cygintl-8.dll", "cygquadmath-0.dll", "cygssp-0.dll",
    "cygstdc++-6.dll", "cygwin1.dll", "sim_vehicle.py", "vehicleinfo.py",
    "vehicleinfo.json", "models\plane.parm", "default_params\copter.parm",
    "models\skywalker_2013.json",
    "default_params\copter-heli.parm", "default_params\rover.parm"
)
foreach ($file in $requiredSitlFiles) {
    Require-File (Join-Path $root ("sitl\" + $file))
}

Require-File (Join-Path $root "ExtLibs\wasm\wwwroot\Cesium\Cesium.js")
$buildingPackage = Join-Path $root "map3d\buildings3d"
Require-File (Join-Path $buildingPackage "tileset.json")
$buildingManifestPath = Join-Path $buildingPackage "manifest.json"
Require-File $buildingManifestPath
$buildingManifest = Get-Content -LiteralPath $buildingManifestPath -Raw | ConvertFrom-Json
if ($buildingManifest.format -ne "dimp-map3d-buildings-3dtiles-v2-textured") {
    throw "The bundled 3D building package is not the textured v2 format."
}
Require-File (Join-Path $buildingPackage ("textures\" + $buildingManifest.materials.facade))
Require-File (Join-Path $buildingPackage ("textures\" + $buildingManifest.materials.roof))
Require-Directory "C:\ProgramData\Mission Planner\gmapcache\TileDBv3\en\GoogleSatelliteMap"
Require-File "C:\ProgramData\Mission Planner\srtm\N31E035.hgt"

if (-not $SkipInstaller) {
    $isccCandidates = @(
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
    )
    $iscc = $isccCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if ([string]::IsNullOrEmpty($iscc)) {
        throw "Inno Setup 6 compiler was not found."
    }

    Write-Host "Building the administrator Windows installer..."
    Invoke-Native $iscc @("/Qp", "MissionPlanner.iss")

    $installer = Join-Path $root "bin\installer\DIMP-1.3.83-Setup.exe"
    Require-File $installer
    $installerInfo = Get-Item -LiteralPath $installer
    if ($installerInfo.Length -lt 800MB) {
        throw "Installer is unexpectedly small: $($installerInfo.Length) bytes"
    }

    $hash = Get-FileHash -LiteralPath $installer -Algorithm SHA256
    $hashFile = [IO.Path]::ChangeExtension($installer, ".sha256")
    [IO.File]::WriteAllText(
        $hashFile,
        ($hash.Hash.ToLowerInvariant() + "  " + $installerInfo.Name + [Environment]::NewLine),
        [Text.UTF8Encoding]::new($false))
    Write-Host "Installer: $installer"
    Write-Host "Size: $($installerInfo.Length) bytes"
    Write-Host "SHA-256: $($hash.Hash)"
}

Write-Host "DIMP release validation completed successfully."
