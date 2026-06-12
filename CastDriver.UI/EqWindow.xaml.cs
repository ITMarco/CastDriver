using System.Windows;
using CastDriver.UI.ViewModels;

namespace CastDriver.UI;

public partial class EqWindow : Window
{
    public EqWindow() => InitializeComponent();

    private void OnSavePreset(object sender, RoutedEventArgs e)
    {
        if (DataContext is not EqViewModel vm) return;
        var name = Microsoft.VisualBasic.Interaction.InputBox(
            "Name this preset:", "Save EQ preset", "My preset");
        vm.SaveCurrentAs(name);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
