using HandheldCompanion.Managers.Overlay.Widget;
using System.Collections.Generic;

namespace HandheldCompanion.Managers.Overlay;

public class WidgetFactory
{
    private static readonly Dictionary<string, IWidget> Widgets = new()
    {
        {"TIME", new TimeWidget()},
        {"BATT", new BatteryWidget()},
        {"VRAM", new VramWidget()},
        {"CPU", new CpuWidget()},
        {"RAM", new RamWidget()},
        {"FPS", new FPSWidget()},
        {"GPU", new GpuWidget()},
        {"AUTOTDP", new AutoTDPWidget()}
    };

    /// <summary>
    ///     Builds the widget registered under <paramref name="key"/>. Keys are matched case-insensitively
    ///     because the persisted <c>OnScreenDisplayOrder</c> setting is user-authored (its default contains "Time").
    /// </summary>
    public static void CreateWidget(string key, OverlayEntry entry, short? level = null)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        if (!Widgets.TryGetValue(key.Trim().ToUpperInvariant(), out IWidget? widget))
            return;

        widget.Build(entry, level);
    }
}