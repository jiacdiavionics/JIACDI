#!/usr/bin/env python3
"""Smoke-test bundled DIMP SITL vehicles through their MAVLink TCP endpoint."""

import argparse
import json
import math
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import time

from pymavlink import mavutil


SENSOR_GYRO = 1
SENSOR_ACCELEROMETER = 2
SENSOR_AHRS = 2_097_152
REQUIRED_HEALTH = SENSOR_GYRO | SENSOR_ACCELEROMETER | SENSOR_AHRS
ARMED_FLAG = mavutil.mavlink.MAV_MODE_FLAG_SAFETY_ARMED
HOME = "31.9539,35.9106,800,0"


VEHICLES = (
    ("Plane", "ArduPlane.exe", "plane:dimp-airframe.json", "models/plane.parm",
     "models/skywalker_2013.json"),
    ("Copter", "ArduCopter.exe", "+", "default_params/copter.parm", None),
    ("Helicopter", "ArduHeli.exe", "heli", "default_params/copter-heli.parm", None),
    ("Rover", "ArduRover.exe", "rover", "default_params/rover.parm", None),
)

EXPECTED_PHYSICS_PARAMETERS = {
    "SIM_RATE_HZ": 1200.0,
    "SIM_SERVO_SPEED": 0.14,
    "SIM_SERVO_DELAY": 0.015,
    "SIM_SERVO_FILTER": 12.0,
    "SIM_WIND_SPD": 0.0,
    "SIM_WIND_TURB": 0.0,
}


def distance_metres(first, second):
    lat1, lon1 = map(math.radians, first)
    lat2, lon2 = map(math.radians, second)
    dlat = lat2 - lat1
    dlon = lon2 - lon1
    value = math.sin(dlat / 2) ** 2 + math.cos(lat1) * math.cos(lat2) * math.sin(dlon / 2) ** 2
    return 6_371_000 * 2 * math.atan2(math.sqrt(value), math.sqrt(1 - value))


def connect(deadline):
    last_error = None
    while time.monotonic() < deadline:
        try:
            return mavutil.mavlink_connection("tcp:127.0.0.1:5760", autoreconnect=False)
        except (ConnectionError, OSError) as error:
            last_error = error
            time.sleep(0.25)
    raise TimeoutError("SITL did not open TCP port 5760") from last_error


def text_value(message):
    value = getattr(message, "text", "")
    if isinstance(value, bytes):
        return value.decode("utf-8", "replace")
    return str(value)


def run_vehicle(sitl_root, specification, timeout):
    name, executable_name, model, defaults_name, aerodynamic_model_name = specification
    executable = sitl_root / executable_name
    defaults = sitl_root / defaults_name
    if not executable.is_file() or not defaults.is_file():
        raise FileNotFoundError(f"{name} payload is incomplete: {executable} / {defaults}")

    simulation_root = Path(tempfile.mkdtemp(prefix=f"dimp-sitl-{name.lower()}-"))
    if aerodynamic_model_name is not None:
        aerodynamic_model = sitl_root / aerodynamic_model_name
        if not aerodynamic_model.is_file():
            raise FileNotFoundError(f"{name} aerodynamic model is missing: {aerodynamic_model}")
        shutil.copy2(aerodynamic_model, simulation_root / "dimp-airframe.json")

    physics_defaults = simulation_root / "dimp-physics.parm"
    physics_defaults.write_text(
        "\n".join((
            "SIM_SERVO_SPEED 0.14",
            "SIM_SERVO_DELAY 0.015",
            "SIM_SERVO_FILTER 12",
            "SIM_WIND_SPD 0",
            "SIM_WIND_TURB 0",
            "",
        )),
        encoding="ascii",
    )
    environment = os.environ.copy()
    environment["HOME"] = str(simulation_root)
    environment["PATH"] = os.pathsep.join((str(sitl_root), str(simulation_root), environment.get("PATH", "")))
    command = (
        str(executable),
        f"-M{model}",
        f"-O{HOME}",
        "-s1",
        "--rate",
        "1200",
        "--serial0",
        "tcp:0",
        "--defaults",
        f"{defaults},{physics_defaults}",
        "--wipe",
    )
    creation_flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
    output_path = simulation_root / "startup.log"
    output_stream = output_path.open("w+", encoding="utf-8", errors="replace")
    process = subprocess.Popen(
        command,
        cwd=simulation_root,
        env=environment,
        stdin=subprocess.DEVNULL,
        stdout=output_stream,
        stderr=subprocess.STDOUT,
        creationflags=creation_flags,
    )

    connection = None
    started = time.monotonic()
    deadline = started + timeout
    health = 0
    gps_fix = 0
    ever_armed = False
    maximum_speed = 0.0
    maximum_ready_speed = 0.0
    first_position = None
    maximum_movement = 0.0
    calibration_warning = False
    messages = []
    ready_at = None
    physics_parameters = {}

    try:
        connection = connect(deadline)
        connection.mav.heartbeat_send(
            mavutil.mavlink.MAV_TYPE_GCS,
            mavutil.mavlink.MAV_AUTOPILOT_INVALID,
            0,
            0,
            0,
        )
        connection.mav.request_data_stream_send(
            1,
            0,
            mavutil.mavlink.MAV_DATA_STREAM_ALL,
            10,
            1,
        )
        for parameter in EXPECTED_PHYSICS_PARAMETERS:
            connection.mav.param_request_read_send(1, 1, parameter.encode("ascii"), -1)
        last_gcs_heartbeat = time.monotonic()
        while time.monotonic() < deadline:
            if process.poll() is not None:
                raise RuntimeError(f"{name} exited with code {process.returncode}")

            if time.monotonic() - last_gcs_heartbeat >= 1:
                connection.mav.heartbeat_send(
                    mavutil.mavlink.MAV_TYPE_GCS,
                    mavutil.mavlink.MAV_AUTOPILOT_INVALID,
                    0,
                    0,
                    0,
                )
                last_gcs_heartbeat = time.monotonic()

            message = connection.recv_match(blocking=True, timeout=0.5)
            if message is None:
                continue

            message_type = message.get_type()
            if message_type == "HEARTBEAT":
                ever_armed = ever_armed or bool(message.base_mode & ARMED_FLAG)
            elif message_type == "SYS_STATUS":
                health = int(message.onboard_control_sensors_health)
            elif message_type == "GPS_RAW_INT":
                gps_fix = max(gps_fix, int(message.fix_type))
            elif message_type == "GLOBAL_POSITION_INT" and message.lat and message.lon:
                position = (message.lat / 1e7, message.lon / 1e7)
                if gps_fix >= 3:
                    if first_position is None:
                        first_position = position
                    maximum_movement = max(maximum_movement, distance_metres(first_position, position))
            elif message_type == "VFR_HUD":
                maximum_speed = max(maximum_speed, abs(float(message.groundspeed)))
                if ready_at is not None:
                    maximum_ready_speed = max(maximum_ready_speed, abs(float(message.groundspeed)))
            elif message_type == "STATUSTEXT":
                status = text_value(message)
                messages.append(status)
                calibration_warning = calibration_warning or "3D Accel calibration needed" in status
            elif message_type == "PARAM_VALUE":
                parameter_id = message.param_id
                if isinstance(parameter_id, bytes):
                    parameter_id = parameter_id.decode("ascii", "replace")
                parameter_id = str(parameter_id).rstrip("\x00")
                if parameter_id in EXPECTED_PHYSICS_PARAMETERS:
                    physics_parameters[parameter_id] = float(message.param_value)

            ready = gps_fix >= 3 and first_position is not None and health & REQUIRED_HEALTH == REQUIRED_HEALTH
            if ready and ready_at is None:
                ready_at = time.monotonic()
            if ready_at is not None and time.monotonic() - ready_at >= 3:
                break

        output_stream.flush()
        output_tail = output_path.read_text(encoding="utf-8", errors="replace").splitlines()[-12:]
        result = {
            "vehicle": name,
            "ready": ready_at is not None,
            "ready_seconds": None if ready_at is None else round(ready_at - started, 2),
            "gps_fix": gps_fix,
            "health_mask": health,
            "ever_armed": ever_armed,
            "maximum_startup_groundspeed_mps": round(maximum_speed, 3),
            "maximum_ready_groundspeed_mps": round(maximum_ready_speed, 3),
            "maximum_position_change_m": round(maximum_movement, 3),
            "accel_calibration_warning": calibration_warning,
            "physics_parameters": physics_parameters,
            "recent_status": messages[-5:],
            "process_output": output_tail if ready_at is None else [],
        }
        result["passed"] = (
            result["ready"]
            and not ever_armed
            and not calibration_warning
            and maximum_ready_speed <= 0.2
            and maximum_movement <= 2.0
            and all(
                parameter in physics_parameters
                and math.isclose(physics_parameters[parameter], expected, abs_tol=0.001)
                for parameter, expected in EXPECTED_PHYSICS_PARAMETERS.items()
            )
        )
        return result
    finally:
        if connection is not None:
            connection.close()
        if process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)
        output_stream.close()
        shutil.rmtree(simulation_root, ignore_errors=True)
        time.sleep(1)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--sitl-root", type=Path, default=Path(__file__).resolve().parents[1] / "sitl")
    parser.add_argument("--timeout", type=float, default=45)
    parser.add_argument("--vehicle", choices=[vehicle[0] for vehicle in VEHICLES])
    arguments = parser.parse_args()
    sitl_root = arguments.sitl_root.resolve()

    results = []
    selected = [vehicle for vehicle in VEHICLES if arguments.vehicle in (None, vehicle[0])]
    for vehicle in selected:
        result = run_vehicle(sitl_root, vehicle, arguments.timeout)
        results.append(result)
        print(json.dumps(result, sort_keys=True), flush=True)

    failed = [result["vehicle"] for result in results if not result["passed"]]
    if failed:
        raise SystemExit("SITL startup checks failed: " + ", ".join(failed))
    print("All bundled SITL startup checks passed.")


if __name__ == "__main__":
    main()
