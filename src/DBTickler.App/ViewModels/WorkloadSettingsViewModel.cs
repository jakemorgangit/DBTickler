using System;
using System.Collections.Generic;
using System.Linq;
using DBTickler.Core.Configuration;

namespace DBTickler.App.ViewModels;

/// <summary>
/// The workload knobs, with the DML mix kept summing to 100 as the operator drags sliders.
/// </summary>
public sealed class WorkloadSettingsViewModel : ObservableObject
{
    private bool _rebalancing;

    private int _virtualUsers = 16;
    private int _batchRows = 50;
    private int _payloadBytes = 2048;
    private int _durationSeconds = 60;
    private int _rampUpSeconds = 5;
    private int _readPercent = 100;
    private int _insertPercent;
    private int _updatePercent;
    private int _deletePercent;
    private int _thinkTimeMs;
    private bool _thinkTimeJitter = true;
    private int _commandTimeoutSeconds = 30;
    private int _maxErrors = 500;
    private bool _safeMode = true;
    private bool _chaosMode;
    private bool _chaosBadQueries = true;
    private bool _chaosConcurrency = true;
    private bool _chaosResourceBurners = true;
    private int _chaosIntensityPercent = 25;
    private string _randomSeed = "";

    public IReadOnlyList<string> PresetNames { get; } = [.. WorkloadProfile.Presets.Keys];

    public int VirtualUsers
    {
        get => _virtualUsers;
        set => SetProperty(ref _virtualUsers, Math.Clamp(value, 1, 512));
    }

    public int BatchRows
    {
        get => _batchRows;
        set { if (SetProperty(ref _batchRows, Math.Clamp(value, 1, 10_000))) OnPropertyChanged(nameof(WriteVolumeHint)); }
    }

    public int PayloadBytes
    {
        get => _payloadBytes;
        set { if (SetProperty(ref _payloadBytes, Math.Clamp(value, 0, 1024 * 1024))) OnPropertyChanged(nameof(WriteVolumeHint)); }
    }

    public int DurationSeconds
    {
        get => _durationSeconds;
        set
        {
            if (!SetProperty(ref _durationSeconds, Math.Clamp(value, 0, 86_400))) return;
            if (_rampUpSeconds > _durationSeconds && _durationSeconds > 0)
                RampUpSeconds = _durationSeconds;
            OnPropertyChanged(nameof(DurationHint));
        }
    }

    public int RampUpSeconds
    {
        get => _rampUpSeconds;
        set => SetProperty(ref _rampUpSeconds, Math.Clamp(value, 0, 3600));
    }

    public int ReadPercent
    {
        get => _readPercent;
        set { if (SetProperty(ref _readPercent, Clamp(value))) Rebalance(nameof(ReadPercent)); }
    }

    public int InsertPercent
    {
        get => _insertPercent;
        set { if (SetProperty(ref _insertPercent, Clamp(value))) Rebalance(nameof(InsertPercent)); }
    }

    public int UpdatePercent
    {
        get => _updatePercent;
        set { if (SetProperty(ref _updatePercent, Clamp(value))) Rebalance(nameof(UpdatePercent)); }
    }

    public int DeletePercent
    {
        get => _deletePercent;
        set { if (SetProperty(ref _deletePercent, Clamp(value))) Rebalance(nameof(DeletePercent)); }
    }

    public int ThinkTimeMs
    {
        get => _thinkTimeMs;
        set => SetProperty(ref _thinkTimeMs, Math.Clamp(value, 0, 60_000));
    }

    public bool ThinkTimeJitter
    {
        get => _thinkTimeJitter;
        set => SetProperty(ref _thinkTimeJitter, value);
    }

    public int CommandTimeoutSeconds
    {
        get => _commandTimeoutSeconds;
        set => SetProperty(ref _commandTimeoutSeconds, Math.Clamp(value, 1, 3600));
    }

    public int MaxErrors
    {
        get => _maxErrors;
        set => SetProperty(ref _maxErrors, Math.Clamp(value, 0, 1_000_000));
    }

    /// <summary>
    /// When on, the engine is handed a profile with the write share folded into reads, so
    /// writes are impossible rather than merely discouraged by the UI.
    /// </summary>
    public bool SafeMode
    {
        get => _safeMode;
        set
        {
            if (!SetProperty(ref _safeMode, value)) return;
            OnPropertyChanged(nameof(WritesEnabled));
            OnPropertyChanged(nameof(SafetyHint));
            if (value) ApplyReadOnlyMix();
        }
    }

    public bool WritesEnabled => !_safeMode;

    public bool ChaosMode
    {
        get => _chaosMode;
        set
        {
            if (!SetProperty(ref _chaosMode, value)) return;
            OnPropertyChanged(nameof(SafetyHint));
        }
    }

    public bool ChaosBadQueries
    {
        get => _chaosBadQueries;
        set => SetProperty(ref _chaosBadQueries, value);
    }

    public bool ChaosConcurrency
    {
        get => _chaosConcurrency;
        set => SetProperty(ref _chaosConcurrency, value);
    }

    public bool ChaosResourceBurners
    {
        get => _chaosResourceBurners;
        set => SetProperty(ref _chaosResourceBurners, value);
    }

    public int ChaosIntensityPercent
    {
        get => _chaosIntensityPercent;
        set => SetProperty(ref _chaosIntensityPercent, Math.Clamp(value, 0, 100));
    }

    /// <summary>Held as text so the field can be left empty to mean "seed from the clock".</summary>
    public string RandomSeed
    {
        get => _randomSeed;
        set => SetProperty(ref _randomSeed, value);
    }

    public string DurationHint => _durationSeconds == 0
        ? "Runs until you press Stop."
        : $"Runs for {TimeSpan.FromSeconds(_durationSeconds):hh\\:mm\\:ss}.";

    public string WriteVolumeHint
    {
        get
        {
            var perOperation = (long)_batchRows * _payloadBytes;
            return perOperation < 1024
                ? $"About {perOperation} B per write operation."
                : perOperation < 1024 * 1024
                    ? $"About {perOperation / 1024.0:F0} KB per write operation."
                    : $"About {perOperation / (1024.0 * 1024.0):F1} MB per write operation.";
        }
    }

    public string SafetyHint => _safeMode
        ? "Safe mode: reads only. Nothing on the target will be modified."
        : _chaosMode
            ? "Writes and chaos enabled — expect blocking, deadlocks and resource pressure."
            : "Writes enabled against dbo.LoadGen. Other tables are never modified.";

    private static int Clamp(int value) => Math.Clamp(value, 0, 100);

    private void ApplyReadOnlyMix()
    {
        _rebalancing = true;
        try
        {
            _readPercent = 100;
            _insertPercent = 0;
            _updatePercent = 0;
            _deletePercent = 0;
            NotifyMixChanged();
        }
        finally
        {
            _rebalancing = false;
        }
    }

    /// <summary>
    /// Spreads the remainder across the other three shares so the mix always totals 100.
    /// The reentrancy guard is what keeps one adjustment from triggering another; v1 achieved
    /// the same thing by unsubscribing and resubscribing four event handlers on every change.
    /// </summary>
    private void Rebalance(string changedProperty)
    {
        if (_rebalancing) return;

        _rebalancing = true;
        try
        {
            var others = new (string Name, Func<int> Get, Action<int> Set)[]
            {
                (nameof(ReadPercent), () => _readPercent, value => _readPercent = value),
                (nameof(InsertPercent), () => _insertPercent, value => _insertPercent = value),
                (nameof(UpdatePercent), () => _updatePercent, value => _updatePercent = value),
                (nameof(DeletePercent), () => _deletePercent, value => _deletePercent = value),
            }
            .Where(entry => entry.Name != changedProperty)
            .ToArray();

            var changedValue = changedProperty switch
            {
                nameof(ReadPercent) => _readPercent,
                nameof(InsertPercent) => _insertPercent,
                nameof(UpdatePercent) => _updatePercent,
                _ => _deletePercent,
            };

            var remaining = 100 - changedValue;
            var currentTotal = others.Sum(entry => entry.Get());

            if (currentTotal == 0)
            {
                // Nothing to scale, so share the remainder out evenly and give any odd
                // percentage point to the first slider.
                var each = remaining / others.Length;
                var leftover = remaining % others.Length;
                for (var i = 0; i < others.Length; i++)
                    others[i].Set(each + (i == 0 ? leftover : 0));
            }
            else
            {
                var assigned = 0;
                for (var i = 0; i < others.Length - 1; i++)
                {
                    var share = (int)Math.Round((double)remaining * others[i].Get() / currentTotal);
                    others[i].Set(share);
                    assigned += share;
                }
                // The last slider absorbs the rounding error so the total is exactly 100.
                others[^1].Set(remaining - assigned);
            }

            NotifyMixChanged();
        }
        finally
        {
            _rebalancing = false;
        }
    }

    private void NotifyMixChanged()
    {
        OnPropertyChanged(nameof(ReadPercent));
        OnPropertyChanged(nameof(InsertPercent));
        OnPropertyChanged(nameof(UpdatePercent));
        OnPropertyChanged(nameof(DeletePercent));
        OnPropertyChanged(nameof(MixSummary));
    }

    public string MixSummary =>
        $"{_readPercent}% read · {_insertPercent}% insert · {_updatePercent}% update · {_deletePercent}% delete";

    public WorkloadProfile ToProfile() => new()
    {
        VirtualUsers = VirtualUsers,
        BatchRows = BatchRows,
        PayloadBytes = PayloadBytes,
        DurationSeconds = DurationSeconds,
        RampUpSeconds = RampUpSeconds,
        ReadPercent = ReadPercent,
        InsertPercent = InsertPercent,
        UpdatePercent = UpdatePercent,
        DeletePercent = DeletePercent,
        ThinkTimeMs = ThinkTimeMs,
        ThinkTimeJitter = ThinkTimeJitter,
        CommandTimeoutSeconds = CommandTimeoutSeconds,
        MaxErrors = MaxErrors,
        SafeMode = SafeMode,
        ChaosMode = ChaosMode,
        ChaosBadQueries = ChaosBadQueries,
        ChaosConcurrency = ChaosConcurrency,
        ChaosResourceBurners = ChaosResourceBurners,
        ChaosIntensityPercent = ChaosIntensityPercent,
        RandomSeed = int.TryParse(RandomSeed, out var seed) ? seed : null,
    };

    public void LoadFrom(WorkloadProfile profile)
    {
        _rebalancing = true;
        try
        {
            _virtualUsers = profile.VirtualUsers;
            _batchRows = profile.BatchRows;
            _payloadBytes = profile.PayloadBytes;
            _durationSeconds = profile.DurationSeconds;
            _rampUpSeconds = profile.RampUpSeconds;
            _readPercent = profile.ReadPercent;
            _insertPercent = profile.InsertPercent;
            _updatePercent = profile.UpdatePercent;
            _deletePercent = profile.DeletePercent;
            _thinkTimeMs = profile.ThinkTimeMs;
            _thinkTimeJitter = profile.ThinkTimeJitter;
            _commandTimeoutSeconds = profile.CommandTimeoutSeconds;
            _maxErrors = profile.MaxErrors;
            _safeMode = profile.SafeMode;
            _chaosMode = profile.ChaosMode;
            _chaosBadQueries = profile.ChaosBadQueries;
            _chaosConcurrency = profile.ChaosConcurrency;
            _chaosResourceBurners = profile.ChaosResourceBurners;
            _chaosIntensityPercent = profile.ChaosIntensityPercent;
            _randomSeed = profile.RandomSeed?.ToString() ?? "";
        }
        finally
        {
            _rebalancing = false;
        }

        foreach (var name in new[]
        {
            nameof(VirtualUsers), nameof(BatchRows), nameof(PayloadBytes), nameof(DurationSeconds),
            nameof(RampUpSeconds), nameof(ThinkTimeMs), nameof(ThinkTimeJitter), nameof(CommandTimeoutSeconds),
            nameof(MaxErrors), nameof(SafeMode), nameof(WritesEnabled), nameof(ChaosMode),
            nameof(ChaosBadQueries), nameof(ChaosConcurrency), nameof(ChaosResourceBurners),
            nameof(ChaosIntensityPercent), nameof(RandomSeed), nameof(DurationHint),
            nameof(WriteVolumeHint), nameof(SafetyHint),
        })
        {
            OnPropertyChanged(name);
        }

        NotifyMixChanged();
    }

    public void ApplyPreset(string presetName)
    {
        if (WorkloadProfile.Presets.TryGetValue(presetName, out var factory))
            LoadFrom(factory());
    }
}
