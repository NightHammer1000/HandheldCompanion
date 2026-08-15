using HandheldCompanion.Managers;
using HandheldCompanion.Misc;
using System;
using System.Globalization;
using System.Windows.Input;
using Resources = HandheldCompanion.Properties.Resources;

namespace HandheldCompanion.ViewModels.Misc
{
    /// <summary>
    ///     One learned AutoTDP baseline of a game profile as shown on the profile page: which power preset it
    ///     belongs to, the target it was learned for, and its editable range. Edits are written through
    ///     <see cref="PerformanceManager.SetAutoTDPBaseline"/>, which also updates a running session.
    /// </summary>
    public class AutoTDPBaselineViewModel : BaseViewModel
    {
        private readonly Profile _profile;
        private readonly string _key;
        private double _min, _baseline, _max;

        public AutoTDPBaselineViewModel(Profile profile, string key, AutoTDPBaseline baseline)
        {
            _profile = profile;
            _key = key;
            _min = baseline.MinWatts;
            _baseline = baseline.RecentWatts;
            _max = baseline.MaxWatts;

            // key layout: executable | power profile guid
            string[] parts = key.Split('|');
            string presetName = string.Empty;
            if (parts.Length > 1 && Guid.TryParseExact(parts[1], "N", out Guid presetGuid))
                presetName = ManagerFactory.powerProfileManager.GetProfile(presetGuid)?.Name ?? string.Empty;
            string target = baseline.TargetFps > 0 ? Math.Round(baseline.TargetFps).ToString(CultureInfo.InvariantCulture) : "?";

            Header = $"{presetName} · {target} FPS".TrimStart(' ', '·');
            Description = string.Format(Resources.ProfilesPage_AutoTDPBaselineEntryDesc,
                baseline.LastUpdatedUtc == default ? "-" : baseline.LastUpdatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
                baseline.FloorLocked ? Resources.ProfilesPage_AutoTDPBaselineFloorLocked : Resources.ProfilesPage_AutoTDPBaselineFloorOpen,
                baseline.Samples);

            RemoveCommand = new DelegateCommand(() => PerformanceManager.RemoveAutoTDPBaseline(_profile, _key));
        }

        public string Key => _key;
        public string Header { get; }
        public string Description { get; }
        public ICommand RemoveCommand { get; }

        public double MinimumWatts => PerformanceManager.GetMinimumTDP();
        public double MaximumWatts => PerformanceManager.GetMaximumTDP();

        public double MinWatts
        {
            get => _min;
            set
            {
                if (double.IsNaN(value) || value == _min)
                    return;
                _min = value;
                if (_baseline < _min) _baseline = _min;
                if (_max < _min) _max = _min;
                Commit();
            }
        }

        public double BaselineWatts
        {
            get => _baseline;
            set
            {
                if (double.IsNaN(value) || value == _baseline)
                    return;
                _baseline = value;
                if (_min > _baseline) _min = _baseline;
                if (_max < _baseline) _max = _baseline;
                Commit();
            }
        }

        public double MaxWatts
        {
            get => _max;
            set
            {
                if (double.IsNaN(value) || value == _max)
                    return;
                _max = value;
                if (_baseline > _max) _baseline = _max;
                if (_min > _max) _min = _max;
                Commit();
            }
        }

        private void Commit()
        {
            PerformanceManager.SetAutoTDPBaseline(_profile, _key, _min, _baseline, _max);
            OnPropertyChanged(nameof(MinWatts));
            OnPropertyChanged(nameof(BaselineWatts));
            OnPropertyChanged(nameof(MaxWatts));
        }
    }
}
