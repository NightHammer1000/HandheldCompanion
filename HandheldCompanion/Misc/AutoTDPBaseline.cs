using System;

namespace HandheldCompanion.Misc
{
    /// <summary>
    ///     Learned AutoTDP operating point for one game under one control configuration, persisted on the
    ///     game's <see cref="Profile"/> (see <see cref="Profile.AutoTDPBaselines"/>). Values are watts as
    ///     applied to the hardware (integer quanta). A baseline is a warm-start hint for the controller,
    ///     never a hard bound: upward correction is always allowed regardless of the stored range.
    /// </summary>
    [Serializable]
    public class AutoTDPBaseline
    {
        /// <summary>Slow EWMA of converged hold levels across sessions.</summary>
        public double TypicalWatts { get; set; }

        /// <summary>Hold level at the most recent convergence or session end.</summary>
        public double RecentWatts { get; set; }

        /// <summary>Lowest applied level that held the target (probe floor + 1).</summary>
        public double MinWatts { get; set; }

        /// <summary>Highest applied level the game needed to clear a deficit.</summary>
        public double MaxWatts { get; set; }

        /// <summary>
        ///     <see cref="MinWatts"/> was established by failed probes below it (or by holding the device minimum):
        ///     the range is complete and later sessions track instead of learn.
        /// </summary>
        public bool FloorLocked { get; set; }

        /// <summary>Number of convergences folded into <see cref="TypicalWatts"/>.</summary>
        public int Samples { get; set; }

        public DateTime LastUpdatedUtc { get; set; }

        /// <summary>CPU package temperature (°C) at the last convergence; used to widen the seed margin on a hot device.</summary>
        public float TemperatureAtConvergence { get; set; }

        /// <summary>
        ///     Wattage to seed a new session with: the most recent converged level plus a positive margin. The slow
        ///     EWMA (<see cref="TypicalWatts"/>) is kept for reference only - anchoring the seed to it would carry an
        ///     early, still-pessimistic convergence into later sessions.
        /// </summary>
        public double GetSeedWatts(double marginFloor = 1.0, double marginRatio = 0.10)
        {
            double basis = RecentWatts > 0 ? RecentWatts : TypicalWatts;
            return basis + Math.Max(marginFloor, basis * marginRatio);
        }

        public AutoTDPBaseline Clone()
        {
            return (AutoTDPBaseline)MemberwiseClone();
        }
    }
}
