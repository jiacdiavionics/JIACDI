#!/usr/bin/env python3
"""Build offline 3D building GeoJSON tiles for DIMP's Cesium map."""

from __future__ import annotations

import argparse
import json
import math
import re
import shutil
import sys
import time
from pathlib import Path
from typing import Dict, Iterable, List, Optional, Set, Tuple

import osmium
import requests


EXTRACTS = {
    "jordan": "https://download.geofabrik.de/asia/jordan-latest.osm.pbf",
    "iraq": "https://download.geofabrik.de/asia/iraq-latest.osm.pbf",
    "syria": "https://download.geofabrik.de/asia/syria-latest.osm.pbf",
    "lebanon": "https://download.geofabrik.de/asia/lebanon-latest.osm.pbf",
    "israel-and-palestine": "https://download.geofabrik.de/asia/israel-and-palestine-latest.osm.pbf",
    "gcc-states": "https://download.geofabrik.de/asia/gcc-states-latest.osm.pbf",
}

BUILDING_TILE_SCALE = 10


HEIGHT_RE = re.compile(r"[-+]?\d+(?:[.,]\d+)?")


def tile_name(lat_degree: int, lng_degree: int, suffix: str = ".json") -> str:
    ns = "N" if lat_degree >= 0 else "S"
    ew = "E" if lng_degree >= 0 else "W"
    return f"{ns}{abs(lat_degree):02d}{ew}{abs(lng_degree):03d}{suffix}"


def building_tile_name(lat: float, lng: float) -> str:
    lat_index = math.floor(lat * BUILDING_TILE_SCALE)
    lng_index = math.floor(lng * BUILDING_TILE_SCALE)
    lat_degree = math.floor(lat_index / BUILDING_TILE_SCALE)
    lng_degree = math.floor(lng_index / BUILDING_TILE_SCALE)
    lat_sub = lat_index - (lat_degree * BUILDING_TILE_SCALE)
    lng_sub = lng_index - (lng_degree * BUILDING_TILE_SCALE)
    return f"{tile_name(lat_degree, lng_degree, '')}_{lat_sub}_{lng_sub}.json"


def parse_hgt_name(path: Path) -> Optional[str]:
    name = path.stem.upper()
    if len(name) != 7:
        return None
    if name[0] not in "NS" or name[3] not in "EW":
        return None
    try:
        int(name[1:3])
        int(name[4:7])
    except ValueError:
        return None
    return name + ".json"


def load_srtm_tile_names(srtm_dir: Path) -> Set[str]:
    tiles = set()
    for hgt in srtm_dir.glob("*.hgt"):
        name = parse_hgt_name(hgt)
        if name:
            tiles.add(name)
    return tiles


def parse_number(value: Optional[str]) -> Optional[float]:
    if not value:
        return None
    match = HEIGHT_RE.search(str(value).replace(",", "."))
    if not match:
        return None
    try:
        return float(match.group(0))
    except ValueError:
        return None


def parse_height(tags: osmium.osm.TagList) -> float:
    height = parse_number(tags.get("height")) or parse_number(tags.get("building:height"))
    if height is not None:
        if "ft" in str(tags.get("height", "")).lower() or "'" in str(tags.get("height", "")):
            height *= 0.3048
        return clamp_height(height)

    levels = parse_number(tags.get("building:levels")) or parse_number(tags.get("levels"))
    if levels is not None:
        return clamp_height(levels * 3.0)

    return 8.0


def clamp_height(value: float) -> float:
    if not math.isfinite(value):
        return 8.0
    return max(2.0, min(300.0, value))


def round_coords(value):
    if isinstance(value, list):
        if value and isinstance(value[0], (int, float)):
            return [round(float(value[0]), 6), round(float(value[1]), 6)]
        return [round_coords(item) for item in value]
    return value


def iter_points(coords) -> Iterable[Tuple[float, float]]:
    if isinstance(coords, list):
        if len(coords) >= 2 and isinstance(coords[0], (int, float)) and isinstance(coords[1], (int, float)):
            yield float(coords[0]), float(coords[1])
        else:
            for item in coords:
                yield from iter_points(item)


def centroid_tiles(geometry: dict) -> Tuple[Optional[str], Optional[str]]:
    points = list(iter_points(geometry.get("coordinates", [])))
    if not points:
        return None, None
    lng = sum(point[0] for point in points) / len(points)
    lat = sum(point[1] for point in points) / len(points)
    if not math.isfinite(lat) or not math.isfinite(lng):
        return None, None
    return tile_name(math.floor(lat), math.floor(lng)), building_tile_name(lat, lng)


def download(url: str, destination: Path) -> None:
    if destination.exists() and destination.stat().st_size > 0:
        print(f"Using cached {destination.name}")
        return

    destination.parent.mkdir(parents=True, exist_ok=True)
    temp = destination.with_suffix(destination.suffix + ".part")
    print(f"Downloading {url}")

    attempts = 0
    while attempts < 6:
        attempts += 1
        existing = temp.stat().st_size if temp.exists() else 0
        headers = {"Range": f"bytes={existing}-"} if existing else {}
        mode = "ab" if existing else "wb"

        try:
            with requests.get(url, stream=True, timeout=(30, 300), headers=headers) as response:
                if existing and response.status_code != 206:
                    existing = 0
                    mode = "wb"
                response.raise_for_status()

                total_header = response.headers.get("content-range") or response.headers.get("content-length")
                total = 0
                if total_header and "/" in total_header:
                    total = int(total_header.rsplit("/", 1)[1])
                elif total_header:
                    total = int(total_header) + existing

                done = existing
                last_report = time.time()
                if existing:
                    print(f"  resuming {destination.name} at {existing / (1024 * 1024):.1f} MB")

                with temp.open(mode) as handle:
                    for chunk in response.iter_content(chunk_size=1024 * 1024):
                        if not chunk:
                            continue
                        handle.write(chunk)
                        done += len(chunk)
                        if time.time() - last_report > 10:
                            if total:
                                print(f"  {destination.name}: {done / total:.1%}")
                            else:
                                print(f"  {destination.name}: {done / (1024 * 1024):.1f} MB")
                            last_report = time.time()

            break
        except Exception as exc:
            if attempts >= 6:
                raise
            print(f"  download retry {attempts}/5 for {destination.name}: {exc}")
            time.sleep(5 * attempts)

    temp.replace(destination)


class BuildingHandler(osmium.SimpleHandler):
    def __init__(self, output_dir: Path, allowed_tiles: Set[str]) -> None:
        super().__init__()
        self.output_dir = output_dir
        self.allowed_tiles = allowed_tiles
        self.factory = osmium.geom.GeoJSONFactory()
        self.handles: Dict[str, object] = {}
        self.count = 0
        self.skipped = 0

    def area(self, area) -> None:
        if "building" not in area.tags:
            return

        try:
            geometry = json.loads(self.factory.create_multipolygon(area))
        except Exception:
            self.skipped += 1
            return

        srtm_tile, building_tile = centroid_tiles(geometry)
        if not srtm_tile or not building_tile or srtm_tile not in self.allowed_tiles:
            self.skipped += 1
            return

        geometry["coordinates"] = round_coords(geometry["coordinates"])
        feature = {
            "type": "Feature",
            "properties": {
                "height": round(parse_height(area.tags), 2),
                "name": area.tags.get("name", "Building"),
            },
            "geometry": geometry,
        }

        handle = self.handles.get(building_tile)
        if handle is None:
            handle = (self.output_dir / f"{building_tile}.tmp").open("a", encoding="utf-8")
            self.handles[building_tile] = handle

        handle.write(json.dumps(feature, separators=(",", ":"), ensure_ascii=False))
        handle.write("\n")
        self.count += 1

        if self.count % 50000 == 0:
            print(f"  extracted {self.count:,} buildings")

    def close(self) -> None:
        for handle in self.handles.values():
            handle.close()
        self.handles.clear()


def finalize_tiles(output_dir: Path, srtm_tiles: Set[str]) -> None:
    manifest_tiles: List[str] = []

    for temp in sorted(output_dir.glob("*.json.tmp")):
        tile = temp.name[:-4]
        final = output_dir / tile
        count = 0

        with temp.open("r", encoding="utf-8") as source, final.open("w", encoding="utf-8") as target:
            target.write('{"type":"FeatureCollection","features":[')
            first = True
            for line in source:
                line = line.strip()
                if not line:
                    continue
                if not first:
                    target.write(",")
                target.write(line)
                first = False
                count += 1
            target.write("]}")

        temp.unlink()
        if count:
            manifest_tiles.append(tile)
        else:
            final.unlink(missing_ok=True)

    manifest = {
        "format": "dimp-map3d-buildings-v1",
        "tileDegrees": 1.0 / BUILDING_TILE_SCALE,
        "heightMeters": {
            "heightTag": "height",
            "levelsTag": "building:levels * 3.0",
            "default": 8,
            "clamp": [2, 300],
        },
        "srtmCoverageTiles": sorted(srtm_tiles),
        "tilesWithBuildings": sorted(manifest_tiles),
    }
    (output_dir / "manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--srtm-dir", required=True, type=Path)
    parser.add_argument("--output-dir", default=Path("map3d/buildings"), type=Path)
    parser.add_argument("--cache-dir", default=Path("map3d/osm-cache"), type=Path)
    parser.add_argument("--keep-cache", action="store_true")
    parser.add_argument("--skip-download", action="store_true")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    srtm_tiles = load_srtm_tile_names(args.srtm_dir)
    if not srtm_tiles:
        print(f"No .hgt SRTM tiles found in {args.srtm_dir}", file=sys.stderr)
        return 2

    args.output_dir.mkdir(parents=True, exist_ok=True)
    for old in args.output_dir.glob("*.json"):
        old.unlink()
    for old in args.output_dir.glob("*.json.tmp"):
        old.unlink()

    handler = BuildingHandler(args.output_dir, srtm_tiles)

    try:
        for name, url in EXTRACTS.items():
            pbf = args.cache_dir / f"{name}-latest.osm.pbf"
            if not args.skip_download:
                download(url, pbf)
            if not pbf.exists():
                print(f"Missing {pbf}; cannot process {name}", file=sys.stderr)
                return 3
            print(f"Processing {pbf.name}")
            handler.apply_file(str(pbf), locations=True)
    finally:
        handler.close()

    finalize_tiles(args.output_dir, srtm_tiles)
    print(f"Buildings extracted: {handler.count:,}; skipped: {handler.skipped:,}")
    print(f"Wrote {args.output_dir}")

    if not args.keep_cache and args.cache_dir.exists():
        shutil.rmtree(args.cache_dir)

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
