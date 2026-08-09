using MissionPlanner.ArduPilot;
using System;

namespace MissionPlanner
{
    internal static class VehicleTelemetryValidation
    {
        internal static bool HasUsablePosition(CurrentState state)
        {
            return state != null && HasUsablePosition(state.gpsstatus, state.lat, state.lng);
        }

        internal static bool HasUsablePosition(float gpsStatus, double latitude, double longitude)
        {
            if (float.IsNaN(gpsStatus) || float.IsInfinity(gpsStatus) || gpsStatus < 3)
            {
                return false;
            }

            if (double.IsNaN(latitude) || double.IsInfinity(latitude) ||
                double.IsNaN(longitude) || double.IsInfinity(longitude))
            {
                return false;
            }

            if (latitude < -90 || latitude > 90 || longitude < -180 || longitude > 180)
            {
                return false;
            }

            return Math.Abs(latitude) > 1e-9 || Math.Abs(longitude) > 1e-9;
        }

        internal static float GetVisualGroundSpeed(CurrentState state)
        {
            if (state == null || !state.armed || float.IsNaN(state.groundspeed) ||
                float.IsInfinity(state.groundspeed))
            {
                return 0;
            }

            return Math.Max(0, state.groundspeed);
        }

        internal static float GetVisualClimbRate(CurrentState state)
        {
            if (state == null || !state.armed || float.IsNaN(state.climbrate) ||
                float.IsInfinity(state.climbrate))
            {
                return 0;
            }

            return state.climbrate;
        }
    }
}
