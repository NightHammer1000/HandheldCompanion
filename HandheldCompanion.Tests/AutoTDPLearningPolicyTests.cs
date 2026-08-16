using HandheldCompanion.Misc;
using Xunit;

namespace HandheldCompanion.Tests;

public class AutoTDPLearningPolicyTests
{
    [Theory]
    [InlineData(30.0, 60.0, 0.02, true)]
    [InlineData(24.0, 60.0, 0.08, true)]
    [InlineData(56.0, 60.0, 0.02, false)]
    [InlineData(30.0, 60.0, 0.15, false)]
    public void StableLowerCadenceRequiresLowerRateAndCleanPacing(double medianFps, double targetFps, double spread, bool expected)
    {
        Assert.Equal(expected, AutoTDPLearningPolicy.IsStableLowerCadence(medianFps, targetFps, spread, 0.08, 0.10));
    }

    [Theory]
    [InlineData(30.0, 30.5, false)]
    [InlineData(30.0, 31.0, false)]
    [InlineData(30.0, 31.1, true)]
    [InlineData(45.0, 46.4, true)]
    public void PowerResponseUsesAbsoluteAndRelativeHysteresis(double beforeFps, double afterFps, bool expected)
    {
        Assert.Equal(expected, AutoTDPLearningPolicy.HasPowerResponse(beforeFps, afterFps, 1.0, 0.03));
    }

    [Theory]
    [InlineData(60.0, 60.0, true, false)]
    [InlineData(53.37, 60.0, false, false)]
    [InlineData(57.0, 57.0, false, true)]
    [InlineData(59.0, 59.0, false, false)]
    public void CoarseFpsClassifiesHitchContaminatedWindowsAsUncertain(double rawFps, double trimmedFps,
        bool expectedPass, bool expectedFailure)
    {
        Assert.Equal(expectedPass, AutoTDPLearningPolicy.IsCoarsePass(rawFps, trimmedFps, 60.0, 0.6, 1.8));
        Assert.Equal(expectedFailure, AutoTDPLearningPolicy.IsCoarseFailure(trimmedFps, 60.0, 1.8));
    }

    [Theory]
    [InlineData(9.0, 10.0, true, 0.0, 10.0)]
    [InlineData(9.0, 0.0, false, 1.0, 9.0)]
    [InlineData(9.0, 0.0, false, 1.5, 10.0)]
    [InlineData(16.0, 0.0, false, 5.0, 17.0)]
    public void FineRecoveryReturnsToKnownLevelThenAddsAtMostOneWatt(double applied, double preDescent,
        bool failedDescent, double deficitSeconds, double expected)
    {
        Assert.Equal(expected, AutoTDPLearningPolicy.FineRecoveryTarget(
            applied, preDescent, failedDescent, deficitSeconds, 1.5));
    }

    [Theory]
    [InlineData(13.0, 9.0, 2)]
    [InlineData(13.0, 11.0, 1)]
    [InlineData(13.0, 12.0, 1)]
    public void CoarseRecoveryNarrowsFromTwoWattsToOne(double lastPass, double applied, int expected)
    {
        Assert.Equal(expected, AutoTDPLearningPolicy.CoarseRecoveryStep(lastPass, applied, 2, 1));
    }

    [Theory]
    [InlineData(10.0, 18.0, 12.0)]
    [InlineData(12.0, 16.0, 13.0)]
    [InlineData(12.0, 12.0, 12.0)]
    public void BaselineIsMidpointBiasedHalfwayTowardFloor(double min, double max, double expected)
    {
        Assert.Equal(expected, AutoTDPLearningPolicy.EfficiencyBiasedBaseline(min, max, 0.25));
    }

    [Theory]
    [InlineData(300.0, 600.0)]
    [InlineData(600.0, 1200.0)]
    [InlineData(1200.0, 1800.0)]
    [InlineData(1800.0, 1800.0)]
    public void FloorRevalidationBackoffCapsAtThirtyMinutes(double current, double expected)
    {
        Assert.Equal(expected, AutoTDPLearningPolicy.NextRevalidationInterval(current, 300.0, 1800.0));
    }
}
