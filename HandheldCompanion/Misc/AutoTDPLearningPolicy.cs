using System;

namespace HandheldCompanion.Misc;

/// <summary>Pure decision helpers shared by the live AutoTDP controller and deterministic tests.</summary>
internal static class AutoTDPLearningPolicy
{
    internal static bool IsStableLowerCadence(double medianFps, double targetFps, double cadenceSpread,
        double belowRelative, double maximumSpread)
    {
        return medianFps > 0
            && medianFps < targetFps - Math.Max(3.0, targetFps * belowRelative)
            && cadenceSpread <= maximumSpread;
    }

    internal static bool HasPowerResponse(double beforeFps, double afterFps, double minimumFps, double minimumRelative)
    {
        return afterFps - beforeFps > Math.Max(minimumFps, beforeFps * minimumRelative);
    }

    internal static bool IsCoarsePass(double rawFps, double trimmedFps, double targetFps,
        double passBandFps, double failBandFps)
    {
        return trimmedFps >= targetFps - passBandFps
            && rawFps >= targetFps - failBandFps;
    }

    internal static bool IsCoarseFailure(double trimmedFps, double targetFps, double failBandFps)
    {
        return trimmedFps < targetFps - failBandFps;
    }

    internal static double FineRecoveryTarget(double appliedWatts, double preDescentWatts,
        bool failedDescent, double deficitSeconds, double dwellSeconds)
    {
        if (failedDescent)
            return Math.Max(appliedWatts, preDescentWatts);

        return deficitSeconds >= dwellSeconds ? appliedWatts + 1.0 : appliedWatts;
    }

    internal static int CoarseRecoveryStep(double lastPassingWatts, double appliedWatts, int broadStep, int fineStep)
    {
        return lastPassingWatts - appliedWatts > broadStep ? broadStep : fineStep;
    }

    internal static double EfficiencyBiasedBaseline(double minimumWatts, double maximumWatts, double rangeBias)
    {
        double min = Math.Min(minimumWatts, maximumWatts);
        double max = Math.Max(minimumWatts, maximumWatts);
        return Math.Round(min + rangeBias * (max - min));
    }

    internal static double NextRevalidationInterval(double currentSeconds, double minimumSeconds, double maximumSeconds)
    {
        return Math.Min(maximumSeconds, Math.Max(minimumSeconds, currentSeconds * 2.0));
    }
}
