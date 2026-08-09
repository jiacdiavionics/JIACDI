#!/usr/bin/env python3
"""Convert DIMP's offline OSM GeoJSON into terrain-seated Cesium 3D Tiles.

This is a build-time tool. It keeps the runtime fast by turning each 0.1-degree
GeoJSON file into small batched 3D models and a two-level tileset hierarchy.

Dependencies:
    pip install numpy mapbox-earcut Pillow
"""

from __future__ import annotations

import argparse
import json
import math
import mmap
import os
import re
import shutil
import struct
import sys
import time
import zlib
from collections import OrderedDict, defaultdict
from concurrent.futures import ProcessPoolExecutor
from pathlib import Path
from typing import Dict, Iterable, Iterator, List, Optional, Sequence, Tuple

import mapbox_earcut
import numpy as np
from PIL import Image, ImageOps


SOURCE_TILE_RE = re.compile(
    r"^(?P<ns>[NS])(?P<lat>\d{2})(?P<ew>[EW])(?P<lng>\d{3})_(?P<lat_sub>\d+)_(?P<lng_sub>\d+)$"
)
SOURCE_TILE_DEGREES = 0.1
DEFAULT_SPLIT = 4
WGS84_A = 6378137.0
WGS84_E2 = 6.69437999014e-3
FACADE_TEXTURE_NAME = "facade_limestone_v1.jpg"
ROOF_TEXTURE_NAME = "roof_concrete_v1.jpg"
FACADE_REPEAT_METERS_X = 16.0
FACADE_REPEAT_METERS_Z = 4.0
ROOF_REPEAT_METERS = 24.0


class SrtmSampler:
    def __init__(self, directory: Path, max_open: int = 4) -> None:
        self.directory = directory
        self.max_open = max_open
        self.tiles: OrderedDict[str, Tuple[object, mmap.mmap, int]] = OrderedDict()

    def close(self) -> None:
        for handle, mapped, _ in self.tiles.values():
            mapped.close()
            handle.close()
        self.tiles.clear()

    def _open(self, name: str) -> Optional[Tuple[object, mmap.mmap, int]]:
        entry = self.tiles.pop(name, None)
        if entry is not None:
            self.tiles[name] = entry
            return entry

        path = self.directory / name
        if not path.exists():
            return None

        handle = path.open("rb")
        mapped = mmap.mmap(handle.fileno(), 0, access=mmap.ACCESS_READ)
        samples = int(round(math.sqrt(mapped.size() / 2.0)))
        if samples not in (1201, 3601) or samples * samples * 2 != mapped.size():
            mapped.close()
            handle.close()
            return None

        entry = (handle, mapped, samples)
        self.tiles[name] = entry
        while len(self.tiles) > self.max_open:
            _, old_entry = self.tiles.popitem(last=False)
            old_entry[1].close()
            old_entry[0].close()
        return entry

    def sample(self, lat: float, lng: float) -> float:
        lat_degree = math.floor(lat)
        lng_degree = math.floor(lng)
        name = srtm_name(lat_degree, lng_degree)
        entry = self._open(name)
        if entry is None:
            return 0.0

        _, mapped, samples = entry
        row = min(samples - 1.0, max(0.0, (lat_degree + 1.0 - lat) * (samples - 1)))
        col = min(samples - 1.0, max(0.0, (lng - lng_degree) * (samples - 1)))
        row0 = int(math.floor(row))
        col0 = int(math.floor(col))
        row1 = min(samples - 1, row0 + 1)
        col1 = min(samples - 1, col0 + 1)
        row_fraction = row - row0
        col_fraction = col - col0

        def read(sample_row: int, sample_col: int) -> float:
            value = struct.unpack_from(">h", mapped, ((sample_row * samples) + sample_col) * 2)[0]
            return 0.0 if value < -1000 else float(value)

        north_west = read(row0, col0)
        north_east = read(row0, col1)
        south_west = read(row1, col0)
        south_east = read(row1, col1)
        north = north_west + (north_east - north_west) * col_fraction
        south = south_west + (south_east - south_west) * col_fraction
        return north + (south - north) * row_fraction


def srtm_name(lat_degree: int, lng_degree: int) -> str:
    return (
        ("N" if lat_degree >= 0 else "S")
        + f"{abs(lat_degree):02d}"
        + ("E" if lng_degree >= 0 else "W")
        + f"{abs(lng_degree):03d}.hgt"
    )


def source_bounds(stem: str) -> Tuple[float, float, float, float]:
    match = SOURCE_TILE_RE.match(stem)
    if not match:
        raise ValueError(f"Unsupported building tile name: {stem}")
    lat_degree = int(match.group("lat")) * (1 if match.group("ns") == "N" else -1)
    lng_degree = int(match.group("lng")) * (1 if match.group("ew") == "E" else -1)
    south = lat_degree + int(match.group("lat_sub")) * SOURCE_TILE_DEGREES
    west = lng_degree + int(match.group("lng_sub")) * SOURCE_TILE_DEGREES
    return west, south, west + SOURCE_TILE_DEGREES, south + SOURCE_TILE_DEGREES


def clean_ring(ring: Sequence[Sequence[float]]) -> List[Tuple[float, float]]:
    result: List[Tuple[float, float]] = []
    for coordinate in ring:
        if len(coordinate) < 2:
            continue
        lng = float(coordinate[0])
        lat = float(coordinate[1])
        if not math.isfinite(lat) or not math.isfinite(lng):
            continue
        point = (lng, lat)
        if not result or point != result[-1]:
            result.append(point)
    if len(result) > 1 and result[0] == result[-1]:
        result.pop()
    return result if len(result) >= 3 else []


def iter_feature_polygons(feature: dict) -> Iterator[List[List[Tuple[float, float]]]]:
    geometry = feature.get("geometry") or {}
    geometry_type = geometry.get("type")
    coordinates = geometry.get("coordinates") or []
    polygons = [coordinates] if geometry_type == "Polygon" else coordinates if geometry_type == "MultiPolygon" else []
    for polygon in polygons:
        rings = [clean_ring(ring) for ring in polygon]
        rings = [ring for ring in rings if ring]
        if rings:
            yield rings


def polygon_centroid(rings: Sequence[Sequence[Tuple[float, float]]]) -> Tuple[float, float]:
    ring = rings[0]
    area_twice = 0.0
    centroid_x = 0.0
    centroid_y = 0.0
    for index, point in enumerate(ring):
        next_point = ring[(index + 1) % len(ring)]
        cross = point[0] * next_point[1] - next_point[0] * point[1]
        area_twice += cross
        centroid_x += (point[0] + next_point[0]) * cross
        centroid_y += (point[1] + next_point[1]) * cross
    if abs(area_twice) > 1e-16:
        return centroid_x / (3.0 * area_twice), centroid_y / (3.0 * area_twice)
    return (
        sum(point[0] for point in ring) / len(ring),
        sum(point[1] for point in ring) / len(ring),
    )


def clamp_height(value: object) -> float:
    try:
        height = float(value)
    except (TypeError, ValueError):
        height = 8.0
    if not math.isfinite(height):
        height = 8.0
    return min(300.0, max(2.0, height))


def meters_per_degree(lat: float) -> Tuple[float, float]:
    radians = math.radians(lat)
    lat_meters = (
        111132.92
        - 559.82 * math.cos(2.0 * radians)
        + 1.175 * math.cos(4.0 * radians)
        - 0.0023 * math.cos(6.0 * radians)
    )
    lng_meters = (
        111412.84 * math.cos(radians)
        - 93.5 * math.cos(3.0 * radians)
        + 0.118 * math.cos(5.0 * radians)
    )
    return lat_meters, lng_meters


def enu_transform(lat: float, lng: float, height: float) -> List[float]:
    latitude = math.radians(lat)
    longitude = math.radians(lng)
    sin_lat = math.sin(latitude)
    cos_lat = math.cos(latitude)
    sin_lng = math.sin(longitude)
    cos_lng = math.cos(longitude)
    prime_vertical = WGS84_A / math.sqrt(1.0 - WGS84_E2 * sin_lat * sin_lat)
    origin_x = (prime_vertical + height) * cos_lat * cos_lng
    origin_y = (prime_vertical + height) * cos_lat * sin_lng
    origin_z = (prime_vertical * (1.0 - WGS84_E2) + height) * sin_lat
    return [
        -sin_lng, cos_lng, 0.0, 0.0,
        -sin_lat * cos_lng, -sin_lat * sin_lng, cos_lat, 0.0,
        cos_lat * cos_lng, cos_lat * sin_lng, sin_lat, 0.0,
        origin_x, origin_y, origin_z, 1.0,
    ]


def append_aligned(buffer: bytearray, payload: bytes, alignment: int = 4) -> Tuple[int, int]:
    while len(buffer) % alignment:
        buffer.append(0)
    offset = len(buffer)
    buffer.extend(payload)
    return offset, len(payload)


def pack_positions(positions: Sequence[Tuple[float, float, float]]) -> bytes:
    payload = bytearray(len(positions) * 12)
    offset = 0
    for position in positions:
        struct.pack_into("<3f", payload, offset, *position)
        offset += 12
    return bytes(payload)


def pack_indices(indices: Sequence[int]) -> bytes:
    payload = bytearray(len(indices) * 4)
    offset = 0
    for index in indices:
        struct.pack_into("<I", payload, offset, index)
        offset += 4
    return bytes(payload)


def pack_texcoords(texcoords: Sequence[Tuple[float, float]]) -> bytes:
    payload = bytearray(len(texcoords) * 8)
    offset = 0
    for texcoord in texcoords:
        struct.pack_into("<2f", payload, offset, *texcoord)
        offset += 8
    return bytes(payload)


def make_glb(
    side_positions: Sequence[Tuple[float, float, float]],
    side_indices: Sequence[int],
    side_texcoords: Sequence[Tuple[float, float]],
    roof_positions: Sequence[Tuple[float, float, float]],
    roof_indices: Sequence[int],
    roof_texcoords: Sequence[Tuple[float, float]],
    side_tint: Sequence[float],
    roof_tint: Sequence[float],
) -> bytes:
    binary = bytearray()
    buffer_views = []
    accessors = []
    primitives = []

    def add_primitive(
        positions: Sequence[Tuple[float, float, float]],
        indices: Sequence[int],
        texcoords: Sequence[Tuple[float, float]],
        material: int,
    ) -> None:
        if not positions or not indices:
            return
        if len(positions) != len(texcoords):
            raise ValueError("Texture-coordinate count does not match vertex count")
        position_offset, position_length = append_aligned(binary, pack_positions(positions))
        position_view = len(buffer_views)
        buffer_views.append({"buffer": 0, "byteOffset": position_offset, "byteLength": position_length, "target": 34962})
        position_accessor = len(accessors)
        accessors.append({
            "bufferView": position_view,
            "componentType": 5126,
            "count": len(positions),
            "type": "VEC3",
            "min": [min(point[axis] for point in positions) for axis in range(3)],
            "max": [max(point[axis] for point in positions) for axis in range(3)],
        })
        index_offset, index_length = append_aligned(binary, pack_indices(indices))
        index_view = len(buffer_views)
        buffer_views.append({"buffer": 0, "byteOffset": index_offset, "byteLength": index_length, "target": 34963})
        index_accessor = len(accessors)
        accessors.append({
            "bufferView": index_view,
            "componentType": 5125,
            "count": len(indices),
            "type": "SCALAR",
            "min": [min(indices)],
            "max": [max(indices)],
        })
        texcoord_offset, texcoord_length = append_aligned(binary, pack_texcoords(texcoords))
        texcoord_view = len(buffer_views)
        buffer_views.append({
            "buffer": 0,
            "byteOffset": texcoord_offset,
            "byteLength": texcoord_length,
            "target": 34962,
        })
        texcoord_accessor = len(accessors)
        accessors.append({
            "bufferView": texcoord_view,
            "componentType": 5126,
            "count": len(texcoords),
            "type": "VEC2",
            "min": [min(point[axis] for point in texcoords) for axis in range(2)],
            "max": [max(point[axis] for point in texcoords) for axis in range(2)],
        })
        primitives.append({
            "attributes": {
                "POSITION": position_accessor,
                "TEXCOORD_0": texcoord_accessor,
            },
            "indices": index_accessor,
            "material": material,
            "mode": 4,
        })

    add_primitive(side_positions, side_indices, side_texcoords, 0)
    add_primitive(roof_positions, roof_indices, roof_texcoords, 1)
    document = {
        "asset": {"version": "2.0", "generator": "DIMP textured offline OSM building tiler"},
        "extensionsUsed": ["KHR_materials_unlit"],
        "scene": 0,
        "scenes": [{"nodes": [0]}],
        "nodes": [{"mesh": 0}],
        "meshes": [{"primitives": primitives}],
        "materials": [
            {
                "name": "photo-style building facades",
                "doubleSided": True,
                "pbrMetallicRoughness": {
                    "baseColorFactor": list(side_tint),
                    "baseColorTexture": {"index": 0, "texCoord": 0},
                    "metallicFactor": 0.0,
                    "roughnessFactor": 0.92,
                },
                "extensions": {"KHR_materials_unlit": {}},
            },
            {
                "name": "weathered concrete roofs",
                "doubleSided": True,
                "pbrMetallicRoughness": {
                    "baseColorFactor": list(roof_tint),
                    "baseColorTexture": {"index": 1, "texCoord": 0},
                    "metallicFactor": 0.0,
                    "roughnessFactor": 0.95,
                },
                "extensions": {"KHR_materials_unlit": {}},
            },
        ],
        "samplers": [
            {
                "magFilter": 9729,
                "minFilter": 9987,
                "wrapS": 10497,
                "wrapT": 33648,
            },
            {
                "magFilter": 9729,
                "minFilter": 9987,
                "wrapS": 10497,
                "wrapT": 10497,
            },
        ],
        "images": [
            {"uri": "../textures/" + FACADE_TEXTURE_NAME},
            {"uri": "../textures/" + ROOF_TEXTURE_NAME},
        ],
        "textures": [
            {"sampler": 0, "source": 0},
            {"sampler": 1, "source": 1},
        ],
        "buffers": [{"byteLength": len(binary)}],
        "bufferViews": buffer_views,
        "accessors": accessors,
    }
    json_bytes = json.dumps(document, separators=(",", ":"), ensure_ascii=True).encode("utf-8")
    json_bytes += b" " * ((4 - len(json_bytes) % 4) % 4)
    binary += b"\0" * ((4 - len(binary) % 4) % 4)
    total_length = 12 + 8 + len(json_bytes) + 8 + len(binary)
    return b"".join([
        struct.pack("<4sII", b"glTF", 2, total_length),
        struct.pack("<I4s", len(json_bytes), b"JSON"),
        json_bytes,
        struct.pack("<I4s", len(binary), b"BIN\0"),
        bytes(binary),
    ])


def make_b3dm(glb: bytes) -> bytes:
    feature_table = bytearray(b'{"BATCH_LENGTH":0}')
    while (28 + len(feature_table)) % 8:
        feature_table.append(0x20)
    payload = bytearray(glb)
    while (28 + len(feature_table) + len(payload)) % 8:
        payload.append(0)
    byte_length = 28 + len(feature_table) + len(payload)
    header = struct.pack(
        "<4sIIIIII",
        b"b3dm",
        1,
        byte_length,
        len(feature_table),
        0,
        0,
        0,
    )
    return header + bytes(feature_table) + bytes(payload)


def write_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, separators=(",", ":"), ensure_ascii=True), encoding="utf-8")
    os.replace(temporary, path)


def texture_tints(key: str) -> Tuple[List[float], List[float]]:
    palettes = (
        ([1.00, 1.00, 1.00, 1.0], [1.00, 1.00, 1.00, 1.0]),
        ([0.94, 0.97, 1.00, 1.0], [0.94, 0.96, 0.98, 1.0]),
        ([1.00, 0.95, 0.88, 1.0], [0.97, 0.93, 0.86, 1.0]),
        ([0.88, 0.91, 0.94, 1.0], [0.91, 0.91, 0.89, 1.0]),
    )
    return palettes[zlib.crc32(key.encode("ascii")) % len(palettes)]


def prepare_texture(source: Path, destination: Path, size: int) -> int:
    destination.parent.mkdir(parents=True, exist_ok=True)
    resampling = getattr(Image, "Resampling", Image).LANCZOS
    with Image.open(source) as image:
        texture = ImageOps.fit(image.convert("RGB"), (size, size), method=resampling)
        temporary = destination.with_suffix(destination.suffix + ".tmp")
        texture.save(
            temporary,
            format="JPEG",
            quality=90,
            optimize=True,
            progressive=False,
            subsampling=0,
        )
    os.replace(temporary, destination)
    return destination.stat().st_size


def make_building_mesh(
    polygons: Sequence[Tuple[List[List[Tuple[float, float]]], float]],
    center_lat: float,
    center_lng: float,
    origin_height: float,
    sampler: SrtmSampler,
    texture_key: str,
) -> Tuple[bytes, List[float], int]:
    side_positions: List[Tuple[float, float, float]] = []
    side_indices: List[int] = []
    side_texcoords: List[Tuple[float, float]] = []
    roof_positions: List[Tuple[float, float, float]] = []
    roof_indices: List[int] = []
    roof_texcoords: List[Tuple[float, float]] = []
    lat_meters, lng_meters = meters_per_degree(center_lat)
    min_x = min_y = min_z = math.inf
    max_x = max_y = max_z = -math.inf
    building_count = 0

    def local(point: Tuple[float, float], altitude: float) -> Tuple[float, float, float]:
        return (
            (point[0] - center_lng) * lng_meters,
            (point[1] - center_lat) * lat_meters,
            altitude - origin_height,
        )

    for rings, height in polygons:
        centroid_lng, centroid_lat = polygon_centroid(rings)
        base_height = sampler.sample(centroid_lat, centroid_lng)
        roof_height = base_height + height
        flat_vertices: List[Tuple[float, float]] = []
        ring_ends: List[int] = []
        for ring in rings:
            flat_vertices.extend(ring)
            ring_ends.append(len(flat_vertices))
            for index, point in enumerate(ring):
                next_point = ring[(index + 1) % len(ring)]
                point_base = local(point, base_height)
                next_base = local(next_point, base_height)
                next_roof = local(next_point, roof_height)
                point_roof = local(point, roof_height)
                base = len(side_positions)
                side_positions.extend([
                    point_base,
                    next_base,
                    next_roof,
                    point_roof,
                ])
                wall_width = math.hypot(next_base[0] - point_base[0], next_base[1] - point_base[1])
                wall_u = max(0.05, wall_width / FACADE_REPEAT_METERS_X)
                wall_v = max(0.05, height / FACADE_REPEAT_METERS_Z)
                side_texcoords.extend([
                    (0.0, wall_v),
                    (wall_u, wall_v),
                    (wall_u, 0.0),
                    (0.0, 0.0),
                ])
                side_indices.extend([base, base + 1, base + 2, base, base + 2, base + 3])

        if len(flat_vertices) < 3:
            continue
        vertices_array = np.asarray(flat_vertices, dtype=np.float64)
        ring_ends_array = np.asarray(ring_ends, dtype=np.uint32)
        try:
            triangulated = mapbox_earcut.triangulate_float64(vertices_array, ring_ends_array)
        except (ValueError, RuntimeError):
            continue
        roof_base = len(roof_positions)
        for point in flat_vertices:
            roof_point = local(point, roof_height)
            roof_positions.append(roof_point)
            roof_texcoords.append((
                roof_point[0] / ROOF_REPEAT_METERS,
                -roof_point[1] / ROOF_REPEAT_METERS,
            ))
        roof_indices.extend(roof_base + int(index) for index in triangulated)
        building_count += 1

    all_positions = side_positions + roof_positions
    if not all_positions or not side_indices or not roof_indices:
        raise ValueError("No renderable building geometry")
    for x, y, z in all_positions:
        min_x, min_y, min_z = min(min_x, x), min(min_y, y), min(min_z, z)
        max_x, max_y, max_z = max(max_x, x), max(max_y, y), max(max_z, z)
    side_tint, roof_tint = texture_tints(texture_key)
    glb = make_glb(
        side_positions,
        side_indices,
        side_texcoords,
        roof_positions,
        roof_indices,
        roof_texcoords,
        side_tint,
        roof_tint,
    )
    box = [
        (min_x + max_x) / 2.0,
        (min_y + max_y) / 2.0,
        (min_z + max_z) / 2.0,
        (max_x - min_x) / 2.0, 0.0, 0.0,
        0.0, (max_y - min_y) / 2.0, 0.0,
        0.0, 0.0, (max_z - min_z) / 2.0,
    ]
    return make_b3dm(glb), box, building_count


def process_source_tile(arguments: Tuple[str, str, str, int]) -> Optional[dict]:
    source_text, output_text, srtm_text, split = arguments
    source = Path(source_text)
    output = Path(output_text)
    sampler = SrtmSampler(Path(srtm_text))
    west, south, east, north = source_bounds(source.stem)
    sub_degrees = SOURCE_TILE_DEGREES / split

    try:
        document = json.loads(source.read_text(encoding="utf-8"))
        buckets: Dict[Tuple[int, int], List[Tuple[List[List[Tuple[float, float]]], float]]] = defaultdict(list)
        for feature in document.get("features", []):
            height = clamp_height((feature.get("properties") or {}).get("height", 8.0))
            for rings in iter_feature_polygons(feature):
                centroid_lng, centroid_lat = polygon_centroid(rings)
                sub_x = min(split - 1, max(0, int(math.floor((centroid_lng - west) / sub_degrees))))
                sub_y = min(split - 1, max(0, int(math.floor((centroid_lat - south) / sub_degrees))))
                buckets[(sub_x, sub_y)].append((rings, height))

        children = []
        total_buildings = 0
        total_bytes = 0
        for (sub_x, sub_y), polygons in sorted(buckets.items(), key=lambda item: (item[0][1], item[0][0])):
            sub_west = west + sub_x * sub_degrees
            sub_south = south + sub_y * sub_degrees
            sub_east = sub_west + sub_degrees
            sub_north = sub_south + sub_degrees
            center_lng = (sub_west + sub_east) / 2.0
            center_lat = (sub_south + sub_north) / 2.0
            origin_height = sampler.sample(center_lat, center_lng)
            content_name = f"{source.stem}_{sub_y}_{sub_x}.b3dm"
            try:
                content, box, building_count = make_building_mesh(
                    polygons,
                    center_lat,
                    center_lng,
                    origin_height,
                    sampler,
                    content_name,
                )
            except ValueError:
                continue

            content_path = output / "content" / content_name
            content_path.parent.mkdir(parents=True, exist_ok=True)
            temporary = content_path.with_suffix(".b3dm.tmp")
            temporary.write_bytes(content)
            os.replace(temporary, content_path)
            total_bytes += len(content)
            total_buildings += building_count
            children.append({
                "boundingVolume": {"box": box},
                "transform": enu_transform(center_lat, center_lng, origin_height),
                "geometricError": 0,
                "content": {"url": f"../content/{content_name}"},
            })

        if not children:
            return None
        external = {
            "asset": {"version": "1.0", "gltfUpAxis": "Z"},
            "geometricError": 800,
            "root": {
                "boundingVolume": {
                    "region": [
                        math.radians(west), math.radians(south),
                        math.radians(east), math.radians(north),
                        -500.0, 5000.0,
                    ]
                },
                "geometricError": 350,
                "refine": "ADD",
                "children": children,
            },
        }
        external_name = f"{source.stem}.json"
        write_json(output / "tilesets" / external_name, external)
        return {
            "name": source.stem,
            "url": f"tilesets/{external_name}",
            "bounds": [west, south, east, north],
            "buildings": total_buildings,
            "bytes": total_bytes,
            "subtiles": len(children),
        }
    finally:
        sampler.close()


def make_main_tileset(results: Sequence[dict]) -> dict:
    west = min(result["bounds"][0] for result in results)
    south = min(result["bounds"][1] for result in results)
    east = max(result["bounds"][2] for result in results)
    north = max(result["bounds"][3] for result in results)
    children = []
    for result in sorted(results, key=lambda value: value["name"]):
        child_west, child_south, child_east, child_north = result["bounds"]
        children.append({
            "boundingVolume": {
                "region": [
                    math.radians(child_west), math.radians(child_south),
                    math.radians(child_east), math.radians(child_north),
                    -500.0, 5000.0,
                ]
            },
            "geometricError": 800,
            "content": {"url": result["url"]},
        })
    return {
        "asset": {"version": "1.0", "gltfUpAxis": "Z"},
        "geometricError": 1000000,
        "root": {
            "boundingVolume": {
                "region": [
                    math.radians(west), math.radians(south),
                    math.radians(east), math.radians(north),
                    -500.0, 5000.0,
                ]
            },
            "geometricError": 250000,
            "refine": "ADD",
            "children": children,
        },
    }


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input-dir", type=Path, default=Path("map3d/buildings"))
    parser.add_argument("--output-dir", type=Path, default=Path("map3d/buildings3d"))
    parser.add_argument("--srtm-dir", type=Path, required=True)
    parser.add_argument(
        "--facade-texture",
        type=Path,
        default=Path("map3d/textures/facade_limestone_v1.png"),
    )
    parser.add_argument(
        "--roof-texture",
        type=Path,
        default=Path("map3d/textures/roof_concrete_v1.png"),
    )
    parser.add_argument("--texture-size", type=int, default=1024)
    parser.add_argument("--split", type=int, default=DEFAULT_SPLIT)
    parser.add_argument("--jobs", type=int, default=max(1, min(4, os.cpu_count() or 1)))
    parser.add_argument("--limit-files", type=int, default=0)
    parser.add_argument("--clean", action="store_true")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    if args.split < 1 or args.split > 20:
        print("--split must be between 1 and 20", file=sys.stderr)
        return 2
    if not args.srtm_dir.exists():
        print(f"Missing SRTM directory: {args.srtm_dir}", file=sys.stderr)
        return 2
    if not args.facade_texture.is_file():
        print(f"Missing facade texture: {args.facade_texture}", file=sys.stderr)
        return 2
    if not args.roof_texture.is_file():
        print(f"Missing roof texture: {args.roof_texture}", file=sys.stderr)
        return 2
    if args.texture_size < 64 or args.texture_size > 2048 or args.texture_size & (args.texture_size - 1):
        print("--texture-size must be a power of two between 64 and 2048", file=sys.stderr)
        return 2
    if args.clean and args.output_dir.exists():
        shutil.rmtree(args.output_dir)
    args.output_dir.mkdir(parents=True, exist_ok=True)
    texture_bytes = prepare_texture(
        args.facade_texture,
        args.output_dir / "textures" / FACADE_TEXTURE_NAME,
        args.texture_size,
    )
    texture_bytes += prepare_texture(
        args.roof_texture,
        args.output_dir / "textures" / ROOF_TEXTURE_NAME,
        args.texture_size,
    )

    sources = sorted(
        path for path in args.input_dir.glob("*.json")
        if path.name != "manifest.json" and SOURCE_TILE_RE.match(path.stem)
    )
    if args.limit_files:
        sources = sources[:args.limit_files]
    if not sources:
        print(f"No building GeoJSON tiles found in {args.input_dir}", file=sys.stderr)
        return 2

    started = time.time()
    work = [(str(path), str(args.output_dir), str(args.srtm_dir), args.split) for path in sources]
    results: List[dict] = []
    with ProcessPoolExecutor(max_workers=args.jobs) as executor:
        for index, result in enumerate(executor.map(process_source_tile, work, chunksize=1), 1):
            if result is not None:
                results.append(result)
            if index == 1 or index % 25 == 0 or index == len(work):
                print(f"  processed {index:,}/{len(work):,} source tiles", flush=True)

    if not results:
        print("No 3D building content was generated", file=sys.stderr)
        return 3
    write_json(args.output_dir / "tileset.json", make_main_tileset(results))
    manifest = {
        "format": "dimp-map3d-buildings-3dtiles-v2-textured",
        "source": "OpenStreetMap building footprints",
        "attribution": "OpenStreetMap contributors",
        "materials": {
            "facade": FACADE_TEXTURE_NAME,
            "roof": ROOF_TEXTURE_NAME,
            "textureSize": args.texture_size,
            "license": "Original DIMP-generated texture assets",
        },
        "sourceTileDegrees": SOURCE_TILE_DEGREES,
        "subtileDegrees": SOURCE_TILE_DEGREES / args.split,
        "sourceTiles": len(results),
        "contentTiles": sum(result["subtiles"] for result in results),
        "buildings": sum(result["buildings"] for result in results),
        "contentBytes": sum(result["bytes"] for result in results),
        "textureBytes": texture_bytes,
        "seconds": round(time.time() - started, 1),
    }
    write_json(args.output_dir / "manifest.json", manifest)
    print(json.dumps(manifest, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
