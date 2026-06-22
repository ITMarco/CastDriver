using System.Linq;
using System.Windows;
using Application = System.Windows.Application;

namespace CastDriver.UI;

// What the user chose in the "try MP3 first" Sonos hint dialog.
public enum SonosHintChoice
{
    Cancel,               // closed without choosing — abort the switch
    EnableCompatibility,  // switch this player to the generic DLNA compatibility path
    ChangeToMp3,          // switch the stream to MP3 and keep the fast path instead
}

public partial class SonosCompatibilityDialog : Window
{
    public SonosHintChoice Choice { get; private set; } = SonosHintChoice.Cancel;
    public bool DontShowAgain => DontShowAgainCheck.IsChecked == true;

    public SonosCompatibilityDialog()
    {
        InitializeComponent();

        Owner = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault();
        if (Owner == null) WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    private void OnEnableCompatibility(object sender, RoutedEventArgs e) => Pick(SonosHintChoice.EnableCompatibility);
    private void OnChangeMp3(object sender, RoutedEventArgs e)           => Pick(SonosHintChoice.ChangeToMp3);

    private void Pick(SonosHintChoice choice)
    {
        Choice = choice;
        Close();
    }
}
