using System.Windows;
using System.Windows.Input;
using Quizzler.App.ViewModels;

namespace Quizzler.App.Views;

public partial class StatsWindow : Window
{
    public StatsWindow(StatsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        // Re-read when brought back to the front, so it's current after a quiz session.
        Activated += (_, _) => viewModel.Refresh();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
        base.OnKeyDown(e);
    }
}
