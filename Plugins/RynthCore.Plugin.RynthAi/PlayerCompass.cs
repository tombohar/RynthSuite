using System;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// Your heading as a compass bearing (0 = North, clockwise), from the pose quaternion: the
/// basis of TurnToHeading, map bearings and every nav and combat steer. Host.TryGetPlayerHeading
/// is another basis (0 = East, counter-clockwise: the radar's; measured 2026-10-20, Freya's
/// quaternion 13.9 against its 76.1), so it is never compared with a bearing or turned to.
/// </summary>
internal static class PlayerCompass
{
    public static bool TryGet(RynthCoreHost host, out float heading)
    {
        heading = 0;
        if (!host.HasGetPlayerPose || !host.TryGetPlayerPose(out _, out _, out _, out _, out float qw, out _, out _, out float qz))
            return false;
        double yaw = 2.0 * Math.Atan2(qz, qw) * (180.0 / Math.PI);
        heading = (float)((-yaw % 360.0 + 720.0) % 360.0);
        return true;
    }
}
