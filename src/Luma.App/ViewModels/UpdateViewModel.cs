using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Luma.App.Services;
using Luma.App.Services.Updates;
using Luma.Core.Logic;

namespace Luma.App.ViewModels;

/// <summary>
/// UI state for updates: the floating update card in the main window and the "Updates" row in Settings share it.
/// All service events are marshalled to the UI thread here.
/// </summary>
public sealed partial class UpdateViewModel : ObservableObject
{
    private readonly IUpdateService _updates;
    private readonly IToastService _toasts;
    private UpdateSnapshot _snapshot;

    public UpdateViewModel(IUpdateService updates, IToastService toasts)
    {
        _updates = updates;
        _toasts = toasts;
        _snapshot = updates.Current;
        updates.Changed += (_, snapshot) => Dispatcher.UIThread.Post(() => Apply(snapshot));
    }

    // ----- Card (main window) -----

    /// <summary>The card appears only for something the user can act on, and never for background check failures.</summary>
    public bool IsCardVisible => _snapshot.Status switch
    {
        UpdateStatus.Available => !_updates.IsPostponed,
        UpdateStatus.Downloading or UpdateStatus.ReadyToRestart or UpdateStatus.Restarting => !_updates.IsPostponed,
        UpdateStatus.Failed => _snapshot.FailedOperation is UpdateOperation.Download or UpdateOperation.Apply && !_updates.IsPostponed,
        _ => false
    };

    public bool IsAvailable => _snapshot.Status == UpdateStatus.Available;
    public bool IsDownloading => _snapshot.Status == UpdateStatus.Downloading;
    public bool IsReady => _snapshot.Status == UpdateStatus.ReadyToRestart;
    public bool IsRestarting => _snapshot.Status == UpdateStatus.Restarting;
    public bool IsFailed => _snapshot.Status == UpdateStatus.Failed;
    public bool IsChecking => _snapshot.Status == UpdateStatus.Checking;
    public bool CanRetryDownload => IsFailed && _snapshot.FailedOperation == UpdateOperation.Download;

    public int Progress => _snapshot.Progress;
    public string ProgressText => $"Downloading…  {_snapshot.Progress}%";

    public string CardTitle => _snapshot.Status switch
    {
        UpdateStatus.ReadyToRestart => "Ready to install",
        UpdateStatus.Restarting => "Restarting Luma…",
        UpdateStatus.Failed => "Update didn't finish",
        UpdateStatus.Downloading => "Downloading update",
        _ => "A new version of Luma is here"
    };

    public string VersionText => _snapshot.Release is { } r
        ? r.SizeBytes > 0 && IsAvailable
            ? $"Version {r.Version}  ·  {ByteSizeFormatter.Format(r.SizeBytes)}"
            : $"Version {r.Version}"
        : string.Empty;

    public string CardMessage => _snapshot.Status switch
    {
        UpdateStatus.ReadyToRestart => "Luma will close for a moment, install the update and open again. Your library and settings stay exactly as they are. If you choose Later, it installs the next time Luma starts.",
        UpdateStatus.Restarting => "Installing the update. Luma will reopen by itself.",
        UpdateStatus.Failed => _snapshot.Error ?? "Something went wrong.",
        _ => string.Empty
    };

    public bool HasCardMessage => !string.IsNullOrEmpty(CardMessage);

    public string? ReleaseNotes => FormatReleaseNotes(_snapshot.Release?.ReleaseNotes);
    public bool HasReleaseNotes => IsAvailable && !string.IsNullOrWhiteSpace(ReleaseNotes);

    [ObservableProperty]
    private bool _showNotes;

    // ----- Settings row -----

    public bool IsSupported => _updates.IsSupported;
    public string CurrentVersion => _snapshot.CurrentVersion ?? AppVersion.Current;

    public string SettingsStatus => _snapshot.Status switch
    {
        UpdateStatus.Unsupported => "Automatic updates work in installed copies of Luma. This build was not installed with the Luma installer.",
        UpdateStatus.Idle => "Luma checks for updates in the background.",
        UpdateStatus.Checking => "Checking for updates…",
        UpdateStatus.UpToDate => $"You're on the latest version.{LastChecked}",
        UpdateStatus.Available => $"Version {_snapshot.Release?.Version} is available.",
        UpdateStatus.Downloading => $"Downloading version {_snapshot.Release?.Version}…  {_snapshot.Progress}%",
        UpdateStatus.ReadyToRestart => $"Version {_snapshot.Release?.Version} is ready. Restart Luma to install it.",
        UpdateStatus.Restarting => "Restarting to install the update…",
        UpdateStatus.Failed => _snapshot.Error ?? "The last update attempt failed.",
        _ => string.Empty
    };

    private string LastChecked => _snapshot.LastCheckedAt is { } at ? $"  Last checked at {at.ToLocalTime():t}." : string.Empty;

    public bool CanCheck => IsSupported && _snapshot.Status is not (UpdateStatus.Checking or UpdateStatus.Downloading or UpdateStatus.Restarting);

    // ----- Commands -----

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (!CanCheck) return;
        var outcome = await _updates.CheckAsync(userInitiated: true);
        switch (outcome)
        {
            case UpdateCheckOutcome.UpToDate:
                _toasts.Show($"You're up to date. Luma {CurrentVersion} is the latest version.", ToastKind.Success);
                break;
            case UpdateCheckOutcome.Failed:
                _toasts.Show(_updates.Current.Error ?? "Luma couldn't check for updates right now.", ToastKind.Warning);
                break;
            case UpdateCheckOutcome.Unsupported:
                _toasts.Show("This copy of Luma wasn't installed with the Luma installer, so it can't update itself.", ToastKind.Info);
                break;
        }
        Refresh();
    }

    [RelayCommand]
    private async Task UpdateNowAsync()
    {
        ShowNotes = false;
        // Download only; the card then switches to "Ready to install" so the user restarts when it suits them.
        await _updates.DownloadAsync();
    }

    [RelayCommand]
    private void Restart() => _updates.RestartToApply();

    [RelayCommand]
    private void CancelDownload() => _updates.CancelDownload();

    [RelayCommand]
    private void Later()
    {
        _updates.Postpone();
        ShowNotes = false;
        Refresh();
    }

    [RelayCommand]
    private void ToggleNotes() => ShowNotes = !ShowNotes;

    private void Apply(UpdateSnapshot snapshot)
    {
        _snapshot = snapshot;
        Refresh();
    }

    private void Refresh() => OnPropertyChanged(string.Empty);

    /// <summary>Turns GitHub-flavoured release notes into tidy plain text for the card (no Markdown renderer in Luma).</summary>
    public static string? FormatReleaseNotes(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return null;
        var builder = new StringBuilder();
        var blank = false;
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0)
            {
                if (builder.Length > 0) blank = true;
                continue;
            }
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#')) trimmed = trimmed.TrimStart('#').Trim();
            else if (trimmed.StartsWith("- ") || trimmed.StartsWith("* ") || trimmed.StartsWith("+ ")) trimmed = "•  " + trimmed[2..].Trim();
            trimmed = trimmed.Replace("**", string.Empty).Replace("__", string.Empty).Replace("`", string.Empty);
            if (trimmed.Length == 0) continue;

            if (builder.Length > 0) builder.Append(blank ? "\n\n" : "\n");
            builder.Append(trimmed);
            blank = false;
        }
        return builder.Length == 0 ? null : builder.ToString();
    }
}
