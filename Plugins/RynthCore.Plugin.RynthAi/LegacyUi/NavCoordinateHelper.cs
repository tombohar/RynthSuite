using System;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.LegacyUi;

internal static class NavCoordinateHelper
{
    public static bool TryGetNavCoords(RynthCoreHost host, out double northSouth, out double eastWest)
    {
        northSouth = 0;
        eastWest = 0;

        if (host.TryGetCurCoords(out northSouth, out eastWest))
            return true;

        if (host.TryGetPlayerPose(out uint fallbackCellId, out float fallbackX, out float fallbackY, out _, out _, out _, out _, out _))
            return TryConvertPoseToCoords(fallbackCellId, fallbackX, fallbackY, out northSouth, out eastWest);

        return false;
    }

    internal static bool TryConvertPoseToCoords(uint objCellId, float x, float y, out double northSouth, out double eastWest)
    {
        northSouth = 0;
        eastWest = 0;

        uint landblock = objCellId >> 16;
        if (landblock == 0)
            return false;

        int lbX = (int)((objCellId >> 24) & 0xFF);
        int lbY = (int)((objCellId >> 16) & 0xFF);

        // Match the classic radar/Coordinates() basis exactly:
        //   EW = ((Landcell >> 24) * 8 + X / 24 - 1019.5) / 10
        //   NS = (((Landcell >> 16) & 0xFF) * 8 + Y / 24 - 1019.5) / 10
        eastWest = (lbX * 8.0 + x / 24.0 - 1019.5) / 10.0;
        northSouth = (lbY * 8.0 + y / 24.0 - 1019.5) / 10.0;
        return !double.IsNaN(northSouth) && !double.IsNaN(eastWest) &&
               !double.IsInfinity(northSouth) && !double.IsInfinity(eastWest);
    }
}
