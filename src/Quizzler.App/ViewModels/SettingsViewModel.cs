using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Quizzler.Ai;
using Quizzler.App.Services;
using Quizzler.Core.Scheduling;
using Quizzler.Core.Study;
using Quizzler.Data;

namespace Quizzler.App.ViewModels;

public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>What the settings window needs from the app.</summary>
internal interface ISettingsHost
{
    QuizDatabase Database { get; }
    void SendTestNotification();
    void OpenDataFolder();
    void OpenLog();

    /// <summary>Called after settings are saved so the tray/reminders pick them up immediately.</summary>
    void SettingsSaved();
}

internal sealed class SettingsViewModel : ObservableObject
{
    public const string ThemeSettingKey = "theme";

    private readonly ISettingsHost _host;
    private readonly AppTheme _originalTheme;

    private Choice<AppTheme> _theme;
    private string _newPerDay;
    private Choice<int> _rolloverHour;
    private bool _notificationsEnabled;
    private Choice<int> _reminderInterval;
    private bool _quietHoursEnabled;
    private Choice<int> _quietStart;
    private Choice<int> _quietEnd;
    private bool _startWithWindows;
    private string _error = "";

    public SettingsViewModel(ISettingsHost host)
    {
        _host = host;
        var db = host.Database;
        var study = db.GetStudySettings();
        var notify = db.GetNotificationSettings();

        _originalTheme = ThemeService.Parse(db.GetSetting(ThemeSettingKey));
        _theme = Themes.First(t => t.Value == _originalTheme);
        _newPerDay = study.NewPerDay.ToString(CultureInfo.InvariantCulture);
        _rolloverHour = Hour(study.DayRolloverHour);
        _notificationsEnabled = notify.Enabled;
        _reminderInterval = Intervals.FirstOrDefault(i => i.Value == notify.ReminderIntervalMinutes)
            ?? new Choice<int>(notify.ReminderIntervalMinutes, $"Every {notify.ReminderIntervalMinutes} minutes");
        _quietHoursEnabled = notify.QuietStartHour != notify.QuietEndHour;
        _quietStart = Hour(_quietHoursEnabled ? notify.QuietStartHour : 22);
        _quietEnd = Hour(_quietHoursEnabled ? notify.QuietEndHour : 8);
        _startWithWindows = StartupRegistration.IsEnabled;

        if (!Intervals.Contains(_reminderInterval)) Intervals = [.. Intervals, _reminderInterval];

        _keys = new ApiKeyStore(db.GetSetting, db.SetOrDeleteSetting);
        RemoveApiKeyCommand = new RelayCommand(() =>
        {
            _removeKey = true;
            NewApiKey = "";
            OnPropertyChanged(nameof(ApiKeyStatus));
            OnPropertyChanged(nameof(CanRemoveApiKey));
        });

        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(Cancel);
        TestNotificationCommand = new RelayCommand(host.SendTestNotification);
        OpenDataFolderCommand = new RelayCommand(host.OpenDataFolder);
        OpenLogCommand = new RelayCommand(host.OpenLog);
    }

    public event EventHandler? CloseRequested;

    public IReadOnlyList<Choice<AppTheme>> Themes { get; } =
    [
        new(AppTheme.System, "Match Windows"),
        new(AppTheme.Light, "Light"),
        new(AppTheme.Dark, "Dark"),
    ];

    public IReadOnlyList<Choice<int>> Hours { get; } =
        Enumerable.Range(0, 24).Select(h => new Choice<int>(h, $"{h:00}:00")).ToList();

    public IReadOnlyList<Choice<int>> Intervals { get; private set; } =
    [
        new(15, "Every 15 minutes"), new(30, "Every 30 minutes"), new(45, "Every 45 minutes"),
        new(60, "Every hour"), new(90, "Every 90 minutes"), new(120, "Every 2 hours"),
        new(180, "Every 3 hours"), new(240, "Every 4 hours"),
    ];

    // ------------------------------------------------------------------ Claude (explain my mistake)

    private readonly ApiKeyStore _keys;
    private bool _removeKey;
    private string _newApiKey = "";

    /// <summary>Typed into the password box (never read back from storage).</summary>
    public string NewApiKey
    {
        get => _newApiKey;
        set
        {
            if (SetProperty(ref _newApiKey, value) && value.Length > 0) _removeKey = false;
        }
    }

    public RelayCommand RemoveApiKeyCommand { get; }

    public bool CanRemoveApiKey => _keys.HasSavedKey && !_removeKey;

    public string ApiKeyStatus =>
        ApiKeyStore.FromEnvironment is not null ? "Using the ANTHROPIC_API_KEY environment variable."
        : _removeKey ? "The saved key will be removed when you save."
        : _keys.HasSavedKey ? "A key is saved (encrypted for your Windows account)."
        : "No key yet. Without one, \"Ask Claude\" will point you here.";

    public RelayCommand SaveCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand TestNotificationCommand { get; }
    public RelayCommand OpenDataFolderCommand { get; }
    public RelayCommand OpenLogCommand { get; }

    public string DataFolder => System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(_host.Database.Path))!;
    public bool IsDevBuild => StartupRegistration.IsDevBuild;

    /// <summary>Selecting a theme previews it immediately; Cancel puts the original back.</summary>
    public Choice<AppTheme> Theme
    {
        get => _theme;
        set
        {
            if (SetProperty(ref _theme, value)) ThemeService.Apply(value.Value);
        }
    }

    public string NewPerDay { get => _newPerDay; set => SetProperty(ref _newPerDay, value); }
    public Choice<int> RolloverHour { get => _rolloverHour; set => SetProperty(ref _rolloverHour, value); }
    public bool NotificationsEnabled { get => _notificationsEnabled; set => SetProperty(ref _notificationsEnabled, value); }
    public Choice<int> ReminderInterval { get => _reminderInterval; set => SetProperty(ref _reminderInterval, value); }
    public bool QuietHoursEnabled { get => _quietHoursEnabled; set => SetProperty(ref _quietHoursEnabled, value); }
    public Choice<int> QuietStart { get => _quietStart; set => SetProperty(ref _quietStart, value); }
    public Choice<int> QuietEnd { get => _quietEnd; set => SetProperty(ref _quietEnd, value); }
    public bool StartWithWindows { get => _startWithWindows; set => SetProperty(ref _startWithWindows, value); }

    public string Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => Error.Length > 0;

    private Choice<int> Hour(int h) => Hours[Math.Clamp(h, 0, 23)];

    private void Save()
    {
        if (!int.TryParse(NewPerDay.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var perDay) || perDay is < 0 or > 500)
        {
            Error = "New questions per day must be a whole number from 0 to 500.";
            return;
        }
        if (QuietHoursEnabled && QuietStart.Value == QuietEnd.Value)
        {
            Error = "Quiet hours need different start and end times.";
            return;
        }

        try
        {
            var db = _host.Database;
            db.SetSetting(ThemeSettingKey, Theme.Value.ToString().ToLowerInvariant());
            db.SaveStudySettings(new StudySettings { NewPerDay = perDay, DayRolloverHour = RolloverHour.Value });
            db.SaveNotificationSettings(new NotificationSettings
            {
                Enabled = NotificationsEnabled,
                ReminderIntervalMinutes = ReminderInterval.Value,
                QuietStartHour = QuietHoursEnabled ? QuietStart.Value : 0,
                QuietEndHour = QuietHoursEnabled ? QuietEnd.Value : 0,
            });

            if (NewApiKey.Trim().Length > 0) _keys.Save(NewApiKey.Trim());
            else if (_removeKey) _keys.Save(null);

            if (StartWithWindows != StartupRegistration.IsEnabled)
            {
                if (StartWithWindows) StartupRegistration.Enable();
                else StartupRegistration.Disable();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Saving settings failed.", ex);
            Error = $"Couldn't save: {ex.Message}";
            return;
        }

        _host.SettingsSaved();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    public void Cancel()
    {
        RevertPreview();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Undo a theme that was only being previewed.</summary>
    public void RevertPreview()
    {
        if (Theme.Value != _originalTheme) ThemeService.Apply(_originalTheme);
    }
}
