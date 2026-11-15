using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Luma.App.Services;

namespace Luma.App.ViewModels;

/// <summary>A small form (type, title, description) that sends feedback to the developer.</summary>
public sealed partial class FeedbackViewModel : PageViewModel
{
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(30);

    private readonly FeedbackService _feedback;
    private readonly IToastService _toasts;
    private readonly TimeProvider _time;
    private DateTimeOffset _lastSent = DateTimeOffset.MinValue;

    public FeedbackViewModel(FeedbackService feedback, IToastService toasts, TimeProvider? time = null)
    {
        _feedback = feedback;
        _toasts = toasts;
        _time = time ?? TimeProvider.System;
        TypeOptions = Enum.GetValues<FeedbackType>().Select(t => new Option<FeedbackType>(FeedbackService.Label(t), t)).ToList();
        _selectedType = TypeOptions[0];
    }

    public override string Title => "Feedback";

    public IReadOnlyList<Option<FeedbackType>> TypeOptions { get; }

    public int MaxTitleLength => FeedbackService.MaxTitleLength;

    public int MaxDescriptionLength => FeedbackService.MaxDescriptionLength;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    private Option<FeedbackType> _selectedType;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    private string _feedbackTitle = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    [NotifyPropertyChangedFor(nameof(DescriptionCounter))]
    private string _description = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    [NotifyPropertyChangedFor(nameof(SubmitText))]
    private bool _isSending;

    [ObservableProperty]
    private string? _error;

    public string DescriptionCounter => $"{Description.Length} / {MaxDescriptionLength}";

    public string SubmitText => IsSending ? "Sending…" : "Send feedback";

    private bool CanSubmit() => !IsSending && !string.IsNullOrWhiteSpace(FeedbackTitle) && !string.IsNullOrWhiteSpace(Description);

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private async Task SubmitAsync()
    {
        var wait = _lastSent + Cooldown - _time.GetUtcNow();
        if (wait > TimeSpan.Zero)
        {
            Error = $"Please wait {Math.Ceiling(wait.TotalSeconds)} seconds before sending more feedback.";
            return;
        }

        Error = null;
        IsSending = true;
        try
        {
            var error = await _feedback.SendAsync(new FeedbackSubmission(SelectedType.Value, FeedbackTitle, Description));
            if (error is not null)
            {
                Error = error;
                return;
            }
            _lastSent = _time.GetUtcNow();
            FeedbackTitle = string.Empty;
            Description = string.Empty;
            SelectedType = TypeOptions[0];
            _toasts.Show("Thanks! Your feedback was sent.", ToastKind.Success);
        }
        finally
        {
            IsSending = false;
        }
    }
}
