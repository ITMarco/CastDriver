using System.Linq;
using System.Windows;
using Application = System.Windows.Application;

namespace CastDriver.UI;

// What the user chose in the "try MP3 first" Sonos hint dialog.
public enum SonosHintChoice
{
    Cancel,        // closed without choosing — abort the switch
    Proceed,       // "Okay" — switch this player to compatibility mode
    ChangeToMp3,   // switch the stream to MP3 and keep the fast path (don't go compatibility)
    DontShowAgain, // switch to compatibility mode and never show this hint again
}

public partial class SonosCompatibilityDialog : Window
{
    public SonosHintChoice Choice { get; private set; } = SonosHintChoice.Cancel;

    public SonosCompatibilityDialog()
    {
        InitializeComponent();

        Owner = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault();
        if (Owner == null) WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    private void OnProceed(object sender, RoutedEventArgs e)       => Pick(SonosHintChoice.Proceed);
    private void OnChangeMp3(object sender, RoutedEventArgs e)     => Pick(SonosHintChoice.ChangeToMp3);
    private void OnDontShowAgain(object sender, RoutedEventArgs e) => Pick(SonosHintChoice.DontShowAgain);

    private void Pick(SonosHintChoice choice)
    {
        Choice = choice;
        Close();
    }
}
