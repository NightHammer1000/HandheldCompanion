using System;
using System.Globalization;

namespace HandheldCompanion.Managers.Overlay.Widget;

/// <summary>
///     OSD row for the AutoTDP controller: a coloured state marker plus the applied wattage (MINIMAL), extended
///     with the learned baseline and the state word (FULL). Both the marker and the digits carry the state
///     colour, so the indicator survives even if the OSD font lacks the block glyph.
/// </summary>
public class AutoTDPWidget : IWidget
{
    // U+2588 FULL BLOCK occupies exactly one character cell; swap for MarkerFallback if the RTSS OSD font lacks it
    private const string Marker = "█";
    private const string MarkerFallback = "[]";

    public void Build(OverlayEntry entry, short? level = null)
    {
        short _level = level ?? OSDManager.OverlayAutoTDPLevel;
        if (_level != WidgetLevel.MINIMAL && _level != WidgetLevel.FULL)
            return;

        AutoTDPStatus status = PerformanceManager.GetAutoTDPStatus();
        if (status.State == AutoTDPState.Disabled)
            return;

        string color = status.Phase == AutoTDPLearningPhase.ContentCapped ? OverlayColors.AUTOTDP_IDLE : StateColor(status.State);
        entry.elements.Add(new OverlayEntryElement(Marker, string.Empty, color));

        if (status.State == AutoTDPState.NoTelemetry)
        {
            entry.elements.Add(new OverlayEntryElement("--", "W", color));
            return;
        }

        entry.elements.Add(new OverlayEntryElement(status.AppliedW.ToString("00", CultureInfo.InvariantCulture), "W", color));

        if (_level == WidgetLevel.FULL)
        {
            // learned range: floor (hard limit) and heavy level, then the baseline the session was seeded from
            if (status.RangeMinW > 0 || status.RangeMaxW > 0)
                entry.elements.Add(new OverlayEntryElement(
                    (status.RangeMinW > 0 ? status.RangeMinW.ToString("0", CultureInfo.InvariantCulture) : "?") + "-" +
                    (status.RangeMaxW > 0 ? status.RangeMaxW.ToString("0", CultureInfo.InvariantCulture) : "?"), "W", OverlayColors.DEFAULT_COLOR));

            if (status.BaselineW.HasValue)
                entry.elements.Add(new OverlayEntryElement("B" + status.BaselineW.Value.ToString("0", CultureInfo.InvariantCulture), "W", OverlayColors.DEFAULT_COLOR));

            entry.elements.Add(new OverlayEntryElement(StateWord(status), string.Empty, color));

            if (!string.IsNullOrEmpty(status.EfficiencyRung))
                entry.elements.Add(new OverlayEntryElement(status.Optimizing ? status.EfficiencyRung + "?" : status.EfficiencyRung, string.Empty, OverlayColors.DEFAULT_COLOR));
            else if (status.Optimizing)
                entry.elements.Add(new OverlayEntryElement("EFF?", string.Empty, OverlayColors.DEFAULT_COLOR));
        }
    }

    private static string StateColor(AutoTDPState state)
    {
        return state switch
        {
            AutoTDPState.Learning => OverlayColors.AUTOTDP_LEARNING,
            AutoTDPState.Tracking => OverlayColors.AUTOTDP_TRACKING,
            AutoTDPState.MaxLimited => OverlayColors.AUTOTDP_LIMITED,
            _ => OverlayColors.AUTOTDP_IDLE,
        };
    }

    private static string StateWord(AutoTDPStatus status)
    {
        if (status.Phase == AutoTDPLearningPhase.ContentCapped)
            return "CAP" + Math.Round(status.ContentCapFps ?? status.Fps).ToString(CultureInfo.InvariantCulture);

        if (status.Phase is AutoTDPLearningPhase.WaitingForTarget or AutoTDPLearningPhase.CoarseDown or AutoTDPLearningPhase.CoarseRecover)
            return "COARSE";

        if (status.Phase == AutoTDPLearningPhase.FineFloor)
            return "FINE";

        string word = status.State switch
        {
            AutoTDPState.Learning => "LEARN",
            AutoTDPState.Tracking => "TRACK",
            AutoTDPState.MaxLimited => "LIMIT",
            _ => "IDLE",
        };

        return status.Capped ? word + "/CAP" : word;
    }
}
