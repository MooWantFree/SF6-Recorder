using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace Sf6Prototype;

public partial class UpdateNotice : UserControl
{
    private (UpdateRelease Release, Action Install, Action<UpdateReminder> Save)? _offer;

    public UpdateNotice() => InitializeComponent();

    public void Show(UpdateRelease release, Action install, Action<UpdateReminder> saveReminder)
    {
        _offer = (release, install, saveReminder);
        HintText.SetResourceReference(TextBlock.TextProperty, "I18n.InstallUpdateHelp");
        HintText.SetResourceReference(TextBlock.ForegroundProperty, "Ink2");
        Visibility = Visibility.Visible;
        RefreshLanguage();
    }

    public void RefreshLanguage()
    {
        if (_offer is not { } offer) return;
        MessageText.Text = offer.Release.AvailableMessage;
        UIElementAutomationPeer.FromElement(this)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    public void Dismiss()
    {
        Visibility = Visibility.Collapsed;
        _offer = null;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Dismiss();

    private void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_offer is not { } offer) return;
        Dismiss();
        offer.Install();
    }

    private void Defer_Click(object sender, RoutedEventArgs e)
    {
        if (_offer is not { } offer) return;
        var reminder = sender == IgnoreUpdateButton ? new UpdateReminder(IgnoredVersion: offer.Release.Version.ToString(3)) :
            UpdateReminder.InFiveDays(DateTimeOffset.UtcNow);
        try
        {
            offer.Save(reminder);
            Dismiss();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Diagnostics.Write("updates", "reminder_save_failed", error);
            HintText.SetResourceReference(TextBlock.TextProperty, "I18n.UpdateReminderSaveFailed");
            HintText.SetResourceReference(TextBlock.ForegroundProperty, "Loss");
            UIElementAutomationPeer.FromElement(this)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }
}
