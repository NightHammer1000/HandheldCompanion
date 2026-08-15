using HandheldCompanion.Devices;
using HandheldCompanion.GraphicsProcessingUnit;
using HandheldCompanion.Misc;
using HandheldCompanion.Platforms.Misc;
using HandheldCompanion.Processors;
using HandheldCompanion.Shared;
using HandheldCompanion.Utils;
using RTSSSharedMemoryNET;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using static HandheldCompanion.Processors.Intel.KX;
using static HandheldCompanion.Processors.IntelProcessor;
using Timer = System.Timers.Timer;

namespace HandheldCompanion.Managers;

public static class OSPowerMode
{
    /// <summary>
    ///     Better Battery mode.
    /// </summary>
    public static Guid BetterBattery = IDevice.BetterBatteryGuid;

    /// <summary>
    ///     Better Performance mode.
    /// </summary>
    public static Guid BetterPerformance = Guid.Empty;

    /// <summary>
    ///     Best Performance mode.
    /// </summary>
    public static Guid BestPerformance = IDevice.BestPerformanceGuid;
}

public enum CPUBoostLevel
{
    Disabled = 0,
    Enabled = 1,
    Agressive = 2,
    EfficientEnabled = 3,
    EfficientAgressive = 4,
}

/// <summary>
///     Operating state of the AutoTDP controller as surfaced to the UI and OSD.
/// </summary>
public enum AutoTDPState
{
    /// <summary>The applied power profile has AutoTDP disabled.</summary>
    Disabled = 0,
    /// <summary>Enabled, but no valid frame telemetry (no RTSS hook, or frames stopped advancing).</summary>
    NoTelemetry = 1,
    /// <summary>Converging on a baseline for the current game/configuration.</summary>
    Learning = 2,
    /// <summary>Warm-started from a persisted baseline; tracking around it.</summary>
    Tracking = 3,
    /// <summary>Pinned at the maximum wattage and still short of the target.</summary>
    MaxLimited = 4,
}

/// <summary>
///     Immutable snapshot of the AutoTDP controller published on every state or applied-wattage change.
///     Wattages are the integer values applied to the hardware; <see cref="Fps"/> is the 2 s window mean.
/// </summary>
public record AutoTDPStatus(AutoTDPState State, bool Capped, double TargetFps, double Fps, double SetpointW, double AppliedW, double? BaselineW, string EfficiencyRung, bool Optimizing)
{
    public static readonly AutoTDPStatus Idle = new(AutoTDPState.Disabled, false, 0, 0, 0, 0, null, string.Empty, false);
}

public static class PerformanceManager
{
    private const short INTERVAL_DEFAULT = 3000; // default interval between value scans
    private const short INTERVAL_AUTO = 500; // sampling interval for AutoTDP (actuation is rate-limited separately)
    private const short INTERVAL_DEGRADED = 5000; // degraded interval between value scans

    private const int COUNTER_DEFAULT = 3; // default counter value

    /*
     * AutoTDP tuning constants. Time-based windows/dwells are expressed in seconds so that a 30 FPS target
     * naturally waits proportionally more frames than a 60 FPS one. Wattages are integer quanta.
     */
    private const double AUTOTDP_WINDOW_SEC = 2.0;                 // frametime window used for the control signals
    private const double AUTOTDP_SLOW_TAU_SEC = 1.5;               // EMA time constant for the "slow" fps estimate (downward decisions)
    private const double AUTOTDP_LOW_BAND_FPS = 0.5;               // below target by more than max(this, 1 %) => short of target
    private const double AUTOTDP_HIGH_BAND_FPS = 2.0;              // above target by more than max(this, 3 %) => uncapped surplus
    private const double AUTOTDP_LONGFRAME_RATIO = 1.15;           // a frame longer than target frametime x this counts as a long frame
    private const int AUTOTDP_LONGFRAME_COUNT = 1;                 // long frames in the window that constitute a deficit (capped)
    private const double AUTOTDP_P95_RATIO = 1.05;                 // p95 frametime above target x this => deficit (capped)
    private const double AUTOTDP_UNCAPPED_SEVERE_RATIO = 2.0;      // uncapped: only a frame longer than target x this counts as a stutter
    private const double AUTOTDP_UNCAPPED_P95_RATIO = 1.35;        // uncapped: p95 above target x this => deficit (uncapped frametimes jitter by design)
    private const double AUTOTDP_TAIL_CLEAN_RATIO = 1.02;          // p95 frametime at or below target x this => tail is immaculate
    private const double AUTOTDP_UP_SETTLE_SEC = 1.0;              // minimum spacing between consecutive up-steps
    private const int AUTOTDP_UP_STEP_W = 1;                       // up-step on a tail-only deficit
    private const int AUTOTDP_UP_STEP_SHORTFALL_W = 2;             // up-step when the mean fps is clearly short as well
    private const double AUTOTDP_DOWN_DWELL_INRANGE_SEC = 5.0;     // sustained headroom required before stepping down inside the known range
    private const double AUTOTDP_DOWN_SETTLE_INRANGE_SEC = 3.0;    // spacing between down-steps inside the known range
    private const double AUTOTDP_PROBE_DWELL_SEC = 15.0;           // sustained headroom required before probing below the known floor
    private const double AUTOTDP_PROBE_SETTLE_SEC = 5.0;           // spacing after a probe step
    private const double AUTOTDP_PROBE_FAIL_WINDOW_SEC = 10.0;     // a deficit within this window after a probe marks the probe as failed
    private const double AUTOTDP_PROBE_BACKOFF_MAX_SEC = 300.0;    // exponential back-off cap for repeated failed probes
    private const int AUTOTDP_PROBE_MAX_FAILURES = 3;              // consecutive failed probes after which the floor is locked for the session
    private const double AUTOTDP_FAST_DOWN_RATIO = 1.5;            // uncapped fps above target x this => 2 W down-steps
    private const float AUTOTDP_GPU_GATE_PCT = 85.0f;              // do not step down while GPU load is at or above this
    private const double AUTOTDP_CAP_DETECT_SEC = 10.0;            // fps never above target while tail clean for this long => behaviourally capped
    private const double AUTOTDP_CAP_STICKY_SEC = 30.0;            // behavioural cap detection stays latched for this long
    private const double AUTOTDP_WRITE_SPACING_SEC = 1.0;          // minimum spacing between hardware writes
    private const int AUTOTDP_MAX_STEP_W = 2;                      // maximum change per write, except a jump back to a validated hold level
    private const int AUTOTDP_MAX_JUMP_W = 4;                      // maximum jump back to the last validated hold level
    private const double AUTOTDP_HOLD_VALIDATE_SEC = 5.0;          // hold this long without deficit to remember the level as validated
    private const double AUTOTDP_CONVERGE_SEC = 20.0;              // hold this long at one applied level to converge (Learning -> Tracking)
    private const double AUTOTDP_MAXLIMITED_SEC = 3.0;             // deficit while pinned at max for this long => MaxLimited
    private const double AUTOTDP_TELEMETRY_STALE_SEC = 1.0;        // frame counter not advancing for this long => NoTelemetry
    private const double AUTOTDP_BASELINE_RESAVE_SEC = 60.0;       // minimum spacing between baseline re-serialisations while tracking
    private const double AUTOTDP_BASELINE_EWMA = 0.5;              // weight of the newest convergence in TypicalWatts
    private const int AUTOTDP_BASELINE_CAP = 16;                   // maximum baselines kept per game profile (LRU by LastUpdatedUtc)
    private const int AUTOTDP_FRAME_RING = 1024;                   // matches RTSS's shared-memory ring

    private static bool _performanceManagerEnabled = true;

    public static readonly Guid[] PowerModes = { OSPowerMode.BetterBattery, OSPowerMode.BetterPerformance, OSPowerMode.BestPerformance };

    private static readonly Timer autotdpWatchdog;
    private static readonly Timer tdpWatchdog;
    private static readonly Timer gfxWatchdog;
    private static readonly Timer cpuWatchdog;

    private static CrossThreadLock autotdpLock = new();
    private static CrossThreadLock tdpLock = new();
    private static CrossThreadLock gfxLock = new();
    private static CrossThreadLock cpuLock = new();

    private static PowerProfile? currentProfile = null;

    // used to determine relevant TDP and MSR values
    private static Processor? processor;

    /*
     * AutoTDP controller state. All fields are touched from the autotdpWatchdog tick (guarded by autotdpLock)
     * and from PowerProfileManager/RTSS callbacks; hardware writes go through the single serialised writer.
     */

    // AutoTDP - target, setpoint and actuation
    private static double AutoTDPTargetFPS;
    private static double AutoTDP;                     // continuous setpoint (W)
    private static double AutoTDPApplied;              // last successfully requested wattage (W, integer quanta)
    private static double AutoTDPMax;                  // upper bound for this session (manual slider or settings max)
    private static bool AutoTDPCapped;                 // frame rate cannot overshoot the target (limiter/VSync/behavioural)
    private static bool AutoTDPCappedByLimiter;
    private static double AutoTDPCapDetectSec, AutoTDPCapStickyUntilSec;
    private static readonly Stopwatch AutoTDPClock = Stopwatch.StartNew();
    private static double AutoTDPLastTickSec, AutoTDPLastWriteSec;
    private static Task<bool>? pendingTdpWrite;
    private static int autotdpGeneration;
    private static readonly SemaphoreSlim tdpWriteLock = new(1, 1);

    // AutoTDP - telemetry (own ring of the most recent frametimes, milliseconds, newest at head-1)
    private static readonly double[] AutoTDPFrameTimes = new double[AUTOTDP_FRAME_RING];
    private static int AutoTDPFrameHead, AutoTDPFrameCount;
    private static uint AutoTDPLastStatCount;
    private static double AutoTDPStaleSec;
    private static double AutoTDPWinFps, AutoTDPSlowFps, AutoTDPP95Ratio;
    private static int AutoTDPLongFrames, AutoTDPSevereFrames;
    private static double AutoTDPDeficitStartApplied;             // level when the current deficit began (rangeMax only grows if we had to step up)
    private static float? AutoTDPGpuLoad;

    // AutoTDP - dwell / descent memory for the current session
    private static double AutoTDPHeadroomSec, AutoTDPDeficitSec, AutoTDPHoldSec;
    private static double AutoTDPUpSettleUntilSec, AutoTDPDownSettleUntilSec;
    private static double AutoTDPRangeMinW, AutoTDPRangeMaxW;    // lowest level known to hold / heaviest level that cleared a deficit
    private static double AutoTDPFloorW;                          // lowest level known to hold; descending below it is a cautious probe
    private static double AutoTDPLastDownSec, AutoTDPDownFromW;   // last down-step: when, and from which level
    private static bool AutoTDPDownWasProbe, AutoTDPInDeficit;
    private static double AutoTDPProbeBackoffSec;
    private static int AutoTDPProbeFailures;
    private static double AutoTDPConvergeSec, AutoTDPMaxLimitedSec;
    private static bool AutoTDPConvergedAtLevel;
    private static double AutoTDPPendingW;

    // AutoTDP - session and persistence
    private static string AutoTDPSessionId = string.Empty;
    private static string AutoTDPSessionExecutable = string.Empty;
    private static Profile? AutoTDPSessionProfile;
    private static string AutoTDPResumeKey = string.Empty;        // short-gap resume (alt-tab): last key (rung-agnostic), level, memory and efficiency state
    private static double AutoTDPResumeApplied, AutoTDPResumeRangeMin, AutoTDPResumeRangeMax, AutoTDPResumeFloor, AutoTDPResumeSec;
    private static uint? AutoTDPResumeEpp;
    private static CoreParkingMode? AutoTDPResumeCore;
    private static bool[]? AutoTDPResumeTried;
    private const double AUTOTDP_RESUME_WINDOW_SEC = 300.0;
    private static string AutoTDPBaselineKey = string.Empty;
    private static AutoTDPBaseline? autoTDPBaseline;
    private static bool AutoTDPWarm;                              // a baseline was loaded or learned this session (Tracking)
    private static bool AutoTDPBaselineDirty;
    private static double AutoTDPBaselineSavedSec;

    // AutoTDP - status and trace
    private static volatile AutoTDPStatus autoTDPStatus = AutoTDPStatus.Idle;
    private static StreamWriter? autoTDPTrace;
    private static bool autoTDPTraceEnabled;
    private static readonly object autoTDPTraceLock = new();

    // powercfg
    private static Guid currentPowerMode = Guid.Empty;

    // GPU limits
    private static double FallbackGfxClock;
    private static double StoredGfxClock;
    private static bool gfxWatchdogPendingStop;
    private static int gfxWatchdogCounter;

    // TDP limits
    private static double TDPMin;
    private static double TDPMax;
    private static bool tdpWatchdogPendingStop;
    private static readonly double[] CurrentTDP = new double[5] { 0, 0, 0, 0, 0 };  // store current TDP, unused
    private static readonly double[] RequestedTDP = new double[3] { 0, 0, 0 };      // store requested TDP
    private static readonly double[] RequestedMSR = new double[2] { 0, 0 };         // last successfully written PL1/PL2 via MSR (Intel)

    private const string dllName = "WinRing0x64.dll";

    public static bool IsInitialized;
    public static event InitializedEventHandler? Initialized;
    public delegate void InitializedEventHandler(bool CanChangeTDP, bool CanChangeGPU);

    static PerformanceManager()
    {
        // initialize timer(s)
        cpuWatchdog = new Timer { Interval = INTERVAL_DEFAULT, AutoReset = true, Enabled = false };
        cpuWatchdog.Elapsed += cpuWatchdog_Elapsed;

        tdpWatchdog = new Timer { Interval = INTERVAL_DEFAULT, AutoReset = true, Enabled = false };
        tdpWatchdog.Elapsed += tdpWatchdog_Elapsed;

        gfxWatchdog = new Timer { Interval = INTERVAL_DEFAULT, AutoReset = true, Enabled = false };
        gfxWatchdog.Elapsed += gfxWatchdog_Elapsed;

        autotdpWatchdog = new Timer { Interval = INTERVAL_AUTO, AutoReset = true, Enabled = false };
        autotdpWatchdog.Elapsed += autotdpWatchdog_Elapsed;
    }

    public static void Start()
    {
        if (IsInitialized)
            return;

        // temporary values
        TDPMin = IDevice.GetCurrent().cTDP[0];
        TDPMax = IDevice.GetCurrent().cTDP[1];

        // initialize processor
        processor = Processor.GetCurrent();

        // raise events
        switch (ManagerFactory.powerProfileManager.Status)
        {
            default:
            case ManagerStatus.Initializing:
                ManagerFactory.powerProfileManager.Initialized += PowerProfileManager_Initialized;
                break;
            case ManagerStatus.Initialized:
                QueryPowerProfile();
                break;
        }

        switch (ManagerFactory.settingsManager.Status)
        {
            default:
            case ManagerStatus.Initializing:
                ManagerFactory.settingsManager.Initialized += SettingsManager_Initialized;
                break;
            case ManagerStatus.Initialized:
                QuerySettings();
                break;
        }

        switch (ManagerFactory.platformManager.Status)
        {
            default:
            case ManagerStatus.Initializing:
                ManagerFactory.platformManager.Initialized += PlatformManager_Initialized;
                break;
            case ManagerStatus.Initialized:
                QueryPlatform();
                break;
        }

        IsInitialized = true;
        Initialized?.Invoke(processor?.CanChangeTDP ?? false, processor?.CanChangeGPU ?? false);

        LogManager.LogInformation("{0} has started", "PerformanceManager");
    }

    private static void QueryPowerProfile()
    {
        // manage events
        ManagerFactory.powerProfileManager.Applied += PowerProfileManager_Applied;
        ManagerFactory.powerProfileManager.Discarded += PowerProfileManager_Discarded;

        PowerProfileManager_Applied(ManagerFactory.powerProfileManager.GetCurrent(), UpdateSource.Background);
    }

    private static void PowerProfileManager_Initialized()
    {
        QueryPowerProfile();
    }

    private static void SettingsManager_Initialized()
    {
        QuerySettings();
    }

    private static void PlatformManager_Initialized()
    {
        QueryPlatform();
    }

    private static void QueryPlatform()
    {
        // manage events
        if (PlatformManager.RTSS is not null)
        {
            PlatformManager.RTSS.Hooked += RTSS_Hooked;
            PlatformManager.RTSS.Unhooked += RTSS_Unhooked;
        }
    }

    private static void QuerySettings()
    {
        // manage events
        ManagerFactory.settingsManager.SettingValueChanged += SettingsManager_SettingValueChanged;

        // raise events
        SettingsManager_SettingValueChanged("PerformanceManagerEnabled", ManagerFactory.settingsManager.GetString("PerformanceManagerEnabled"), false, false);
        SettingsManager_SettingValueChanged("ConfigurableTDPOverrideDown", ManagerFactory.settingsManager.GetString("ConfigurableTDPOverrideDown"), false, false);
        SettingsManager_SettingValueChanged("ConfigurableTDPOverrideUp", ManagerFactory.settingsManager.GetString("ConfigurableTDPOverrideUp"), false, false);
        // DEBUG BUILD: force the AutoTDP trace on regardless of a value pinned in user.config (revert before merge)
        ManagerFactory.settingsManager.SetProperty(Settings.AutoTDPTraceEnabled, true, true);
        SettingsManager_SettingValueChanged(Settings.AutoTDPTraceEnabled, ManagerFactory.settingsManager.GetString(Settings.AutoTDPTraceEnabled), false, false);
        // AMD
        SettingsManager_SettingValueChanged("RyzenAdjCoAll", ManagerFactory.settingsManager.GetString("RyzenAdjCoAll"), false, false);
        SettingsManager_SettingValueChanged("RyzenAdjCoGfx", ManagerFactory.settingsManager.GetString("RyzenAdjCoGfx"), false, false);
        // Intel
        SettingsManager_SettingValueChanged("MsrUndervoltCore", ManagerFactory.settingsManager.GetString("MsrUndervoltCore"), false, false);
        SettingsManager_SettingValueChanged("MsrUndervoltGpu", ManagerFactory.settingsManager.GetString("MsrUndervoltGpu"), false, false);
        SettingsManager_SettingValueChanged("MsrUndervoltSoc", ManagerFactory.settingsManager.GetString("MsrUndervoltSoc"), false, false);
    }

    public static void Stop()
    {
        if (!IsInitialized)
            return;

        // halt processor
        if (processor is not null && processor.IsInitialized)
            processor.Stop();

        // halt watchdogs
        autotdpWatchdog.Stop();
        tdpWatchdog.Stop();
        gfxWatchdog.Stop();
        cpuWatchdog.Stop();

        // flush AutoTDP state
        AutoTDPEndSession();
        AutoTDPCloseTrace();

        // dismount WinRing0x64.dll, and WinRing0x64.sys hopefully...
        nint Module = GetModuleHandle(dllName);
        if (Module != IntPtr.Zero)
            FreeLibrary(Module);

        // manage events
        ManagerFactory.powerProfileManager.Applied -= PowerProfileManager_Applied;
        ManagerFactory.powerProfileManager.Discarded -= PowerProfileManager_Discarded;
        ManagerFactory.powerProfileManager.Initialized -= PowerProfileManager_Initialized;
        ManagerFactory.settingsManager.SettingValueChanged -= SettingsManager_SettingValueChanged;
        ManagerFactory.settingsManager.Initialized -= SettingsManager_Initialized;
        ManagerFactory.platformManager.Initialized -= PlatformManager_Initialized;
        if (PlatformManager.RTSS is not null)
        {
            PlatformManager.RTSS.Hooked -= RTSS_Hooked;
            PlatformManager.RTSS.Unhooked -= RTSS_Unhooked;
        }

        IsInitialized = false;

        LogManager.LogInformation("{0} has stopped", "PerformanceManager");
    }

    public static double GetMinimumTDP()
    {
        return TDPMin;
    }

    public static double GetMaximumTDP()
    {
        return TDPMax;
    }

    private static void SettingsManager_SettingValueChanged(string name, object? value, bool temporary, bool initializing)
    {
        switch (name)
        {
            case "PerformanceManagerEnabled":
                {
                    _performanceManagerEnabled = Convert.ToBoolean(value);
                    if (!_performanceManagerEnabled)
                    {
                        // stop all watchdogs and restore defaults
                        cpuWatchdog.Stop();
                        StopAutoTDPWatchdog();
                        AutoTDPEndSession();
                        StopTDPWatchdog(true);
                        StopGPUWatchdog(true);
                        RestoreTDP(true);
                        RestoreCPUClock();
                        RestoreGPUClock(true);
                        RequestCoreParkingMode(CoreParkingMode.AllCoresAuto);
                        RequestCPUCoreCount(MotherboardInfo.NumberOfCores);
                        RequestPerfBoostMode((uint)PerfBoostMode.Disabled);
                        RequestPowerMode(OSPowerMode.BetterPerformance);
                    }
                    else
                    {
                        // start the CPU watchdog and re-apply the current power profile
                        cpuWatchdog.Start();
                        if (currentProfile is not null)
                            PowerProfileManager_Applied(currentProfile, UpdateSource.Background);
                    }
                }
                break;
            case "ConfigurableTDPOverrideDown":
                {
                    double TDPmin = Convert.ToDouble(value);
                    if (TDPmin == 0 || TDPmin > TDPMax)
                        return;

                    // update value
                    TDPMin = TDPmin;

                    if (AutoTDPMax != 0d && AutoTDPMax < TDPMin)
                        AutoTDPMax = TDPMin;
                }
                break;
            case "ConfigurableTDPOverrideUp":
                {
                    double TDPmax = Convert.ToDouble(value);
                    if (TDPmax == 0 || TDPmax < TDPMin)
                        return;

                    // update value
                    TDPMax = TDPmax;

                    if (AutoTDPMax == 0d || AutoTDPMax > TDPMax)
                        AutoTDPMax = TDPMax;
                }
                break;
            case "AutoTDPTraceEnabled":
                {
                    autoTDPTraceEnabled = Convert.ToBoolean(value);
                    if (!autoTDPTraceEnabled)
                        AutoTDPCloseTrace();
                }
                break;
            case "RyzenAdjCoAll":
                {
                    if (processor is AMDProcessor AMDProcessor)
                    {
                        int steps = Convert.ToInt32(value);
                        bool output = AMDProcessor.SetCoAll(steps);
                    }
                }
                break;
            case "RyzenAdjCoGfx":
                {
                    if (processor is AMDProcessor AMDProcessor)
                    {
                        int steps = Convert.ToInt32(value);
                        bool output = AMDProcessor.SetCoGfx(steps);
                    }
                }
                break;
            case "MsrUndervoltCore":
                {
                    if (processor is IntelProcessor IntelProcessor)
                    {
                        int offsetMv = Convert.ToInt32(value);
                        bool output = IntelProcessor.SetMSRUndervolt(IntelUndervoltRail.Core, offsetMv) && IntelProcessor.SetMSRUndervolt(IntelUndervoltRail.Cache, offsetMv);
                    }
                }
                break;
            case "MsrUndervoltGpu":
                {
                    if (processor is IntelProcessor IntelProcessor)
                    {
                        int offsetMv = Convert.ToInt32(value);
                        bool output = IntelProcessor.SetMSRUndervolt(IntelUndervoltRail.Gpu, offsetMv);
                    }
                }
                break;
            case "MsrUndervoltSoc":
                {
                    if (processor is IntelProcessor IntelProcessor)
                    {
                        int offsetMv = Convert.ToInt32(value);
                        bool output = IntelProcessor.SetMSRUndervolt(IntelUndervoltRail.SystemAgent, offsetMv);
                    }
                }
                break;
        }
    }

    private static void PowerProfileManager_Applied(PowerProfile profile, UpdateSource source)
    {
        currentProfile = profile;

        if (!_performanceManagerEnabled)
            return;

        // manual TDP slider values are valid when present and at or above the configurable minimum
        bool hasManualTDP = profile.TDPOverrideValues is { Length: > 0 } && profile.TDPOverrideValues[0] >= TDPMin;
        if (profile.TDPOverrideEnabled && !hasManualTDP)
            LogManager.LogWarning("Profile {0} has invalid or missing TDP override values, using defaults", profile.Name);

        if (profile.AutoTDPEnabled)
        {
            // AutoTDP owns the power rails; the manual slider (when enabled) only caps it
            if (tdpWatchdog.Enabled)
                StopTDPWatchdog(true);

            double autoMax = profile.TDPOverrideEnabled && hasManualTDP
                ? profile.TDPOverrideValues![0]
                : ManagerFactory.settingsManager.GetDouble(Settings.ConfigurableTDPOverrideUp);
            if (autoMax <= 0)
                autoMax = TDPMax;

            AutoTDPApplyProfile(profile, Math.Clamp(autoMax, TDPMin, TDPMax));
        }
        else
        {
            StopAutoTDPWatchdog();
            AutoTDPEndSession();

            if (profile.TDPOverrideEnabled && hasManualTDP)
            {
                _ = RequestTDPAsync(profile.TDPOverrideValues!, true, null);

                if (!tdpWatchdog.Enabled)
                    StartTDPWatchdog();
            }
            else
            {
                if (tdpWatchdog.Enabled)
                    StopTDPWatchdog(true);

                RestoreTDP(true);
            }
        }

        // apply profile defined CPU
        if (profile.CPUOverrideEnabled)
        {
            RequestCPUClock(Convert.ToUInt32(profile.CPUOverrideValue));
        }
        else
        {
            // restore default GPU clock
            RestoreCPUClock();
        }

        // apply profile defined GPU
        if (profile.GPUOverrideEnabled)
        {
            RequestGPUClock(profile.GPUOverrideValue);
            StartGPUWatchdog();
        }
        else
        {
            if (gfxWatchdog.Enabled)
                StopGPUWatchdog(true);

            // restore default GPU clock
            RestoreGPUClock(true);
        }

        // apply profile defined CPU Core Parking
        RequestCoreParkingMode(EffectiveCoreMode(profile));

        // apply profile defined CPU Core Count
        if (profile.CPUCoreEnabled)
        {
            RequestCPUCoreCount(profile.CPUCoreCount);
        }
        else
        {
            // restore default CPU Core Count
            RequestCPUCoreCount(MotherboardInfo.NumberOfCores);
        }

        // apply profile define CPU Boost
        RequestPerfBoostMode((uint)profile.CPUBoostLevel);

        // apply profile Power mode
        RequestPowerMode(profile.OSPowerMode);
    }

    private static void PowerProfileManager_Discarded(PowerProfile profile, bool swapped)
    {
        // don't bother discarding settings, new one will be enforce shortly
        if (swapped)
            return;

        currentProfile = null;

        // restore default TDP
        if (profile.TDPOverrideEnabled)
        {
            StopTDPWatchdog(true);
            RestoreTDP(true);
        }

        // restore default TDP
        if (profile.AutoTDPEnabled)
        {
            StopAutoTDPWatchdog();
            AutoTDPEndSession();
            RestoreTDP(true);
        }

        // restore default CPU frequency
        if (profile.CPUOverrideEnabled)
        {
            RestoreCPUClock();
        }

        // restore default GPU frequency
        if (profile.GPUOverrideEnabled)
        {
            StopGPUWatchdog(true);
            RestoreGPUClock(true);
        }

        // restore default CPU Core Parking
        RequestCoreParkingMode(CoreParkingMode.AllCoresAuto);

        // unapply profile defined CPU Core Count
        if (profile.CPUCoreEnabled)
        {
            RequestCPUCoreCount(MotherboardInfo.NumberOfCores);
        }

        // restore profile define CPU Boost
        RequestPerfBoostMode((uint)PerfBoostMode.Disabled);

        // restore OSPowerMode.BetterPerformance 
        RequestPowerMode(OSPowerMode.BetterPerformance);
    }

    /// <summary>
    ///     Writes the default power profile's TDP to the hardware. Restores hardware only; the AutoTDP
    ///     controller's own setpoint is session state and is not touched here.
    /// </summary>
    private static void RestoreTDP(bool immediate)
    {
        PowerProfile profile = ManagerFactory.powerProfileManager.GetDefault();
        if (profile.TDPOverrideValues is not null)
            _ = RequestTDPAsync(profile.TDPOverrideValues, immediate, null);
    }

    private static void RestoreCPUClock()
    {
        RequestCPUClock(0);
    }

    private static void RestoreGPUClock(bool immediate)
    {
        RequestGPUClock(255 * 50, immediate);
    }

    #region AutoTDP controller

    /*
     * AutoTDP controller
     * ------------------
     * Sampling runs every INTERVAL_AUTO ms on autotdpWatchdog; hardware writes are rate-limited separately and go
     * through the single serialised writer (RequestTDPAsync). Telemetry is RTSS's per-frame frametime ring
     * (AppEntry.StatFrameTimeBuf), copied into a local ring each tick, from which a 2 s window mean, p95 and
     * long-frame count are derived. Frametime consistency is the primary control variable: a "deficit" fires on
     * the tail before the mean framerate moves, which is what keeps a limiter-capped target (e.g. 30/30) stable.
     *
     * Control law (asymmetric, memory-based):
     *   deficit  -> step up immediately, never gated by a down-settle. A deficit shortly after a down-step marks
     *               that descent as failed: the level above becomes the known floor, the setpoint returns to the
     *               level the descent started from, and further probing below the floor backs off exponentially.
     *               Inside a session's known heavy range the recovery jumps (bounded) toward the heaviest level
     *               that has already cleared a deficit, so a wall -> open-world transition costs frames, not seconds.
     *   headroom -> after a dwell, step down: quickly while above the known floor, cautiously (probe) below it.
     *               Headroom is a sustained uncapped surplus, or - when the framerate is capped by a limiter/VSync -
     *               an immaculate frametime tail with the GPU below saturation.
     *   otherwise-> hold.
     *
     * Learning/Tracking are persistence states, not control bounds: Tracking is Learning with a warm start from a
     * baseline stored on the game's Profile (AutoTDPBaselines). Upward correction is never limited by the stored
     * range; a heavier scene simply grows the range.
     *
     * Threading: the tick owns the controller state under autotdpLock and never blocks on any other lock (profile
     * locks are TryEnter'd, writes are fire-and-forget). Profile/RTSS callbacks take autotdpLock blocking; they are
     * short and the tick always releases promptly.
     */

    private static double AutoTDPNowSec => AutoTDPClock.Elapsed.TotalSeconds;

    /// <summary>Latest AutoTDP status snapshot; safe to read from any thread (e.g. the OSD refresh timer).</summary>
    public static AutoTDPStatus GetAutoTDPStatus() => autoTDPStatus;

    /// <summary>
    ///     Discards the learned baseline for the current game/configuration and restarts learning from the
    ///     session maximum. Bound to the Relearn button on the quick performance page.
    /// </summary>
    public static void RelearnAutoTDP()
    {
        if (string.IsNullOrEmpty(AutoTDPSessionId))
            return;

        autotdpLock.Enter();
        try
        {
            // drop this game/target's baselines for every efficiency rung, then start over at the configured rung
            Profile? profile = AutoTDPSessionProfile;
            if (profile is not null && !string.IsNullOrEmpty(AutoTDPBaselineKey))
            {
                string identity = AutoTDPKeyWithoutRung(AutoTDPBaselineKey);
                int removed;
                lock (profile.SyncRoot)
                {
                    List<string> keys = profile.AutoTDPBaselines.Keys.Where(k => AutoTDPKeyWithoutRung(k) == identity).ToList();
                    removed = keys.Count;
                    foreach (string k in keys)
                        profile.AutoTDPBaselines.Remove(k);
                }

                if (removed > 0)
                    ManagerFactory.profileManager.SerializeProfile(profile);
            }

            AutoTDPResumeKey = string.Empty;

            if (AutoTDPEfficiencyActive || effPhase != AutoTDPEfficiencyPhase.Idle)
            {
                AutoTDPEfficiencyClearOverrides();
                if (currentProfile is not null)
                    RequestCoreParkingMode(EffectiveCoreMode(currentProfile));
                AutoTDPRekeyForEfficiency();
            }
            else
                AutoTDPEfficiencyClearOverrides();

            autoTDPBaseline = null;
            AutoTDPBaselineDirty = false;
            AutoTDPWarm = false;

            Interlocked.Increment(ref autotdpGeneration);
            AutoTDPResetDwell();
            AutoTDPRangeMinW = AutoTDPRangeMaxW = AutoTDPFloorW = 0;
            AutoTDPProbeFailures = 0;
            AutoTDPProbeBackoffSec = 0;
            AutoTDP = AutoTDPMax;
            AutoTDPApplied = 0; // forces an immediate write of the seed on the next tick

            LogManager.LogInformation("AutoTDP relearn requested for {0}, seeding {1} W", AutoTDPBaselineKey, AutoTDPMax);
        }
        finally
        {
            autotdpLock.Exit();
        }
    }

    /// <summary>
    ///     Entry point from <see cref="PowerProfileManager_Applied"/>. Starts a new controller session when the
    ///     game, power profile, power source or fingerprint changed; re-targets softly when only the requested FPS
    ///     moved (the slider re-applies the profile on every tick); otherwise only refreshes the ceiling.
    /// </summary>
    private static void AutoTDPApplyProfile(PowerProfile profile, double autoMax)
    {
        string executable = AutoTDPCurrentExecutable();
        string sessionId = AutoTDPBuildSessionId(profile, executable);
        string key = AutoTDPBuildKey(sessionId, profile.AutoTDPRequestedFPS);

        autotdpLock.Enter();
        try
        {
            AutoTDPMax = autoMax;

            bool sameSession = !string.IsNullOrEmpty(AutoTDPSessionId) && string.Equals(sessionId, AutoTDPSessionId, StringComparison.Ordinal);
            if (!sameSession)
            {
                AutoTDPEndSessionCore();
                AutoTDPBeginSession(sessionId, key, profile.AutoTDPRequestedFPS, executable);
            }
            else if (!string.Equals(key, AutoTDPBaselineKey, StringComparison.Ordinal))
            {
                AutoTDPRetarget(key, profile.AutoTDPRequestedFPS);
            }
            else
            {
                AutoTDP = Math.Min(AutoTDP, AutoTDPMax);
            }
        }
        finally
        {
            autotdpLock.Exit();
        }

        if (!autotdpWatchdog.Enabled)
            StartAutoTDPWatchdog();
    }

    private static void AutoTDPBeginSession(string sessionId, string key, float targetFps, string executable)
    {
        Interlocked.Increment(ref autotdpGeneration);

        AutoTDPSessionId = sessionId;
        AutoTDPSessionExecutable = executable;
        AutoTDPSessionProfile = ManagerFactory.profileManager.GetCurrent();
        AutoTDPTargetFPS = targetFps;
        AutoTDPApplied = 0;
        AutoTDPLastWriteSec = 0;
        AutoTDPLastTickSec = 0;
        AutoTDPCapped = AutoTDPCappedByLimiter = false;
        AutoTDPCapDetectSec = AutoTDPCapStickyUntilSec = 0;

        AutoTDPResetTelemetry();
        AutoTDPResetDwell();
        AutoTDPLoadBaseline(key);
        AutoTDPSeedFromBaseline();

        // the same game coming straight back (alt-tab, overlay) continues where it left off instead of re-seeding,
        // including any efficiency step it had already committed
        bool resumed = string.Equals(AutoTDPKeyWithoutRung(key), AutoTDPResumeKey, StringComparison.Ordinal) && AutoTDPResumeApplied > 0 && AutoTDPNowSec - AutoTDPResumeSec < AUTOTDP_RESUME_WINDOW_SEC;
        if (resumed)
        {
            if (AutoTDPResumeEpp.HasValue || AutoTDPResumeCore.HasValue)
            {
                effAcceptedEpp = AutoTDPResumeEpp;
                effAcceptedCore = AutoTDPResumeCore;
                if (AutoTDPResumeTried is not null)
                    Array.Copy(AutoTDPResumeTried, effTried, Math.Min(effTried.Length, AutoTDPResumeTried.Length));
                AutoTDPEfficiencySetState(effAcceptedEpp, effAcceptedCore);
                AutoTDPRekeyForEfficiency();
            }

            AutoTDP = Math.Clamp(AutoTDPResumeApplied, TDPMin, Math.Max(TDPMin, AutoTDPMax));
            AutoTDPRangeMinW = AutoTDPResumeRangeMin;
            AutoTDPRangeMaxW = AutoTDPResumeRangeMax;
            AutoTDPFloorW = AutoTDPResumeFloor;
        }

        bool hooked = PlatformManager.RTSS?.HasHook() ?? false;
        if (!hooked)
            RestoreTDP(true);

        LogManager.LogInformation("AutoTDP session started: key={0}, seed={1} W, max={2} W, warm={3}, resumed={4}", key, AutoTDP, AutoTDPMax, AutoTDPWarm, resumed);
        AutoTDPPublish(hooked ? (AutoTDPWarm ? AutoTDPState.Tracking : AutoTDPState.Learning) : AutoTDPState.NoTelemetry, AutoTDPNowSec, true);
    }

    private static void AutoTDPRetarget(string key, float targetFps)
    {
        AutoTDPSaveBaseline(true);

        AutoTDPTargetFPS = targetFps;
        AutoTDPCapped = AutoTDPCappedByLimiter = false;
        AutoTDPCapDetectSec = AutoTDPCapStickyUntilSec = 0;

        AutoTDPResetDwell();
        AutoTDPLoadBaseline(key);

        // the current level is the only known point for the new target unless a baseline exists
        if (autoTDPBaseline is null)
        {
            double level = AutoTDPApplied > 0 ? AutoTDPApplied : AutoTDP;
            AutoTDPRangeMinW = AutoTDPRangeMaxW = 0;
            AutoTDPFloorW = 0;
            AutoTDP = Math.Max(AutoTDP, level);
        }

        LogManager.LogInformation("AutoTDP re-targeted: key={0}, setpoint={1} W, warm={2}", key, AutoTDP, AutoTDPWarm);
    }

    /// <summary>Ends the controller session (persisting a dirty baseline) and publishes <see cref="AutoTDPState.Disabled"/>.</summary>
    private static void AutoTDPEndSession()
    {
        if (string.IsNullOrEmpty(AutoTDPSessionId))
            return;

        autotdpLock.Enter();
        try
        {
            AutoTDPEndSessionCore();
        }
        finally
        {
            autotdpLock.Exit();
        }
    }

    private static void AutoTDPEndSessionCore()
    {
        if (string.IsNullOrEmpty(AutoTDPSessionId))
            return;

        AutoTDPSaveBaseline(true);
        Interlocked.Increment(ref autotdpGeneration);

        // remember where this key was, so a short interruption resumes rather than re-seeds
        AutoTDPResumeKey = AutoTDPKeyWithoutRung(AutoTDPBaselineKey);
        AutoTDPResumeApplied = AutoTDPApplied;
        AutoTDPResumeRangeMin = AutoTDPRangeMinW;
        AutoTDPResumeRangeMax = AutoTDPRangeMaxW;
        AutoTDPResumeFloor = AutoTDPFloorW;
        AutoTDPResumeEpp = effAcceptedEpp;
        AutoTDPResumeCore = effAcceptedCore;
        AutoTDPResumeTried = (bool[])effTried.Clone();
        AutoTDPResumeSec = AutoTDPNowSec;

        AutoTDPEfficiencyClearOverrides();

        AutoTDPSessionId = string.Empty;
        AutoTDPSessionExecutable = string.Empty;
        AutoTDPSessionProfile = null;
        AutoTDPBaselineKey = string.Empty;
        autoTDPBaseline = null;
        AutoTDPBaselineDirty = false;
        AutoTDPWarm = false;
        AutoTDPApplied = 0;

        AutoTDPResetTelemetry();
        AutoTDPResetDwell();
        AutoTDPRangeMinW = AutoTDPRangeMaxW = AutoTDPFloorW = 0;
        AutoTDPProbeFailures = 0;
        AutoTDPProbeBackoffSec = 0;

        autoTDPStatus = AutoTDPStatus.Idle;
        AutoTDPStatusChanged?.Invoke(autoTDPStatus);
    }

    /// <summary>Executable that identifies the game for baseline purposes: the foreground process, else whatever RTSS has hooked.</summary>
    private static string AutoTDPCurrentExecutable()
    {
        string executable = ProcessManager.GetCurrent()?.Executable ?? string.Empty;
        if (string.IsNullOrEmpty(executable))
        {
            string? hooked = PlatformManager.RTSS?.GetAppEntry()?.Name;
            if (!string.IsNullOrEmpty(hooked))
                executable = Path.GetFileName(hooked);
        }
        return executable;
    }

    private static void RTSS_Hooked(AppEntry appEntry)
    {
        if (string.IsNullOrEmpty(AutoTDPSessionId))
            return;

        autotdpLock.Enter();
        try
        {
            // fresh telemetry for the (re)hooked process; resume from the current setpoint, re-applied on the next tick
            AutoTDPResetTelemetry();
            AutoTDPResetDwell();
            AutoTDPApplied = 0;
            AutoTDPLastWriteSec = 0;

            // a session that started before the game was known adopts the hooked executable in place, keeping what it learned
            if (string.IsNullOrEmpty(AutoTDPSessionExecutable) && currentProfile is not null && !string.IsNullOrEmpty(appEntry?.Name))
            {
                AutoTDPSessionExecutable = Path.GetFileName(appEntry.Name);
                string sessionId = AutoTDPBuildSessionId(currentProfile, AutoTDPSessionExecutable);
                string key = AutoTDPBuildKey(sessionId, (float)AutoTDPTargetFPS);

                double rangeMin = AutoTDPRangeMinW, rangeMax = AutoTDPRangeMaxW, floor = AutoTDPFloorW;
                AutoTDPSessionId = sessionId;
                AutoTDPRetarget(key, (float)AutoTDPTargetFPS);
                if (autoTDPBaseline is null)
                {
                    AutoTDPRangeMinW = rangeMin;
                    AutoTDPRangeMaxW = rangeMax;
                    AutoTDPFloorW = floor;
                }
            }
        }
        finally
        {
            autotdpLock.Exit();
        }
    }

    private static void RTSS_Unhooked(int processId)
    {
        if (string.IsNullOrEmpty(AutoTDPSessionId))
            return;

        autotdpLock.Enter();
        try
        {
            AutoTDPSaveBaseline(true);
            AutoTDPResetTelemetry();
            AutoTDPApplied = 0;
            RestoreTDP(true);
            AutoTDPPublish(AutoTDPState.NoTelemetry, AutoTDPNowSec, true);
        }
        finally
        {
            autotdpLock.Exit();
        }
    }

    private static void autotdpWatchdog_Elapsed(object? sender, ElapsedEventArgs e)
    {
        if (!_performanceManagerEnabled)
            return;

        if (processor is null || !processor.IsInitialized)
            return;

        // we're not ready yet
        if (!ManagerFactory.platformManager.IsReady)
            return;

        if (string.IsNullOrEmpty(AutoTDPSessionId))
            return;

        if (!autotdpLock.TryEnter())
            return;

        try
        {
            double now = AutoTDPNowSec;
            double dt = AutoTDPLastTickSec == 0 ? INTERVAL_AUTO / 1000.0 : Math.Clamp(now - AutoTDPLastTickSec, 0.05, 2.0);
            AutoTDPLastTickSec = now;

            AutoTDPCollectWrite();

            string reason;
            if (!AutoTDPSampleTelemetry(dt))
            {
                reason = "notelemetry";
                AutoTDPPublish(AutoTDPState.NoTelemetry, now, false);
                AutoTDPTrace(now, reason);
                return;
            }

            AutoTDPDetectCap(now, dt);
            reason = AutoTDPStep(now, dt);
            AutoTDPActuate(now, reason);
            AutoTDPMaintainMSR();
            AutoTDPUpdateState(now, dt, reason);
            AutoTDPEfficiencyStep(now, dt, AutoTDPInDeficit);
            AutoTDPTrace(now, reason);
        }
        catch (Exception ex)
        {
            LogManager.LogWarning("AutoTDP tick failed: {0}", ex.Message);
        }
        finally
        {
            autotdpLock.Exit();
        }
    }

    /// <summary>
    ///     Pulls new frametime samples from RTSS into the local ring and refreshes the window statistics.
    ///     Returns <c>false</c> when there is no hook, no entry, or frames stopped advancing for longer than
    ///     <see cref="AUTOTDP_TELEMETRY_STALE_SEC"/> (loading screens, minimised games, dead hooks).
    /// </summary>
    private static bool AutoTDPSampleTelemetry(double dt)
    {
        RTSSPlatform? rtss = PlatformManager.RTSS;
        if (rtss is null || !rtss.HasHook())
        {
            AutoTDPStaleSec = AUTOTDP_TELEMETRY_STALE_SEC;
            return false;
        }

        rtss.RefreshAppEntry();
        AppEntry? entry = rtss.GetAppEntry();
        if (entry?.StatFrameTimeBuf is null || entry.StatFrameTimeBuf.Length < AUTOTDP_FRAME_RING)
            return false;

        uint count = entry.StatFrameTimeCount;
        uint delta = unchecked(count - AutoTDPLastStatCount);
        if (AutoTDPLastStatCount == 0 || delta > (uint)AUTOTDP_FRAME_RING)
        {
            // first sample of the session (or a discontinuity): take just enough history for one window
            int want = (int)Math.Ceiling(AUTOTDP_WINDOW_SEC * Math.Max(30.0, AutoTDPTargetFPS));
            delta = (uint)Math.Min(Math.Min(count, (uint)AUTOTDP_FRAME_RING), (uint)want);
        }

        if (delta == 0)
        {
            AutoTDPStaleSec += dt;
            if (AutoTDPStaleSec >= AUTOTDP_TELEMETRY_STALE_SEC)
                return false;
        }
        else
        {
            AutoTDPStaleSec = 0;

            // StatFrameTimeBufPos is the next-write slot; the newest sample sits at (pos - 1) & 1023
            uint pos = entry.StatFrameTimeBufPos;
            for (uint i = 0; i < delta; i++)
            {
                int idx = (int)(unchecked(pos - delta + i) & (AUTOTDP_FRAME_RING - 1));
                double ft = entry.StatFrameTimeBuf[idx] / 1000.0;
                if (ft <= 0)
                    continue;

                AutoTDPFrameTimes[AutoTDPFrameHead] = Math.Min(ft, 1000.0);
                AutoTDPFrameHead = (AutoTDPFrameHead + 1) & (AUTOTDP_FRAME_RING - 1);
                if (AutoTDPFrameCount < AUTOTDP_FRAME_RING)
                    AutoTDPFrameCount++;
            }
        }

        AutoTDPLastStatCount = count;

        if (AutoTDPFrameCount == 0)
            return false;

        AutoTDPComputeWindow(dt);

        GPU? gpu = GPUManager.GetCurrent();
        AutoTDPGpuLoad = gpu is { IsInitialized: true } && gpu.HasLoad() ? gpu.GetLoad() : null;

        return true;
    }

    private static readonly double[] AutoTDPWindowScratch = new double[AUTOTDP_FRAME_RING];

    /// <summary>Derives window mean fps, p95 frametime ratio and long-frame count over the last <see cref="AUTOTDP_WINDOW_SEC"/> seconds of frames.</summary>
    private static void AutoTDPComputeWindow(double dt)
    {
        double ftTarget = 1000.0 / Math.Max(1.0, AutoTDPTargetFPS);
        double windowMs = AUTOTDP_WINDOW_SEC * 1000.0;

        int n = 0;
        double sum = 0;
        int longFrames = 0, severeFrames = 0;
        for (int i = 0; i < AutoTDPFrameCount && n < AUTOTDP_FRAME_RING; i++)
        {
            int idx = (AutoTDPFrameHead - 1 - i) & (AUTOTDP_FRAME_RING - 1);
            double ft = AutoTDPFrameTimes[idx];
            AutoTDPWindowScratch[n++] = ft;
            sum += ft;
            if (ft > ftTarget * AUTOTDP_LONGFRAME_RATIO)
                longFrames++;
            if (ft > ftTarget * AUTOTDP_UNCAPPED_SEVERE_RATIO)
                severeFrames++;
            if (sum >= windowMs && n >= 8)
                break;
        }

        if (n == 0)
            return;

        AutoTDPWinFps = n / (sum / 1000.0);
        AutoTDPLongFrames = longFrames;
        AutoTDPSevereFrames = severeFrames;

        Array.Sort(AutoTDPWindowScratch, 0, n);
        int p95Index = Math.Clamp((int)Math.Ceiling(0.95 * n) - 1, 0, n - 1);
        AutoTDPP95Ratio = AutoTDPWindowScratch[p95Index] / ftTarget;

        double alpha = 1.0 - Math.Exp(-dt / AUTOTDP_SLOW_TAU_SEC);
        AutoTDPSlowFps = AutoTDPSlowFps <= 0 ? AutoTDPWinFps : AutoTDPSlowFps + alpha * (AutoTDPWinFps - AutoTDPSlowFps);
    }

    /// <summary>
    ///     Decides whether the framerate is capped at the target (frame limiter set at/below it, or observed never
    ///     to exceed it while pacing is clean - VSync and similar). Capped mode switches the headroom signal from
    ///     "sustained overshoot" to "immaculate tail + GPU not saturated".
    /// </summary>
    private static void AutoTDPDetectCap(double now, double dt)
    {
        double target = AutoTDPTargetFPS;
        double highBand = Math.Max(AUTOTDP_HIGH_BAND_FPS, 0.03 * target);
        bool tailClean = AutoTDPLongFrames == 0 && AutoTDPP95Ratio <= AUTOTDP_TAIL_CLEAN_RATIO;

        int limiter = currentProfile?.FramerateValue ?? 0;
        AutoTDPCappedByLimiter = limiter > 0 && limiter <= target + highBand;

        if (!AutoTDPCappedByLimiter)
        {
            if (AutoTDPWinFps > target + 0.5)
                AutoTDPCapDetectSec = 0;
            else if (tailClean)
            {
                AutoTDPCapDetectSec += dt;
                if (AutoTDPCapDetectSec >= AUTOTDP_CAP_DETECT_SEC)
                    AutoTDPCapStickyUntilSec = now + AUTOTDP_CAP_STICKY_SEC;
            }
        }

        AutoTDPCapped = AutoTDPCappedByLimiter || now < AutoTDPCapStickyUntilSec;
    }

    /// <summary>Applies the control law to the setpoint for this tick and returns the decision taken (for the trace/status).</summary>
    private static string AutoTDPStep(double now, double dt)
    {
        double target = AutoTDPTargetFPS;
        double lowBand = Math.Max(AUTOTDP_LOW_BAND_FPS, 0.01 * target);
        double highBand = Math.Max(AUTOTDP_HIGH_BAND_FPS, 0.03 * target);

        // Capped (limiter/VSync at the target): frametimes are flat, so any long frame or p95 drift is a real deficit.
        // Uncapped: frametimes jitter by design; only the window mean and gross stutter count, otherwise the tail rule
        // would force the mean far above the target.
        bool deficit = AutoTDPCapped
            ? AutoTDPLongFrames >= AUTOTDP_LONGFRAME_COUNT || AutoTDPP95Ratio > AUTOTDP_P95_RATIO || AutoTDPWinFps < target - lowBand
            : AutoTDPWinFps < target - lowBand || AutoTDPSevereFrames >= 1 || AutoTDPP95Ratio > AUTOTDP_UNCAPPED_P95_RATIO;
        bool shortfall = AutoTDPWinFps < target - 2 * lowBand;
        bool tailClean = AutoTDPLongFrames == 0 && AutoTDPP95Ratio <= AUTOTDP_TAIL_CLEAN_RATIO;
        bool gpuOk = AutoTDPGpuLoad is null || AutoTDPGpuLoad < AUTOTDP_GPU_GATE_PCT;
        bool headroom = AutoTDPSlowFps > target + highBand || (AutoTDPCapped && tailClean && gpuOk);

        // before the first successful write the setpoint itself is the best estimate of the applied level
        double applied = AutoTDPApplied > 0 ? AutoTDPApplied : AutoTDP;
        bool recentDescent = AutoTDPLastDownSec > 0 && now - AutoTDPLastDownSec < AUTOTDP_PROBE_FAIL_WINDOW_SEC;
        string reason;

        if (deficit)
        {
            if (!AutoTDPInDeficit)
                AutoTDPDeficitStartApplied = applied;

            AutoTDPHeadroomSec = 0;
            AutoTDPHoldSec = 0;
            AutoTDPDeficitSec += dt;

            if (recentDescent)
            {
                // the level we just stepped down to cannot hold the target: it becomes the floor's lower neighbour
                AutoTDPFloorW = applied + 1;
                AutoTDPProbeFailures++;
                AutoTDPProbeBackoffSec = Math.Min(AUTOTDP_PROBE_BACKOFF_MAX_SEC, AUTOTDP_PROBE_DWELL_SEC * Math.Pow(2, AutoTDPProbeFailures));
                AutoTDPLastDownSec = 0;
                AutoTDP = Math.Max(AutoTDP, Math.Max(AutoTDPDownFromW, applied + 1));
                AutoTDPUpSettleUntilSec = now + AUTOTDP_UP_SETTLE_SEC;
                reason = "down-fail";
                LogManager.LogInformation("AutoTDP: {0} W does not hold {1} FPS, floor {2} W, next probe after {3:F0} s", applied, target, AutoTDPFloorW, AutoTDPProbeBackoffSec);
            }
            else if (now >= AutoTDPUpSettleUntilSec)
            {
                if (AutoTDPRangeMaxW > applied)
                {
                    // a heavier scene we have already handled this session: jump toward its level (bounded)
                    AutoTDP = Math.Min(AutoTDPRangeMaxW, applied + AUTOTDP_MAX_JUMP_W);
                    reason = "jump";
                }
                else
                {
                    AutoTDP = applied + (shortfall ? AUTOTDP_UP_STEP_SHORTFALL_W : AUTOTDP_UP_STEP_W);
                    reason = shortfall ? "up-shortfall" : "up";
                }
                AutoTDPUpSettleUntilSec = now + AUTOTDP_UP_SETTLE_SEC;
            }
            else
                reason = "up-settle";
        }
        else if (headroom)
        {
            AutoTDPDeficitSec = 0;
            AutoTDPHeadroomSec += dt;

            if (AutoTDPLastDownSec > 0 && !recentDescent)
                AutoTDPDescentSucceeded(applied);

            bool cautious = AutoTDPFloorW > 0 && applied - 1 < AutoTDPFloorW;
            double dwell = cautious ? Math.Max(AUTOTDP_PROBE_DWELL_SEC, AutoTDPProbeBackoffSec) : AUTOTDP_DOWN_DWELL_INRANGE_SEC;
            double settle = cautious ? AUTOTDP_PROBE_SETTLE_SEC : AUTOTDP_DOWN_SETTLE_INRANGE_SEC;
            bool probePending = cautious && recentDescent;
            bool floorLocked = cautious && AutoTDPProbeFailures >= AUTOTDP_PROBE_MAX_FAILURES;

            if (!probePending && !floorLocked && AutoTDPHeadroomSec >= dwell && now >= AutoTDPDownSettleUntilSec && now >= AutoTDPUpSettleUntilSec && AutoTDP > TDPMin + 0.5)
            {
                int step = !cautious && !AutoTDPCapped && AutoTDPSlowFps > AUTOTDP_FAST_DOWN_RATIO * target ? 2 : 1;
                AutoTDPDownFromW = applied;
                AutoTDPDownWasProbe = cautious;
                AutoTDPLastDownSec = now;
                AutoTDP = applied - step;
                AutoTDPDownSettleUntilSec = now + settle;
                reason = cautious ? "probe" : "down";
            }
            else
                reason = floorLocked ? "floor-locked" : probePending ? "probe-wait" : "headroom";
        }
        else
        {
            AutoTDPHeadroomSec = 0;
            AutoTDPDeficitSec = 0;
            AutoTDPHoldSec += dt;

            if (AutoTDPLastDownSec > 0 && !recentDescent)
                AutoTDPDescentSucceeded(applied);

            if (AutoTDPHoldSec >= AUTOTDP_HOLD_VALIDATE_SEC && !recentDescent)
            {
                if (AutoTDPRangeMinW <= 0 || applied < AutoTDPRangeMinW)
                    AutoTDPRangeMinW = applied;
            }
            reason = "hold";
        }

        // remember the level at which a deficit cleared, but only when we actually had to step up to clear it
        // (a loading screen or menu hitch at the current level says nothing about the game's heavy scenes)
        if (!deficit && AutoTDPInDeficit && applied > AutoTDPDeficitStartApplied)
            AutoTDPRangeMaxW = Math.Max(AutoTDPRangeMaxW, applied);
        AutoTDPInDeficit = deficit;

        AutoTDP = Math.Clamp(AutoTDP, TDPMin, Math.Max(TDPMin, AutoTDPMax));
        return reason;
    }

    private static void AutoTDPDescentSucceeded(double applied)
    {
        AutoTDPLastDownSec = 0;
        if (AutoTDPRangeMinW <= 0 || applied < AutoTDPRangeMinW)
            AutoTDPRangeMinW = applied;

        if (AutoTDPDownWasProbe)
        {
            AutoTDPFloorW = applied;
            AutoTDPProbeFailures = 0;
            AutoTDPProbeBackoffSec = 0;
            LogManager.LogInformation("AutoTDP: probe succeeded, {0} W holds {1} FPS", applied, AutoTDPTargetFPS);
        }
    }

    /// <summary>
    ///     Quantises the setpoint and issues at most one bounded hardware write per <see cref="AUTOTDP_WRITE_SPACING_SEC"/>,
    ///     never while a previous write is still in flight. Upward moves toward an already-validated level may exceed the
    ///     normal step cap up to <see cref="AUTOTDP_MAX_JUMP_W"/>.
    /// </summary>
    private static void AutoTDPActuate(double now, string reason)
    {
        double candidate = Math.Round(AutoTDP, MidpointRounding.AwayFromZero);
        candidate = Math.Clamp(candidate, TDPMin, Math.Max(TDPMin, AutoTDPMax));

        if (AutoTDPApplied > 0)
        {
            double delta = candidate - AutoTDPApplied;
            double maxUp = reason == "jump" || reason == "down-fail" ? AUTOTDP_MAX_JUMP_W : AUTOTDP_MAX_STEP_W;
            delta = Math.Clamp(delta, -AUTOTDP_MAX_STEP_W, maxUp);
            candidate = AutoTDPApplied + delta;
        }

        if (candidate == AutoTDPApplied)
            return;

        if (AutoTDPLastWriteSec > 0 && now - AutoTDPLastWriteSec < AUTOTDP_WRITE_SPACING_SEC)
            return;

        if (pendingTdpWrite is { IsCompleted: false })
            return;

        int bump = GetProcessor() is IntelProcessor { MicroArch: IntelMicroArch.LunarLake } ? 1 : 0;
        double pl2 = Math.Min(candidate + bump, TDPMax);

        AutoTDPLastWriteSec = now;
        AutoTDPPendingW = candidate;
        pendingTdpWrite = RequestTDPAsync(new[] { candidate, candidate, pl2 }, true, autotdpGeneration);
    }

    /// <summary>Folds the outcome of the previous write into <see cref="AutoTDPApplied"/>; a failed write is simply retried by the next actuation.</summary>
    private static void AutoTDPCollectWrite()
    {
        if (pendingTdpWrite is not { IsCompleted: true })
            return;

        bool ok = pendingTdpWrite.Status == TaskStatus.RanToCompletion && pendingTdpWrite.Result;
        pendingTdpWrite = null;

        if (!ok || AutoTDPPendingW <= 0)
            return;

        if (AutoTDPApplied != AutoTDPPendingW)
        {
            AutoTDPApplied = AutoTDPPendingW;
            AutoTDPHoldSec = 0;
            AutoTDPConvergeSec = 0;
            AutoTDPConvergedAtLevel = false;
        }
        AutoTDPPendingW = 0;
    }

    /// <summary>Keeps MSR 0x610 in step with the requested rails on Intel; skipped entirely on backends without MSR support.</summary>
    private static void AutoTDPMaintainMSR()
    {
        if (processor is not IntelProcessor intel || !intel.SupportsMSR)
            return;

        double slow = RequestedTDP[(int)PowerType.Slow];
        double fast = RequestedTDP[(int)PowerType.Fast];
        if (slow == 0.0d || fast == 0.0d)
            return;

        if (RequestedMSR[0] != slow || RequestedMSR[1] != fast)
            RequestMSR(slow, fast);
    }

    private static void AutoTDPUpdateState(double now, double dt, string reason)
    {
        bool deficit = AutoTDPInDeficit;
        double applied = AutoTDPApplied > 0 ? AutoTDPApplied : AutoTDP;

        // pinned at the ceiling and still short: nothing more the controller can do
        if (deficit && applied >= AutoTDPMax - 0.5)
            AutoTDPMaxLimitedSec += dt;
        else
            AutoTDPMaxLimitedSec = 0;

        // convergence: one applied level, no deficit, for AUTOTDP_CONVERGE_SEC
        if (deficit)
            AutoTDPConvergeSec = 0;
        else if (AutoTDPApplied > 0)
            AutoTDPConvergeSec += dt;

        if (!AutoTDPConvergedAtLevel && AutoTDPConvergeSec >= AUTOTDP_CONVERGE_SEC && AutoTDPApplied > 0)
        {
            AutoTDPConvergedAtLevel = true;
            AutoTDPOnConverged();
        }

        AutoTDPState state = AutoTDPMaxLimitedSec >= AUTOTDP_MAXLIMITED_SEC
            ? AutoTDPState.MaxLimited
            : AutoTDPWarm ? AutoTDPState.Tracking : AutoTDPState.Learning;

        AutoTDPPublish(state, now, false);
    }

    private static void AutoTDPOnConverged()
    {
        bool first = autoTDPBaseline is null;
        autoTDPBaseline ??= new AutoTDPBaseline();

        // Min is the lowest level known to hold (the floor, raised by failures, lowered by successful probes);
        // Max follows the heaviest level that cleared a deficit this session, blended so a one-off spike fades.
        double level = AutoTDPApplied;
        double sessionMax = Math.Max(AutoTDPRangeMaxW, level);
        autoTDPBaseline.RecentWatts = level;
        autoTDPBaseline.TypicalWatts = autoTDPBaseline.Samples == 0
            ? level
            : autoTDPBaseline.TypicalWatts + AUTOTDP_BASELINE_EWMA * (level - autoTDPBaseline.TypicalWatts);
        autoTDPBaseline.MinWatts = AutoTDPFloorW > 0 ? Math.Min(AutoTDPFloorW, level) : (AutoTDPRangeMinW > 0 ? Math.Min(AutoTDPRangeMinW, level) : level);
        autoTDPBaseline.MaxWatts = autoTDPBaseline.Samples == 0
            ? sessionMax
            : Math.Max(level, autoTDPBaseline.MaxWatts + AUTOTDP_BASELINE_EWMA * (sessionMax - autoTDPBaseline.MaxWatts));
        autoTDPBaseline.Samples++;
        autoTDPBaseline.LastUpdatedUtc = DateTime.UtcNow;
        autoTDPBaseline.TemperatureAtConvergence = PlatformManager.LibreHardware?.GetCPUTemperature() ?? autoTDPBaseline.TemperatureAtConvergence;
        AutoTDPBaselineDirty = true;

        bool promoted = !AutoTDPWarm;
        AutoTDPWarm = true;

        LogManager.LogInformation("AutoTDP converged at {0} W for {1} FPS (range {2}-{3} W){4}", level, AutoTDPTargetFPS, autoTDPBaseline.MinWatts, autoTDPBaseline.MaxWatts, promoted ? ", tracking" : string.Empty);
        AutoTDPSaveBaseline(first || promoted);
    }

    private static void AutoTDPPublish(AutoTDPState state, double now, bool force)
    {
        AutoTDPStatus previous = autoTDPStatus;
        double applied = AutoTDPApplied;
        double? baseline = autoTDPBaseline is not null ? autoTDPBaseline.RecentWatts : null;

        AutoTDPStatus status = new(state, AutoTDPCapped, AutoTDPTargetFPS, AutoTDPWinFps, AutoTDP, applied, baseline, AutoTDPEfficiencyTag(), effPhase != AutoTDPEfficiencyPhase.Idle);
        autoTDPStatus = status;

        if (force || previous.State != state || previous.AppliedW != applied || previous.Capped != AutoTDPCapped || previous.EfficiencyRung != status.EfficiencyRung || previous.Optimizing != status.Optimizing)
        {
            if (previous.State != state)
                LogManager.LogInformation("AutoTDP state: {0} -> {1} ({2} W applied)", previous.State, state, applied);

            AutoTDPStatusChanged?.Invoke(status);
        }
    }

    private static void AutoTDPResetTelemetry()
    {
        AutoTDPFrameHead = 0;
        AutoTDPFrameCount = 0;
        AutoTDPLastStatCount = 0;
        AutoTDPStaleSec = 0;
        AutoTDPWinFps = AutoTDPSlowFps = 0;
        AutoTDPP95Ratio = 0;
        AutoTDPLongFrames = AutoTDPSevereFrames = 0;
        AutoTDPGpuLoad = null;
    }

    private static void AutoTDPResetDwell()
    {
        AutoTDPHeadroomSec = AutoTDPDeficitSec = AutoTDPHoldSec = 0;
        AutoTDPUpSettleUntilSec = AutoTDPDownSettleUntilSec = 0;
        AutoTDPLastDownSec = 0;
        AutoTDPDownFromW = 0;
        AutoTDPDownWasProbe = false;
        AutoTDPInDeficit = false;
        AutoTDPConvergeSec = 0;
        AutoTDPConvergedAtLevel = false;
        AutoTDPMaxLimitedSec = 0;
        AutoTDPPendingW = 0;
    }

    private static void AutoTDPSeedFromBaseline()
    {
        double seed = AutoTDPMax;
        if (autoTDPBaseline is not null)
        {
            seed = autoTDPBaseline.GetSeedWatts();

            float? temperature = PlatformManager.LibreHardware?.GetCPUTemperature();
            if (temperature.HasValue && autoTDPBaseline.TemperatureAtConvergence > 0 && temperature.Value - autoTDPBaseline.TemperatureAtConvergence > 10.0f)
                seed += 1.0;
        }

        AutoTDP = Math.Clamp(seed, TDPMin, Math.Max(TDPMin, AutoTDPMax));
    }

    #endregion

    #region AutoTDP persistence

    private static string AutoTDPBuildSessionId(PowerProfile profile, string executable)
    {
        int powerLine = (int)System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus;
        string rung = AutoTDPEfficiencyTag();
        return string.Join("|", executable.ToLowerInvariant(), profile.Guid.ToString("N"), powerLine, rung.Length > 0 ? rung : "-", AutoTDPFingerprint(profile));
    }

    private static string AutoTDPBuildKey(string sessionId, float targetFps)
    {
        return string.Concat(sessionId, "|", Math.Round(targetFps).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Key with the efficiency-rung segment blanked; identifies "this game, this target" across rungs.</summary>
    private static string AutoTDPKeyWithoutRung(string key)
    {
        string[] parts = key.Split('|');
        if (parts.Length > 3)
            parts[3] = "-";
        return string.Join("|", parts);
    }

    /// <summary>Stable 32-bit FNV-1a over the power-profile fields that change the fps/W relationship.</summary>
    private static string AutoTDPFingerprint(PowerProfile profile)
    {
        string source = string.Join(";",
            ManagerFactory.settingsManager.GetInt("ConfigurableTDPMethod"),
            TDPMin.ToString(CultureInfo.InvariantCulture),
            TDPMax.ToString(CultureInfo.InvariantCulture),
            profile.CPUOverrideEnabled, profile.CPUOverrideValue.ToString(CultureInfo.InvariantCulture),
            profile.GPUOverrideEnabled, profile.GPUOverrideValue.ToString(CultureInfo.InvariantCulture),
            (int)profile.CPUBoostLevel,
            profile.OSPowerMode.ToString("N"),
            profile.IntelEnduranceGamingEnabled, profile.IntelEnduranceGamingPreset,
            profile.OEMPowerMode,
            profile.FramerateValue,
            (int)profile.CPUParkingMode,
            profile.CPUCoreEnabled, profile.CPUCoreCount);

        uint hash = 2166136261;
        foreach (char c in source)
        {
            hash ^= c;
            hash *= 16777619;
        }
        return hash.ToString("x8");
    }

    private static void AutoTDPLoadBaseline(string key)
    {
        AutoTDPBaselineKey = key;
        autoTDPBaseline = null;
        AutoTDPBaselineDirty = false;
        AutoTDPWarm = false;
        AutoTDPRangeMinW = AutoTDPRangeMaxW = AutoTDPFloorW = 0;
        AutoTDPProbeFailures = 0;
        AutoTDPProbeBackoffSec = 0;

        Profile? profile = AutoTDPSessionProfile;
        if (profile is null)
            return;

        lock (profile.SyncRoot)
        {
            if (profile.AutoTDPBaselines.TryGetValue(key, out AutoTDPBaseline? stored) && stored is not null && stored.Samples > 0)
                autoTDPBaseline = stored.Clone();
        }

        if (autoTDPBaseline is null)
            return;

        AutoTDPWarm = true;
        AutoTDPRangeMinW = autoTDPBaseline.MinWatts;
        AutoTDPRangeMaxW = autoTDPBaseline.MaxWatts;
        AutoTDPFloorW = autoTDPBaseline.MinWatts;
    }

    /// <summary>
    ///     Writes the working baseline into the session profile and serialises the profile without re-applying it.
    ///     The tick calls this with <paramref name="force"/> = false, which respects the re-save spacing; session
    ///     boundaries force it. Profile locks are only tried, never awaited, so the tick can never deadlock here.
    /// </summary>
    private static void AutoTDPSaveBaseline(bool force)
    {
        if (autoTDPBaseline is null || !AutoTDPBaselineDirty)
            return;

        double now = AutoTDPNowSec;
        if (!force && now - AutoTDPBaselineSavedSec < AUTOTDP_BASELINE_RESAVE_SEC)
            return;

        Profile? profile = AutoTDPSessionProfile;
        if (profile is null || string.IsNullOrEmpty(AutoTDPBaselineKey))
            return;

        if (!Monitor.TryEnter(profile.SyncRoot, 250))
            return;

        try
        {
            profile.AutoTDPBaselines[AutoTDPBaselineKey] = autoTDPBaseline.Clone();

            while (profile.AutoTDPBaselines.Count > AUTOTDP_BASELINE_CAP)
            {
                string oldest = profile.AutoTDPBaselines.OrderBy(kv => kv.Value.LastUpdatedUtc).First().Key;
                profile.AutoTDPBaselines.Remove(oldest);
            }
        }
        finally
        {
            Monitor.Exit(profile.SyncRoot);
        }

        // serialisation clones the profile under its locks and touches the disk; keep it off the controller thread
        Task.Run(() => ManagerFactory.profileManager.SerializeProfile(profile));
        AutoTDPBaselineDirty = false;
        AutoTDPBaselineSavedSec = now;
    }

    #endregion

    #region AutoTDP trace

    private static void AutoTDPTrace(double now, string reason)
    {
        if (!autoTDPTraceEnabled)
            return;

        lock (autoTDPTraceLock)
        {
            try
            {
                if (autoTDPTrace is null)
                {
                    Directory.CreateDirectory(App.LogsPath);
                    string path = Path.Combine(App.LogsPath, $"autotdp-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
                    autoTDPTrace = new StreamWriter(path, false) { AutoFlush = true };
                    autoTDPTrace.WriteLine("t,fps,slowFps,p95Ratio,longFrames,severeFrames,gpuLoad,capped,setpoint,applied,state,reason,rangeMin,rangeMax,floor,key");
                }

                autoTDPTrace.WriteLine(string.Join(",",
                    now.ToString("F2", CultureInfo.InvariantCulture),
                    AutoTDPWinFps.ToString("F2", CultureInfo.InvariantCulture),
                    AutoTDPSlowFps.ToString("F2", CultureInfo.InvariantCulture),
                    AutoTDPP95Ratio.ToString("F3", CultureInfo.InvariantCulture),
                    AutoTDPLongFrames,
                    AutoTDPSevereFrames,
                    AutoTDPGpuLoad?.ToString("F0", CultureInfo.InvariantCulture) ?? string.Empty,
                    AutoTDPCapped ? 1 : 0,
                    AutoTDP.ToString("F2", CultureInfo.InvariantCulture),
                    AutoTDPApplied.ToString("F0", CultureInfo.InvariantCulture),
                    autoTDPStatus.State,
                    reason,
                    AutoTDPRangeMinW.ToString("F0", CultureInfo.InvariantCulture),
                    AutoTDPRangeMaxW.ToString("F0", CultureInfo.InvariantCulture),
                    AutoTDPFloorW.ToString("F0", CultureInfo.InvariantCulture),
                    AutoTDPBaselineKey));
            }
            catch (Exception ex)
            {
                LogManager.LogWarning("AutoTDP trace failed: {0}", ex.Message);
                autoTDPTraceEnabled = false;
            }
        }
    }

    private static void AutoTDPCloseTrace()
    {
        lock (autoTDPTraceLock)
        {
            autoTDPTrace?.Dispose();
            autoTDPTrace = null;
        }
    }

    #endregion

    #region AutoTDP efficiency ladder

    /*
     * Efficiency ladder
     * -----------------
     * While AutoTDP is tracking and the game is quiet, this optimiser trials one efficiency step at a time -
     * energy/performance preference (EPP 60/75/90) and E-core scheduling (prefer / only) - and keeps a step only
     * when measured draw drops by a significant margin and the frame target keeps holding. Draw is battery
     * discharge on DC and CPU package power on AC (never summed with GPU power).
     *
     * A trial is A (30 s at the current state) -> B (30 s at the candidate, after the TDP loop settled) ->
     * A' (30 s back at the current state); the candidate is committed when mean(A, A') - mean(B) exceeds
     * max(0.5 W, 2 standard errors). Any deficit during B rejects the candidate; a candidate that cannot settle
     * within the timeout is rejected too. Rejected steps and every step above them of the same kind are skipped
     * for the session. Each rung learns its own TDP baseline (the rung is part of the baseline key).
     *
     * The chosen state lives in runtime overrides that the CPU watchdog and the profile apply path honour via
     * EffectiveCoreMode; EPP is restored to the value captured before the first override when the session ends.
     */

    private enum AutoTDPEfficiencyRung { EPP60, PrefECore, EPP75, EPP90, OnlyECore }
    private enum AutoTDPEfficiencyPhase { Idle, MeasureA, SettleB, MeasureB, SettleA, MeasureA2 }

    private static readonly AutoTDPEfficiencyRung[] AutoTDPEfficiencyLadder =
    [
        AutoTDPEfficiencyRung.EPP60,
        AutoTDPEfficiencyRung.PrefECore,
        AutoTDPEfficiencyRung.EPP75,
        AutoTDPEfficiencyRung.EPP90,
        AutoTDPEfficiencyRung.OnlyECore,
    ];

    private const double AUTOTDP_EFF_MEASURE_SEC = 30.0;         // length of each measurement window
    private const double AUTOTDP_EFF_QUIET_SEC = 20.0;           // TDP loop quiet (no deficit, no writes) before measuring
    private const double AUTOTDP_EFF_SETTLE_TIMEOUT_SEC = 120.0; // candidate must settle within this or it is rejected
    private const double AUTOTDP_EFF_COOLDOWN_SEC = 60.0;        // pause between trials
    private const double AUTOTDP_EFF_LOCKOUT_SEC = 600.0;        // rejected candidate is not retried before this
    private const double AUTOTDP_EFF_MIN_GAIN_W = 0.5;           // minimum accepted draw improvement
    private const int AUTOTDP_EFF_MIN_SAMPLES = 4;               // distinct sensor samples required per window

    private static AutoTDPEfficiencyPhase effPhase = AutoTDPEfficiencyPhase.Idle;
    private static int effCandidate = -1;
    private static readonly bool[] effTried = new bool[AutoTDPEfficiencyLadder.Length];
    private static readonly double[] effLockoutUntilSec = new double[AutoTDPEfficiencyLadder.Length];
    private static double effPhaseStartSec, effCooldownUntilSec;
    private static double effSum, effSumSq;
    private static int effCount;
    private static float effLastSample = float.NaN;
    private static double effMeanA, effVarA, effMeanB, effVarB;
    private static int effCountA, effCountB;
    private static bool effOnBattery;

    private static uint? effAcceptedEpp;
    private static CoreParkingMode? effAcceptedCore;
    private static uint? runtimeEppOverride;
    private static CoreParkingMode? runtimeCoreModeOverride;
    private static uint[]? eppBeforeOverride;

    /// <summary>Core scheduling mode to enforce: the efficiency ladder's runtime choice when one is active, else the profile's.</summary>
    private static CoreParkingMode EffectiveCoreMode(PowerProfile profile) => runtimeCoreModeOverride ?? profile.CPUParkingMode;

    private static bool AutoTDPEfficiencyActive => runtimeEppOverride.HasValue || runtimeCoreModeOverride.HasValue;

    /// <summary>Short label of the active efficiency state for the key, status and OSD ("EPP60+PrefE"); empty when none.</summary>
    private static string AutoTDPEfficiencyTag()
    {
        string tag = string.Empty;
        if (runtimeEppOverride.HasValue)
            tag = $"EPP{runtimeEppOverride.Value}";
        if (runtimeCoreModeOverride.HasValue)
            tag += (tag.Length > 0 ? "+" : string.Empty) + (runtimeCoreModeOverride.Value == CoreParkingMode.OnlyECore ? "OnlyE" : "PrefE");
        return tag;
    }

    private static void AutoTDPEfficiencyStep(double now, double dt, bool deficit)
    {
        bool enabled = currentProfile?.AutoTDPEfficiencyEnabled ?? false;
        if (!enabled)
        {
            // switched off mid-session: back to the profile's own settings
            if (AutoTDPEfficiencyActive || effPhase != AutoTDPEfficiencyPhase.Idle)
            {
                AutoTDPEfficiencyClearOverrides();
                if (currentProfile is not null)
                    RequestCoreParkingMode(EffectiveCoreMode(currentProfile));
                AutoTDPRekeyForEfficiency();
                LogManager.LogInformation("AutoTDP efficiency: disabled, overrides cleared");
            }
            return;
        }

        float? sample = AutoTDPEfficiencySample();
        if (sample is null)
        {
            if (effPhase != AutoTDPEfficiencyPhase.Idle)
                AutoTDPEfficiencyFinishTrial(now, commit: false, lockout: false, "no power sensor");
            return;
        }

        bool quiet = AutoTDPConvergeSec >= AUTOTDP_EFF_QUIET_SEC;

        switch (effPhase)
        {
            case AutoTDPEfficiencyPhase.Idle:
                if (now < effCooldownUntilSec || !AutoTDPWarm || !quiet)
                    return;

                int candidate = AutoTDPEfficiencyNextCandidate(now);
                if (candidate < 0)
                    return;

                effCandidate = candidate;
                AutoTDPEfficiencyBeginWindow(now, AutoTDPEfficiencyPhase.MeasureA);
                LogManager.LogInformation("AutoTDP efficiency: trialing {0} ({1})", AutoTDPEfficiencyLadder[candidate], effOnBattery ? "battery draw" : "CPU package power");
                break;

            case AutoTDPEfficiencyPhase.MeasureA:
                if (deficit)
                {
                    AutoTDPEfficiencyFinishTrial(now, commit: false, lockout: false, "deficit before trial");
                    return;
                }

                AutoTDPEfficiencyAccumulate(sample.Value);
                if (now - effPhaseStartSec >= AUTOTDP_EFF_MEASURE_SEC)
                {
                    if (effCount < AUTOTDP_EFF_MIN_SAMPLES)
                    {
                        AutoTDPEfficiencyFinishTrial(now, commit: false, lockout: false, "too few samples");
                        return;
                    }

                    (effMeanA, effVarA, effCountA) = AutoTDPEfficiencyWindowStats();
                    AutoTDPEfficiencyApplyCandidate(effCandidate, now);
                    effPhase = AutoTDPEfficiencyPhase.SettleB;
                    effPhaseStartSec = now;
                }
                break;

            case AutoTDPEfficiencyPhase.SettleB:
                if (quiet)
                    AutoTDPEfficiencyBeginWindow(now, AutoTDPEfficiencyPhase.MeasureB);
                else if (now - effPhaseStartSec > AUTOTDP_EFF_SETTLE_TIMEOUT_SEC)
                    AutoTDPEfficiencyFinishTrial(now, commit: false, lockout: true, "candidate did not settle");
                break;

            case AutoTDPEfficiencyPhase.MeasureB:
                if (deficit)
                {
                    AutoTDPEfficiencyFinishTrial(now, commit: false, lockout: true, "deficit under candidate");
                    return;
                }

                AutoTDPEfficiencyAccumulate(sample.Value);
                if (now - effPhaseStartSec >= AUTOTDP_EFF_MEASURE_SEC)
                {
                    if (effCount < AUTOTDP_EFF_MIN_SAMPLES)
                    {
                        AutoTDPEfficiencyFinishTrial(now, commit: false, lockout: false, "too few samples");
                        return;
                    }

                    (effMeanB, effVarB, effCountB) = AutoTDPEfficiencyWindowStats();
                    AutoTDPEfficiencyApplyAccepted(now);
                    effPhase = AutoTDPEfficiencyPhase.SettleA;
                    effPhaseStartSec = now;
                }
                break;

            case AutoTDPEfficiencyPhase.SettleA:
                if (quiet)
                    AutoTDPEfficiencyBeginWindow(now, AutoTDPEfficiencyPhase.MeasureA2);
                else if (now - effPhaseStartSec > AUTOTDP_EFF_SETTLE_TIMEOUT_SEC)
                    AutoTDPEfficiencyFinishTrial(now, commit: false, lockout: false, "did not re-settle");
                break;

            case AutoTDPEfficiencyPhase.MeasureA2:
                if (deficit)
                {
                    AutoTDPEfficiencyFinishTrial(now, commit: false, lockout: false, "deficit after trial");
                    return;
                }

                AutoTDPEfficiencyAccumulate(sample.Value);
                if (now - effPhaseStartSec >= AUTOTDP_EFF_MEASURE_SEC)
                {
                    (double meanA2, double varA2, int countA2) = AutoTDPEfficiencyWindowStats();
                    if (countA2 >= AUTOTDP_EFF_MIN_SAMPLES)
                    {
                        // pool both A windows
                        int nA = effCountA + countA2;
                        double meanA = (effMeanA * effCountA + meanA2 * countA2) / nA;
                        double varA = (effVarA * effCountA + varA2 * countA2) / nA;

                        double gain = meanA - effMeanB;
                        double se = Math.Sqrt(varA / Math.Max(1, nA) + effVarB / Math.Max(1, effCountB));
                        double threshold = Math.Max(AUTOTDP_EFF_MIN_GAIN_W, 2.0 * se);

                        bool commit = gain > threshold;
                        AutoTDPEfficiencyFinishTrial(now, commit, lockout: !commit, $"gain {gain:F2} W vs threshold {threshold:F2} W");
                    }
                    else
                        AutoTDPEfficiencyFinishTrial(now, commit: false, lockout: false, "too few samples");
                }
                break;
        }
    }

    /// <summary>System draw to minimise: battery discharge (W) on DC, CPU package power on AC. <c>null</c> when no sensor is available.</summary>
    private static float? AutoTDPEfficiencySample()
    {
        LibreHardwarePlatform? lhm = PlatformManager.LibreHardware;
        if (lhm is null)
            return null;

        effOnBattery = System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Offline;
        float? value = effOnBattery ? lhm.GetBatteryPower() : lhm.GetCPUPower();
        if (!value.HasValue)
            return null;

        // battery power is negative while discharging; report draw as a positive number
        return effOnBattery ? -value.Value : value.Value;
    }

    private static void AutoTDPEfficiencyBeginWindow(double now, AutoTDPEfficiencyPhase phase)
    {
        effPhase = phase;
        effPhaseStartSec = now;
        effSum = effSumSq = 0;
        effCount = 0;
        effLastSample = float.NaN;
    }

    /// <summary>The sensors update far slower than the tick; only distinct readings are counted so variance is not understated.</summary>
    private static void AutoTDPEfficiencyAccumulate(float sample)
    {
        if (!float.IsNaN(effLastSample) && sample == effLastSample)
            return;

        effLastSample = sample;
        effSum += sample;
        effSumSq += (double)sample * sample;
        effCount++;
    }

    private static (double mean, double variance, int count) AutoTDPEfficiencyWindowStats()
    {
        if (effCount == 0)
            return (0, 0, 0);

        double mean = effSum / effCount;
        double variance = Math.Max(0, effSumSq / effCount - mean * mean);
        return (mean, variance, effCount);
    }

    private static int AutoTDPEfficiencyNextCandidate(double now)
    {
        CoreParkingMode configured = currentProfile?.CPUParkingMode ?? CoreParkingMode.AllCoresAuto;

        for (int i = 0; i < AutoTDPEfficiencyLadder.Length; i++)
        {
            if (effTried[i] || now < effLockoutUntilSec[i])
                continue;

            switch (AutoTDPEfficiencyLadder[i])
            {
                case AutoTDPEfficiencyRung.EPP60:
                case AutoTDPEfficiencyRung.EPP75:
                case AutoTDPEfficiencyRung.EPP90:
                    if (AutoTDPEfficiencyRungEpp(AutoTDPEfficiencyLadder[i]) <= (effAcceptedEpp ?? 0))
                        continue;
                    return i;

                case AutoTDPEfficiencyRung.PrefECore:
                case AutoTDPEfficiencyRung.OnlyECore:
                    CoreParkingMode target = AutoTDPEfficiencyLadder[i] == AutoTDPEfficiencyRung.OnlyECore ? CoreParkingMode.OnlyECore : CoreParkingMode.AllCoresPrefECore;
                    if (AutoTDPCoreEfficiencyRank(target) <= AutoTDPCoreEfficiencyRank(effAcceptedCore ?? configured))
                        continue;
                    return i;
            }
        }

        return -1;
    }

    /// <summary>Orders core scheduling modes from performance-preferring to efficiency-preferring.</summary>
    private static int AutoTDPCoreEfficiencyRank(CoreParkingMode mode)
    {
        return mode switch
        {
            CoreParkingMode.OnlyPCore => 0,
            CoreParkingMode.AllCoresPrefPCore => 1,
            CoreParkingMode.AllCoresAuto => 2,
            CoreParkingMode.AllCoresPrefECore => 3,
            CoreParkingMode.OnlyECore => 4,
            _ => 2,
        };
    }

    private static uint AutoTDPEfficiencyRungEpp(AutoTDPEfficiencyRung rung)
    {
        return rung switch
        {
            AutoTDPEfficiencyRung.EPP60 => 60,
            AutoTDPEfficiencyRung.EPP75 => 75,
            AutoTDPEfficiencyRung.EPP90 => 90,
            _ => 0,
        };
    }

    private static (uint? epp, CoreParkingMode? core) AutoTDPEfficiencyCandidateState(int index)
    {
        AutoTDPEfficiencyRung rung = AutoTDPEfficiencyLadder[index];
        return rung switch
        {
            AutoTDPEfficiencyRung.PrefECore => (effAcceptedEpp, CoreParkingMode.AllCoresPrefECore),
            AutoTDPEfficiencyRung.OnlyECore => (effAcceptedEpp, CoreParkingMode.OnlyECore),
            _ => (AutoTDPEfficiencyRungEpp(rung), effAcceptedCore),
        };
    }

    private static void AutoTDPEfficiencyApplyCandidate(int index, double now)
    {
        (uint? epp, CoreParkingMode? core) = AutoTDPEfficiencyCandidateState(index);
        AutoTDPEfficiencySetState(epp, core);
        AutoTDPRekeyForEfficiency();
    }

    private static void AutoTDPEfficiencyApplyAccepted(double now)
    {
        AutoTDPEfficiencySetState(effAcceptedEpp, effAcceptedCore);
        AutoTDPRekeyForEfficiency();
    }

    /// <summary>Applies an EPP/core-mode pair as the runtime override, capturing the pre-override EPP the first time.</summary>
    private static void AutoTDPEfficiencySetState(uint? epp, CoreParkingMode? core)
    {
        if (epp.HasValue)
        {
            eppBeforeOverride ??= PowerScheme.ReadPowerCfg(PowerSubGroup.SUB_PROCESSOR, PowerSetting.PERFEPP);
            RequestEPP(epp.Value, epp.Value);
        }
        else if (eppBeforeOverride is not null && runtimeEppOverride.HasValue)
        {
            RequestEPP(eppBeforeOverride[0], eppBeforeOverride[1]);
        }
        runtimeEppOverride = epp;

        runtimeCoreModeOverride = core;
        if (currentProfile is not null)
            RequestCoreParkingMode(EffectiveCoreMode(currentProfile));
    }

    /// <summary>Drops every runtime override (EPP restored to the captured value); the caller re-applies the profile's core mode.</summary>
    private static void AutoTDPEfficiencyClearOverrides()
    {
        if (eppBeforeOverride is not null && runtimeEppOverride.HasValue)
            RequestEPP(eppBeforeOverride[0], eppBeforeOverride[1]);

        runtimeEppOverride = null;
        runtimeCoreModeOverride = null;
        eppBeforeOverride = null;
        effAcceptedEpp = null;
        effAcceptedCore = null;
        effPhase = AutoTDPEfficiencyPhase.Idle;
        effCandidate = -1;
        effCooldownUntilSec = 0;
        Array.Clear(effTried);
        Array.Clear(effLockoutUntilSec);
    }

    private static void AutoTDPEfficiencyFinishTrial(double now, bool commit, bool lockout, string reason)
    {
        int candidate = effCandidate;
        AutoTDPEfficiencyRung rung = candidate >= 0 ? AutoTDPEfficiencyLadder[candidate] : AutoTDPEfficiencyRung.EPP60;

        if (candidate >= 0)
        {
            effTried[candidate] = true;
            if (lockout)
            {
                effLockoutUntilSec[candidate] = now + AUTOTDP_EFF_LOCKOUT_SEC;

                // a rejected step also rules out the more aggressive steps of the same kind for this session
                bool isEpp = AutoTDPEfficiencyRungEpp(rung) > 0;
                for (int i = candidate + 1; i < AutoTDPEfficiencyLadder.Length; i++)
                    if ((AutoTDPEfficiencyRungEpp(AutoTDPEfficiencyLadder[i]) > 0) == isEpp)
                        effTried[i] = true;
            }

            if (commit)
            {
                (effAcceptedEpp, effAcceptedCore) = AutoTDPEfficiencyCandidateState(candidate);
                LogManager.LogInformation("AutoTDP efficiency: committed {0} ({1})", rung, reason);
            }
            else
                LogManager.LogInformation("AutoTDP efficiency: rejected {0} ({1})", rung, reason);
        }

        // whichever phase we were in, the accepted state is what must be running now
        if (AutoTDPEfficiencyActive || commit)
        {
            AutoTDPEfficiencySetState(effAcceptedEpp, effAcceptedCore);
            AutoTDPRekeyForEfficiency();
        }

        effPhase = AutoTDPEfficiencyPhase.Idle;
        effCandidate = -1;
        effCooldownUntilSec = now + AUTOTDP_EFF_COOLDOWN_SEC;
    }

    /// <summary>
    ///     The efficiency state is part of the baseline identity: switch the key softly (setpoint kept) so each rung
    ///     learns its own TDP baseline. When the new rung has no baseline yet, the previous rung's floor and range
    ///     stay as the prior - the plant is similar and this keeps the TDP loop from re-descending from scratch.
    /// </summary>
    private static void AutoTDPRekeyForEfficiency()
    {
        if (currentProfile is null || string.IsNullOrEmpty(AutoTDPSessionId))
            return;

        string sessionId = AutoTDPBuildSessionId(currentProfile, AutoTDPSessionExecutable);
        string key = AutoTDPBuildKey(sessionId, (float)AutoTDPTargetFPS);
        if (string.Equals(key, AutoTDPBaselineKey, StringComparison.Ordinal))
            return;

        double rangeMin = AutoTDPRangeMinW, rangeMax = AutoTDPRangeMaxW, floor = AutoTDPFloorW;

        AutoTDPSessionId = sessionId;
        AutoTDPRetarget(key, (float)AutoTDPTargetFPS);

        if (autoTDPBaseline is null)
        {
            AutoTDPRangeMinW = rangeMin;
            AutoTDPRangeMaxW = rangeMax;
            AutoTDPFloorW = floor;
        }
    }

    #endregion

    private static void cpuWatchdog_Elapsed(object? sender, ElapsedEventArgs e)
    {
        if (!_performanceManagerEnabled)
            return;

        if (cpuLock.TryEnter())
        {
            try
            {
                if (currentProfile is not null)
                {
                    // Check if CPU clock speed has changed and apply if needed
                    if (currentProfile.CPUOverrideEnabled)
                        RequestCPUClock(Convert.ToUInt32(currentProfile.CPUOverrideValue));

                    // Check if CPU core count has changed and apply if needed
                    if (currentProfile.CPUCoreEnabled)
                        RequestCPUCoreCount(currentProfile.CPUCoreCount);

                    // Check if CPU core parking mode has changed and apply if needed
                    RequestCoreParkingMode(EffectiveCoreMode(currentProfile));

                    // Check if active power shceme has changed and apply if needed
                    RequestPowerMode(currentProfile.OSPowerMode);

                    // Check if PerfBoostMode value has changed and apply if needed
                    RequestPerfBoostMode((uint)currentProfile.CPUBoostLevel);
                }
            }
            catch { }
            finally
            {
                // release lock
                cpuLock.Exit();
            }
        }
    }

    private static void tdpWatchdog_Elapsed(object? sender, ElapsedEventArgs e)
    {
        if (!_performanceManagerEnabled)
            return;

        if (processor is null || !processor.IsInitialized)
            return;

        if (tdpLock.TryEnter())
        {
            try
            {
                bool TDPdone = false;
                bool MSRdone = true;

                // read current values and (re)apply requested TDP if needed
                for (int idx = (int)PowerType.Slow; idx <= (int)PowerType.Fast; idx++)
                {
                    double TDP = RequestedTDP[idx];
                    if (TDP == 0.0d)
                        continue;

                    // AMD reduces TDP by 10% when OS power mode is set to Best power efficiency
                    if (processor is AMDProcessor && currentPowerMode == OSPowerMode.BetterBattery)
                        TDP = (int)Math.Truncate(TDP * 0.9);

                    // todo: find a way to read TDP limits
                    double ReadTDP = CurrentTDP[idx];
                    if (ReadTDP != 0)
                        tdpWatchdog.Interval = INTERVAL_DEFAULT;
                    else
                        tdpWatchdog.Interval = INTERVAL_DEGRADED;

                    // only request an update if current limit is different than stored
                    if (ReadTDP != TDP)
                        RequestTDP((PowerType)idx, TDP, true);

                    Thread.Sleep(200);
                }

                // are we done ?
                TDPdone = CurrentTDP[0] == RequestedTDP[0] && CurrentTDP[1] == RequestedTDP[1] && CurrentTDP[2] == RequestedTDP[2];

                // processor specific
                AutoTDPMaintainMSR();

                // user requested to halt TDP watchdog
                if (tdpWatchdogPendingStop)
                {
                    if (tdpWatchdog.Interval == INTERVAL_DEFAULT)
                    {
                        if (TDPdone && MSRdone)
                            tdpWatchdog.Stop();
                    }
                    else if (tdpWatchdog.Interval == INTERVAL_DEGRADED)
                    {
                        tdpWatchdog.Stop();
                    }
                }
            }
            catch { }
            finally
            {
                // release lock
                tdpLock.Exit();
            }
        }
    }

    private static void gfxWatchdog_Elapsed(object? sender, ElapsedEventArgs e)
    {
        if (!_performanceManagerEnabled)
            return;

        if (processor is null || !processor.IsInitialized)
            return;

        GPU? GPU = GPUManager.GetCurrent();
        if (GPU is null || !GPU.IsInitialized)
            return;

        if (gfxLock.TryEnter())
        {
            try
            {
                gfxWatchdogCounter++;

                bool GPUdone = true;
                bool forcedUpdate = false;

                // not ready yet
                if (StoredGfxClock == 0)
                    return;

                GPU? currentGpu = GPUManager.GetCurrent();
                if (currentGpu is null)
                    return;

                float CurrentGfxClock = currentGpu.GetClock();

                if (CurrentGfxClock != 0)
                    gfxWatchdog.Interval = INTERVAL_DEFAULT;
                else
                    gfxWatchdog.Interval = INTERVAL_DEGRADED;

                if (gfxWatchdogCounter > COUNTER_DEFAULT)
                {
                    forcedUpdate = true;
                    gfxWatchdogCounter = 0;
                }

                // only request an update if current gfx clock is different than stored
                // or a forced update is requested
                if (CurrentGfxClock != StoredGfxClock || forcedUpdate)
                {
                    // disabling
                    if (StoredGfxClock != 12750)
                    {
                        GPUdone = false;
                        RequestGPUClock(StoredGfxClock, true);
                    }
                }

                // user requested to halt gpu watchdog
                if (gfxWatchdogPendingStop)
                {
                    if (gfxWatchdog.Interval == INTERVAL_DEFAULT)
                    {
                        if (GPUdone)
                            gfxWatchdog.Stop();
                    }
                    else if (gfxWatchdog.Interval == INTERVAL_DEGRADED)
                    {
                        gfxWatchdog.Stop();
                    }
                }
            }
            catch { }
            finally
            {
                // release lock
                gfxLock.Exit();
            }
        }
    }

    private static void StartGPUWatchdog()
    {
        gfxWatchdogPendingStop = false;
        gfxWatchdog.Interval = INTERVAL_DEFAULT;
        gfxWatchdog.Start();
    }

    private static void StopGPUWatchdog(bool immediate = false)
    {
        gfxWatchdogPendingStop = true;
        if (immediate)
            gfxWatchdog.Stop();
    }

    private static void StartTDPWatchdog()
    {
        tdpWatchdogPendingStop = false;
        tdpWatchdog.Interval = INTERVAL_DEFAULT;
        tdpWatchdog.Start();
    }

    private static void StopTDPWatchdog(bool immediate = false)
    {
        tdpWatchdogPendingStop = true;
        if (immediate)
            tdpWatchdog.Stop();
    }

    private static void StartAutoTDPWatchdog()
    {
        AutoTDPLastTickSec = 0;
        autotdpWatchdog.Interval = INTERVAL_AUTO;
        autotdpWatchdog.Start();
    }

    private static void StopAutoTDPWatchdog()
    {
        autotdpWatchdog.Stop();
    }

    /// <summary>Whether a rail is actually written on this processor (Intel has no STAPM rail).</summary>
    private static bool IsRailWritten(PowerType type)
    {
        return !(processor is IntelProcessor && type == PowerType.Stapm);
    }

    /// <summary>
    ///     Records the requested value for one rail and, when <paramref name="immediate"/>, writes it.
    ///     Returns <c>true</c> when the value was accepted (stored, or written and acknowledged by the backend).
    /// </summary>
    private static bool RequestTDP(PowerType type, double value, bool immediate = false)
    {
        // make sure we're not trying to run below or above specs
        value = Math.Min(TDPMax, Math.Max(TDPMin, value));

        // skip if value is invalid
        if (value == 0 || double.IsNaN(value) || double.IsInfinity(value))
            return false;

        // update value read by timer
        int idx = (int)type;
        RequestedTDP[idx] = value;

        // skip if processor is not ready
        if (processor is null || !processor.IsInitialized)
            return false;

        if (!immediate)
            return true;

        // TODO: Implement proper TDP reading
        // CurrentTDP[idx] = value;

        if (!IsRailWritten(type))
            return true;

        return processor.SetTDPLimit(type, value, immediate);
    }

    /// <summary>
    ///     Single serialised writer for a full rail set (Slow, Stapm, Fast). Rails are written in order with a short
    ///     pause only between rails that are actually written; concurrent callers queue behind <see cref="tdpWriteLock"/>
    ///     so rail sequences never interleave. A <paramref name="generation"/> ties the write to an AutoTDP session:
    ///     if the session changes mid-sequence the remaining rails are abandoned. <c>null</c> writes unconditionally.
    /// </summary>
    /// <returns><c>true</c> when every written rail was acknowledged by the backend.</returns>
    private static async Task<bool> RequestTDPAsync(double[] values, bool immediate, int? generation)
    {
        // Handle null or insufficient array scenario
        if (values == null || values.Length <= (int)PowerType.Fast)
            return false;

        await tdpWriteLock.WaitAsync().ConfigureAwait(false);
        try
        {
            bool success = true;
            bool wroteAny = false;

            for (int idx = (int)PowerType.Slow; idx <= (int)PowerType.Fast; idx++)
            {
                if (generation.HasValue && generation.Value != autotdpGeneration)
                    return false;

                PowerType type = (PowerType)idx;
                if (!IsRailWritten(type))
                {
                    RequestTDP(type, values[idx], false);
                    continue;
                }

                if (wroteAny)
                    await Task.Delay(200).ConfigureAwait(false);

                success &= RequestTDP(type, values[idx], immediate);
                wroteAny = true;
            }

            return success;
        }
        catch (Exception ex)
        {
            LogManager.LogWarning("TDP write failed: {0}", ex.Message);
            return false;
        }
        finally
        {
            tdpWriteLock.Release();
        }
    }

    /// <summary>Writes PL1/PL2 to MSR 0x610 on Intel and remembers the last acknowledged pair in <see cref="RequestedMSR"/>.</summary>
    private static bool RequestMSR(double PL1, double PL2)
    {
        if (processor is null || !processor.IsInitialized)
            return false;

        if (processor is not IntelProcessor intel)
            return false;

        // make sure we're not trying to run below or above specs
        double TDPslow = Math.Min(TDPMax, Math.Max(TDPMin, PL1));
        double TDPfast = Math.Min(TDPMax, Math.Max(TDPMin, PL2));

        bool success = intel.SetMSRLimit(TDPslow, TDPfast);
        if (success)
        {
            RequestedMSR[0] = TDPslow;
            RequestedMSR[1] = TDPfast;
        }

        return success;
    }

    private static void RequestGPUClock(double value, bool immediate = false)
    {
        // update value read by timer
        StoredGfxClock = value;

        if (processor is null || !processor.IsInitialized)
            return;

        // immediately apply
        if (immediate)
            processor.SetGPUClock(StoredGfxClock);
    }

    private static void RequestPowerMode(Guid guid)
    {
        if (PowerGetEffectiveOverlayScheme(out Guid activeScheme) == 0)
        {
            if (activeScheme == guid)
            {
                // Scheme is already correct; sync currentPowerMode if it drifted (e.g. on first call at startup)
                if (currentPowerMode != guid)
                {
                    currentPowerMode = guid;
                    int idx = Array.IndexOf(PowerModes, guid);
                    if (idx != -1)
                        PowerModeChanged?.Invoke(idx);
                }
                return;
            }
        }

        LogManager.LogDebug("User requested power scheme: {0}", guid);

        if (PowerSetActiveOverlayScheme(guid) != 0)
            LogManager.LogWarning("Failed to set requested power scheme: {0}", guid);
        else
        {
            currentPowerMode = guid;

            int idx = Array.IndexOf(PowerModes, currentPowerMode);
            if (idx != -1)
                PowerModeChanged?.Invoke(idx);
        }
    }

    private static void RequestCoreParkingMode(CoreParkingMode coreParkingMode)
    {
        /*
         * HETEROGENEOUS_POLICY values:
         * 0: Default (no explicit preference)
         * 1: Prefer heterogeneous scheduling (allows mixed cores based on scheduling hints)
         * 2: Prefer E-cores exclusively (favor efficiency and battery life)
         * 3: Prefer P-cores exclusively (favor performance at all costs)

         * HETEROGENEOUS_THREAD_SCHEDULING_POLICY and HETEROGENEOUS_SHORT_THREAD_SCHEDULING_POLICY values: These settings instruct Windows Scheduler about how aggressively it should favor either core type for regular or short-lived threads:
         * 1: Strongly Prefer P-Cores (high-performance cores only)
         * 2: Prefer P-Cores (favor P-Cores but allow E-Cores occasionally)
         * 3: Strongly Prefer E-Cores (efficiency cores only)
         * 4: Prefer E-Cores (favor E-Cores but allow P-Cores occasionally)
         * 5: No specific preference (Windows decides automatically)
         */

        uint policyAC, policyDC, threadAC, threadDC, shortAC, shortDC;
        switch (coreParkingMode)
        {
            case CoreParkingMode.AllCoresPrefPCore:
                policyAC = policyDC = 1U; threadAC = threadDC = 2U; shortAC = shortDC = 2U;
                break;
            case CoreParkingMode.AllCoresPrefECore:
                policyAC = policyDC = 1U; threadAC = threadDC = 4U; shortAC = shortDC = 4U;
                break;
            case CoreParkingMode.OnlyPCore:
                policyAC = policyDC = 3U; threadAC = threadDC = 1U; shortAC = shortDC = 1U;
                break;
            case CoreParkingMode.OnlyECore:
                policyAC = policyDC = 2U; threadAC = threadDC = 3U; shortAC = shortDC = 3U;
                break;
            default:
                policyAC = policyDC = 0U; threadAC = threadDC = 5U; shortAC = shortDC = 5U;
                break;
        }

        // Are the values already correct?
        uint[] curPolicy = PowerScheme.ReadPowerCfg(PowerSubGroup.SUB_PROCESSOR, PowerSetting.HETEROGENEOUS_POLICY);
        uint[] curThread = PowerScheme.ReadPowerCfg(PowerSubGroup.SUB_PROCESSOR, PowerSetting.HETEROGENEOUS_THREAD_SCHEDULING_POLICY);
        uint[] curShort = PowerScheme.ReadPowerCfg(PowerSubGroup.SUB_PROCESSOR, PowerSetting.HETEROGENEOUS_SHORT_THREAD_SCHEDULING_POLICY);

        if (curPolicy[0] == policyAC && curPolicy[1] == policyDC &&
            curThread[0] == threadAC && curThread[1] == threadDC &&
            curShort[0] == shortAC && curShort[1] == shortDC)
            return;

        // one scheme activation for the three related policies
        PowerScheme.WritePowerCfg(PowerSubGroup.SUB_PROCESSOR,
        [
            (PowerSetting.HETEROGENEOUS_POLICY, policyAC, policyDC),
            (PowerSetting.HETEROGENEOUS_THREAD_SCHEDULING_POLICY, threadAC, threadDC),
            (PowerSetting.HETEROGENEOUS_SHORT_THREAD_SCHEDULING_POLICY, shortAC, shortDC),
        ]);

        LogManager.LogDebug("User requested Core Parking Mode: {0}", coreParkingMode);
    }

    [Obsolete("This function is deprecated and will be removed in future versions.")]
    private static void RequestEPP(uint EPPOverrideValue)
    {
        uint ac = (uint)Math.Max(0, (int)EPPOverrideValue - 17);
        uint dc = (uint)Math.Max(0, (int)EPPOverrideValue);

        if (RequestEPP(ac, dc))
            EPPChanged?.Invoke(EPPOverrideValue);
    }

    /// <summary>
    ///     Sets the processor energy/performance preference (powercfg percent, 0 = performance .. 100 = efficiency)
    ///     for both efficiency classes with a single scheme activation. Returns <c>true</c> when the value was
    ///     already in place or was applied and read back successfully.
    /// </summary>
    private static bool RequestEPP(uint ac, uint dc)
    {
        // Is the EPP value already correct?
        uint[] EPP = PowerScheme.ReadPowerCfg(PowerSubGroup.SUB_PROCESSOR, PowerSetting.PERFEPP);
        if (EPP[0] == ac && EPP[1] == dc)
            return true;

        LogManager.LogDebug("User requested EPP AC: {0}, DC: {1}", ac, dc);

        PowerScheme.WritePowerCfg(PowerSubGroup.SUB_PROCESSOR,
        [
            (PowerSetting.PERFEPP, ac, dc),
            (PowerSetting.PERFEPP1, ac, dc),
        ]);

        // Has the value been applied?
        EPP = PowerScheme.ReadPowerCfg(PowerSubGroup.SUB_PROCESSOR, PowerSetting.PERFEPP);
        if (EPP[0] != ac || EPP[1] != dc)
        {
            LogManager.LogWarning("Failed to set requested EPP");
            return false;
        }

        return true;
    }

    private static void RequestCPUCoreCount(int CoreCount)
    {
        uint currentCoreCountPercent = (uint)((100.0d / MotherboardInfo.NumberOfCores) * CoreCount);

        // Is the CPMINCORES value already correct?
        uint[] CPMINCORES = PowerScheme.ReadPowerCfg(PowerSubGroup.SUB_PROCESSOR, PowerSetting.CPMINCORES);
        bool CPMINCORESReady = (CPMINCORES[0] == currentCoreCountPercent && CPMINCORES[1] == currentCoreCountPercent);

        // Is the CPMAXCORES value already correct?
        uint[] CPMAXCORES = PowerScheme.ReadPowerCfg(PowerSubGroup.SUB_PROCESSOR, PowerSetting.CPMAXCORES);
        bool CPMAXCORESReady = (CPMAXCORES[0] == currentCoreCountPercent && CPMAXCORES[1] == currentCoreCountPercent);

        if (CPMINCORESReady && CPMAXCORESReady)
            return;

        // Set profile CPMINCORES and CPMAXCORES
        PowerScheme.WritePowerCfg(PowerSubGroup.SUB_PROCESSOR, PowerSetting.CPMINCORES, currentCoreCountPercent, currentCoreCountPercent);
        PowerScheme.WritePowerCfg(PowerSubGroup.SUB_PROCESSOR, PowerSetting.CPMAXCORES, currentCoreCountPercent, currentCoreCountPercent);

        LogManager.LogDebug("User requested CoreCount: {0} ({1}%)", CoreCount, currentCoreCountPercent);

        // Has the CPMINCORES value been applied?
        CPMINCORES = PowerScheme.ReadPowerCfg(PowerSubGroup.SUB_PROCESSOR, PowerSetting.CPMINCORES);
        if (CPMINCORES[0] != currentCoreCountPercent || CPMINCORES[1] != currentCoreCountPercent)
            LogManager.LogWarning("Failed to set requested CPMINCORES");

        // Has the CPMAXCORES value been applied?
        CPMAXCORES = PowerScheme.ReadPowerCfg(PowerSubGroup.SUB_PROCESSOR, PowerSetting.CPMAXCORES);
        if (CPMAXCORES[0] != currentCoreCountPercent || CPMAXCORES[1] != currentCoreCountPercent)
            LogManager.LogWarning("Failed to set requested CPMAXCORES");
    }

    private static void RequestPerfBoostMode(uint value)
    {
        // Is the PerfBoostMode value already correct?
        uint[] perfBoostMode = PowerScheme.ReadPowerCfg(PowerSubGroup.SUB_PROCESSOR, PowerSetting.PERFBOOSTMODE);
        bool IsReady = (perfBoostMode[0] == value && perfBoostMode[1] == value);

        if (IsReady)
            return;

        // Set profile PerfBoostMode
        PowerScheme.WritePowerCfg(PowerSubGroup.SUB_PROCESSOR, PowerSetting.PERFBOOSTMODE, value, value);

        LogManager.LogDebug("User requested perfboostmode: {0}", value);

        // Has the value been applied?
        perfBoostMode = PowerScheme.ReadPowerCfg(PowerSubGroup.SUB_PROCESSOR, PowerSetting.PERFBOOSTMODE);
        if (perfBoostMode[0] != value || perfBoostMode[1] != value)
            LogManager.LogWarning("Failed to set requested perfboostmode");
    }

    private static void RequestCPUClock(uint cpuClock)
    {
        // Is the PROCFREQMAX value already correct?
        uint[] currentClock = PowerScheme.ReadPowerCfg(PowerSubGroup.SUB_PROCESSOR, PowerSetting.PROCFREQMAX);
        bool IsReady = (currentClock[0] == cpuClock && currentClock[1] == cpuClock);

        if (IsReady)
            return;

        // Set profile max processor frequency
        PowerScheme.WritePowerCfg(PowerSubGroup.SUB_PROCESSOR, PowerSetting.PROCFREQMAX, cpuClock, cpuClock);
        PowerScheme.WritePowerCfg(PowerSubGroup.SUB_PROCESSOR, PowerSetting.PROCFREQMAX1, cpuClock, cpuClock);

        double maxClock = MotherboardInfo.ProcessorMaxTurboSpeed;
        double cpuPercentage = cpuClock / maxClock * 100.0d;
        LogManager.LogDebug("User requested PROCFREQMAX: {0} ({1}%)", cpuClock, cpuPercentage);

        // Has the value been applied?
        currentClock = PowerScheme.ReadPowerCfg(PowerSubGroup.SUB_PROCESSOR, PowerSetting.PROCFREQMAX);
        if (currentClock[0] != cpuClock || currentClock[1] != cpuClock)
            LogManager.LogWarning("Failed to set requested PROCFREQMAX");
    }

    public static void Resume(bool OS)
    {
        // TODO: Implement proper TDP reading
        /*
        foreach (PowerType type in (PowerType[])Enum.GetValues(typeof(PowerType)))
        {
            int idx = (int)type;
            CurrentTDP[idx] = 0;
        }
        */
    }

    public static Processor? GetProcessor() => processor;

    #region imports

    /// <summary>
    ///     Retrieves the active overlay power scheme and returns a GUID that identifies the scheme.
    /// </summary>
    /// <param name="EffectiveOverlayPolicyGuid">A pointer to a GUID structure.</param>
    /// <returns>Returns zero if the call was successful, and a nonzero value if the call failed.</returns>
    [DllImportAttribute("powrprof.dll", EntryPoint = "PowerGetEffectiveOverlayScheme")]
    private static extern uint PowerGetEffectiveOverlayScheme(out Guid EffectiveOverlayPolicyGuid);

    /// <summary>
    ///     Sets the active power overlay power scheme.
    /// </summary>
    /// <param name="OverlaySchemeGuid">The identifier of the overlay power scheme.</param>
    /// <returns>Returns zero if the call was successful, and a nonzero value if the call failed.</returns>
    [DllImportAttribute("powrprof.dll", EntryPoint = "PowerSetActiveOverlayScheme")]
    private static extern uint PowerSetActiveOverlayScheme(Guid OverlaySchemeGuid);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool FreeLibrary(IntPtr hModule);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    #endregion

    #region events

    public static event LimitChangedHandler? PowerLimitChanged;
    public delegate void LimitChangedHandler(PowerType type, int limit);

    public static event ValueChangedHandler? PowerValueChanged;
    public delegate void ValueChangedHandler(PowerType type, float value);

    public static event PowerModeChangedEventHandler? PowerModeChanged;
    public delegate void PowerModeChangedEventHandler(int idx);

    public static event PerfBoostModeChangedEventHandler? PerfBoostModeChanged;
    public delegate void PerfBoostModeChangedEventHandler(uint value);

    public static event EPPChangedEventHandler? EPPChanged;
    public delegate void EPPChangedEventHandler(uint EPP);

    /// <summary>Raised when the AutoTDP state, applied wattage or capped flag changes (not on every sample). May fire on a threadpool thread.</summary>
    public static event AutoTDPStatusChangedEventHandler? AutoTDPStatusChanged;
    public delegate void AutoTDPStatusChangedEventHandler(AutoTDPStatus status);

    #endregion
}
