#!/usr/bin/env python3
"""Generate DIMP's small offline UAV glTF models.

The models deliberately use only core glTF 2.0 features so the bundled Cesium 1.69
runtime can load them without external textures or network requests.
"""

from __future__ import annotations

import argparse
import json
import math
import struct
from pathlib import Path
from typing import Dict, Iterable, List, Sequence, Tuple


Vec3 = Tuple[float, float, float]


MATERIALS = {
    "body": ([0.83, 0.91, 0.96, 1.0], "OPAQUE"),
    "body_dark": ([0.12, 0.20, 0.27, 1.0], "OPAQUE"),
    "accent": ([0.94, 0.12, 0.10, 1.0], "OPAQUE"),
    "canopy": ([0.03, 0.32, 0.46, 0.92], "BLEND"),
    "rotor": ([0.10, 0.13, 0.16, 0.64], "BLEND"),
    "motor": ([0.10, 0.78, 0.52, 1.0], "OPAQUE"),
    "metal": ([0.42, 0.48, 0.54, 1.0], "OPAQUE"),
}


class MeshBuilder:
    def __init__(self) -> None:
        self.parts: Dict[str, Dict[str, list]] = {}

    def _part(self, material: str) -> Dict[str, list]:
        return self.parts.setdefault(material, {"positions": [], "normals": [], "indices": []})

    def add_triangles(
        self,
        material: str,
        positions: Sequence[Vec3],
        normals: Sequence[Vec3],
        indices: Iterable[int],
    ) -> None:
        part = self._part(material)
        base = len(part["positions"])
        part["positions"].extend(positions)
        part["normals"].extend(normals)
        part["indices"].extend(base + index for index in indices)

    def add_box(
        self,
        material: str,
        center: Vec3,
        size: Vec3,
        rotation: Vec3 = (0.0, 0.0, 0.0),
    ) -> None:
        hx, hy, hz = (value / 2.0 for value in size)
        faces = [
            ((1, 0, 0), [(hx, -hy, -hz), (hx, hy, -hz), (hx, hy, hz), (hx, -hy, hz)]),
            ((-1, 0, 0), [(-hx, hy, -hz), (-hx, -hy, -hz), (-hx, -hy, hz), (-hx, hy, hz)]),
            ((0, 1, 0), [(-hx, hy, -hz), (hx, hy, -hz), (hx, hy, hz), (-hx, hy, hz)]),
            ((0, -1, 0), [(hx, -hy, -hz), (-hx, -hy, -hz), (-hx, -hy, hz), (hx, -hy, hz)]),
            ((0, 0, 1), [(-hx, -hy, hz), (hx, -hy, hz), (hx, hy, hz), (-hx, hy, hz)]),
            ((0, 0, -1), [(-hx, hy, -hz), (hx, hy, -hz), (hx, -hy, -hz), (-hx, -hy, -hz)]),
        ]
        positions: List[Vec3] = []
        normals: List[Vec3] = []
        indices: List[int] = []
        for normal, corners in faces:
            base = len(positions)
            positions.extend(transform_point(point, center, rotation) for point in corners)
            transformed_normal = rotate_vector(normal, rotation)
            normals.extend([transformed_normal] * 4)
            indices.extend([base, base + 1, base + 2, base, base + 2, base + 3])
        self.add_triangles(material, positions, normals, indices)

    def add_cylinder(
        self,
        material: str,
        center: Vec3,
        radius: float,
        length: float,
        rotation: Vec3 = (0.0, 0.0, 0.0),
        segments: int = 20,
    ) -> None:
        positions: List[Vec3] = []
        normals: List[Vec3] = []
        indices: List[int] = []
        half = length / 2.0

        for i in range(segments):
            angle = 2.0 * math.pi * i / segments
            nx, ny = math.cos(angle), math.sin(angle)
            positions.extend([
                transform_point((radius * nx, radius * ny, -half), center, rotation),
                transform_point((radius * nx, radius * ny, half), center, rotation),
            ])
            normal = rotate_vector((nx, ny, 0.0), rotation)
            normals.extend([normal, normal])

        for i in range(segments):
            nxt = (i + 1) % segments
            a, b = i * 2, i * 2 + 1
            c, d = nxt * 2, nxt * 2 + 1
            indices.extend([a, c, d, a, d, b])

        for direction in (-1.0, 1.0):
            center_index = len(positions)
            positions.append(transform_point((0.0, 0.0, direction * half), center, rotation))
            cap_normal = rotate_vector((0.0, 0.0, direction), rotation)
            normals.append(cap_normal)
            ring_start = len(positions)
            for i in range(segments):
                angle = 2.0 * math.pi * i / segments
                positions.append(transform_point(
                    (radius * math.cos(angle), radius * math.sin(angle), direction * half),
                    center,
                    rotation,
                ))
                normals.append(cap_normal)
            for i in range(segments):
                nxt = (i + 1) % segments
                if direction > 0:
                    indices.extend([center_index, ring_start + i, ring_start + nxt])
                else:
                    indices.extend([center_index, ring_start + nxt, ring_start + i])

        self.add_triangles(material, positions, normals, indices)

    def add_ellipsoid(
        self,
        material: str,
        center: Vec3,
        radii: Vec3,
        slices: int = 24,
        stacks: int = 12,
    ) -> None:
        positions: List[Vec3] = []
        normals: List[Vec3] = []
        indices: List[int] = []
        rx, ry, rz = radii

        for stack in range(stacks + 1):
            latitude = -math.pi / 2.0 + math.pi * stack / stacks
            cos_lat = math.cos(latitude)
            sin_lat = math.sin(latitude)
            for slice_index in range(slices + 1):
                longitude = 2.0 * math.pi * slice_index / slices
                cos_lon = math.cos(longitude)
                sin_lon = math.sin(longitude)
                local = (rx * cos_lat * cos_lon, ry * cos_lat * sin_lon, rz * sin_lat)
                positions.append((center[0] + local[0], center[1] + local[1], center[2] + local[2]))
                normal = normalize((local[0] / (rx * rx), local[1] / (ry * ry), local[2] / (rz * rz)))
                normals.append(normal)

        row = slices + 1
        for stack in range(stacks):
            for slice_index in range(slices):
                a = stack * row + slice_index
                b = a + row
                indices.extend([a, b, a + 1, a + 1, b, b + 1])

        self.add_triangles(material, positions, normals, indices)

    def add_cone(
        self,
        material: str,
        center: Vec3,
        radius: float,
        length: float,
        rotation: Vec3,
        segments: int = 20,
    ) -> None:
        positions: List[Vec3] = []
        normals: List[Vec3] = []
        indices: List[int] = []
        half = length / 2.0
        tip = (0.0, 0.0, half)
        slope = radius / max(length, 0.001)

        for i in range(segments):
            angle = 2.0 * math.pi * i / segments
            nxt_angle = 2.0 * math.pi * (i + 1) / segments
            ring_a = (radius * math.cos(angle), radius * math.sin(angle), -half)
            ring_b = (radius * math.cos(nxt_angle), radius * math.sin(nxt_angle), -half)
            normal_a = normalize((math.cos(angle), math.sin(angle), slope))
            normal_b = normalize((math.cos(nxt_angle), math.sin(nxt_angle), slope))
            base = len(positions)
            positions.extend([
                transform_point(tip, center, rotation),
                transform_point(ring_a, center, rotation),
                transform_point(ring_b, center, rotation),
            ])
            normals.extend([
                rotate_vector(normalize((normal_a[0] + normal_b[0], normal_a[1] + normal_b[1], slope)), rotation),
                rotate_vector(normal_a, rotation),
                rotate_vector(normal_b, rotation),
            ])
            indices.extend([base, base + 1, base + 2])

        self.add_triangles(material, positions, normals, indices)


def normalize(value: Vec3) -> Vec3:
    length = math.sqrt(sum(component * component for component in value))
    if length <= 1e-9:
        return (0.0, 0.0, 1.0)
    return tuple(component / length for component in value)  # type: ignore[return-value]


def rotate_vector(value: Vec3, rotation: Vec3) -> Vec3:
    x, y, z = value
    rx, ry, rz = rotation

    cy, sy = math.cos(rx), math.sin(rx)
    y, z = y * cy - z * sy, y * sy + z * cy
    cy, sy = math.cos(ry), math.sin(ry)
    x, z = x * cy + z * sy, -x * sy + z * cy
    cy, sy = math.cos(rz), math.sin(rz)
    x, y = x * cy - y * sy, x * sy + y * cy
    return normalize((x, y, z))


def transform_point(value: Vec3, center: Vec3, rotation: Vec3) -> Vec3:
    x, y, z = value
    rx, ry, rz = rotation

    c, s = math.cos(rx), math.sin(rx)
    y, z = y * c - z * s, y * s + z * c
    c, s = math.cos(ry), math.sin(ry)
    x, z = x * c + z * s, -x * s + z * c
    c, s = math.cos(rz), math.sin(rz)
    x, y = x * c - y * s, x * s + y * c
    return (x + center[0], y + center[1], z + center[2])


def to_gltf_axes(value: Vec3) -> Vec3:
    """Convert authoring axes (+X forward, +Y right, +Z up) to glTF 2.0 axes."""
    return (value[1], value[2], value[0])


def add_multirotor(builder: MeshBuilder, rotor_count: int) -> None:
    arm_length = 2.0 if rotor_count == 4 else 2.25
    builder.add_ellipsoid("body", (0.0, 0.0, 0.15), (0.85, 0.62, 0.36))
    builder.add_ellipsoid("canopy", (0.35, 0.0, 0.38), (0.45, 0.38, 0.22), 20, 8)
    builder.add_cone("accent", (1.05, 0.0, 0.18), 0.28, 0.65, (0.0, math.pi / 2.0, 0.0))

    start_angle = math.pi / 4.0 if rotor_count == 4 else 0.0
    for index in range(rotor_count):
        angle = start_angle + 2.0 * math.pi * index / rotor_count
        x = math.cos(angle) * arm_length
        y = math.sin(angle) * arm_length
        builder.add_box(
            "body_dark",
            (x / 2.0, y / 2.0, 0.12),
            (arm_length, 0.12, 0.12),
            (0.0, 0.0, angle),
        )
        builder.add_cylinder("motor", (x, y, 0.22), 0.20, 0.30, segments=16)
        builder.add_cylinder("rotor", (x, y, 0.42), 0.72, 0.035, segments=28)


def build_fixed_wing() -> MeshBuilder:
    builder = MeshBuilder()
    builder.add_ellipsoid("body", (0.0, 0.0, 0.1), (2.65, 0.43, 0.48), 28, 12)
    builder.add_cone("accent", (2.85, 0.0, 0.1), 0.43, 0.80, (0.0, math.pi / 2.0, 0.0), 24)
    builder.add_box("body", (0.0, 0.0, 0.05), (1.15, 6.7, 0.14))
    builder.add_box("body", (-2.05, 0.0, 0.18), (0.72, 2.45, 0.11))
    builder.add_box("accent", (-2.05, 0.0, 0.78), (0.72, 0.10, 1.35))
    builder.add_ellipsoid("canopy", (0.85, 0.0, 0.50), (0.70, 0.30, 0.24), 20, 8)
    builder.add_cylinder("metal", (3.25, 0.0, 0.1), 0.67, 0.045, (0.0, math.pi / 2.0, 0.0), 28)
    return builder


def build_quadcopter() -> MeshBuilder:
    builder = MeshBuilder()
    add_multirotor(builder, 4)
    return builder


def build_hexacopter() -> MeshBuilder:
    builder = MeshBuilder()
    add_multirotor(builder, 6)
    return builder


def build_helicopter() -> MeshBuilder:
    builder = MeshBuilder()
    builder.add_ellipsoid("body", (0.35, 0.0, 0.35), (1.45, 0.62, 0.68), 28, 14)
    builder.add_ellipsoid("canopy", (1.05, 0.0, 0.48), (0.72, 0.52, 0.50), 22, 10)
    builder.add_cone("accent", (1.72, 0.0, 0.40), 0.42, 0.72, (0.0, math.pi / 2.0, 0.0), 22)
    builder.add_box("body_dark", (-1.65, 0.0, 0.52), (3.0, 0.24, 0.24), (0.0, 0.08, 0.0))
    builder.add_box("accent", (-3.05, 0.0, 0.92), (0.65, 0.12, 1.25))
    builder.add_cylinder("metal", (0.15, 0.0, 1.15), 0.12, 0.70, segments=16)
    builder.add_box("rotor", (0.15, 0.0, 1.52), (6.3, 0.10, 0.035), (0.0, 0.0, 0.08))
    builder.add_box("rotor", (0.15, 0.0, 1.54), (0.10, 6.3, 0.035), (0.0, 0.0, 0.08))
    builder.add_cylinder("motor", (-3.35, 0.0, 0.88), 0.14, 0.34, (math.pi / 2.0, 0.0, 0.0), 16)
    builder.add_box("rotor", (-3.35, -0.20, 0.88), (0.08, 0.035, 1.45), (0.0, 0.0, 0.32))
    builder.add_box("rotor", (-3.35, -0.22, 0.88), (0.08, 0.035, 1.45), (0.0, 0.0, math.pi / 2.0 + 0.32))
    for side in (-1.0, 1.0):
        builder.add_box("metal", (0.10, side * 0.63, -0.45), (2.3, 0.10, 0.10))
        builder.add_box("metal", (0.70, side * 0.48, -0.05), (0.10, 0.10, 0.85), (0.0, -0.35, 0.0))
        builder.add_box("metal", (-0.65, side * 0.48, -0.05), (0.10, 0.10, 0.85), (0.0, 0.35, 0.0))
    return builder


def append_aligned(buffer: bytearray, payload: bytes, alignment: int = 4) -> Tuple[int, int]:
    while len(buffer) % alignment:
        buffer.append(0)
    offset = len(buffer)
    buffer.extend(payload)
    return offset, len(payload)


def pack_floats(values: Sequence[Vec3]) -> bytes:
    payload = bytearray()
    for value in values:
        payload.extend(struct.pack("<3f", *value))
    return bytes(payload)


def pack_indices(values: Sequence[int]) -> bytes:
    return struct.pack("<" + "I" * len(values), *values)


def write_glb(path: Path, vehicle_type: str, builder: MeshBuilder) -> None:
    binary = bytearray()
    buffer_views = []
    accessors = []
    primitives = []
    material_names = list(MATERIALS)

    for material_name, part in builder.parts.items():
        positions = [to_gltf_axes(value) for value in part["positions"]]
        normals = [to_gltf_axes(value) for value in part["normals"]]
        indices: List[int] = part["indices"]
        if not positions or not indices:
            continue

        position_offset, position_length = append_aligned(binary, pack_floats(positions))
        position_view = len(buffer_views)
        buffer_views.append({"buffer": 0, "byteOffset": position_offset, "byteLength": position_length, "target": 34962})
        position_accessor = len(accessors)
        minimum = [min(value[axis] for value in positions) for axis in range(3)]
        maximum = [max(value[axis] for value in positions) for axis in range(3)]
        accessors.append({
            "bufferView": position_view,
            "componentType": 5126,
            "count": len(positions),
            "type": "VEC3",
            "min": minimum,
            "max": maximum,
        })

        normal_offset, normal_length = append_aligned(binary, pack_floats(normals))
        normal_view = len(buffer_views)
        buffer_views.append({"buffer": 0, "byteOffset": normal_offset, "byteLength": normal_length, "target": 34962})
        normal_accessor = len(accessors)
        accessors.append({
            "bufferView": normal_view,
            "componentType": 5126,
            "count": len(normals),
            "type": "VEC3",
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

        primitives.append({
            "attributes": {"POSITION": position_accessor, "NORMAL": normal_accessor},
            "indices": index_accessor,
            "material": material_names.index(material_name),
            "mode": 4,
        })

    materials = []
    for material_name in material_names:
        color, alpha_mode = MATERIALS[material_name]
        material = {
            "name": material_name,
            "pbrMetallicRoughness": {
                "baseColorFactor": color,
                "metallicFactor": 0.05 if material_name == "metal" else 0.0,
                "roughnessFactor": 0.62,
            },
            "doubleSided": material_name == "rotor",
        }
        if alpha_mode != "OPAQUE":
            material["alphaMode"] = alpha_mode
        materials.append(material)

    document = {
        "asset": {"version": "2.0", "generator": "DIMP Map3D vehicle model builder"},
        "scene": 0,
        "scenes": [{"nodes": [0]}],
        "nodes": [{"name": vehicle_type, "mesh": 0}],
        "meshes": [{"name": vehicle_type, "primitives": primitives}],
        "materials": materials,
        "buffers": [{"byteLength": len(binary)}],
        "bufferViews": buffer_views,
        "accessors": accessors,
        "extras": {"dimpVehicleType": vehicle_type, "forwardAxis": "+Z", "upAxis": "+Y"},
    }

    json_bytes = json.dumps(document, separators=(",", ":"), ensure_ascii=True).encode("utf-8")
    json_bytes += b" " * ((4 - len(json_bytes) % 4) % 4)
    binary += b"\0" * ((4 - len(binary) % 4) % 4)
    total_length = 12 + 8 + len(json_bytes) + 8 + len(binary)

    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("wb") as handle:
        handle.write(struct.pack("<4sII", b"glTF", 2, total_length))
        handle.write(struct.pack("<I4s", len(json_bytes), b"JSON"))
        handle.write(json_bytes)
        handle.write(struct.pack("<I4s", len(binary), b"BIN\0"))
        handle.write(binary)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output-dir", type=Path, default=Path("map3d/vehicles"))
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    models = {
        "fixedwing": build_fixed_wing(),
        "quadcopter": build_quadcopter(),
        "hexacopter": build_hexacopter(),
        "helicopter": build_helicopter(),
    }
    for name, builder in models.items():
        output = args.output_dir / f"{name}.glb"
        write_glb(output, name, builder)
        print(f"Wrote {output} ({output.stat().st_size:,} bytes)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
