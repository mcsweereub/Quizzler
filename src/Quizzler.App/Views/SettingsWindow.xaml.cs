using System.ComponentModel;
using System.Windows;
using Quizzler.App.ViewModels;

namespace Quizzler.App.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;
    private bool _closingFromViewModel;

    internal SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) =>
        {
            _closingFromViewModel = true;
            Close();
        };
    }

    /// <summary>Closing with the X behaves like Cancel (reverts a previewed theme).</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_closingFromViewModel) _viewModel.RevertPreview();
        base.OnClosing(e);
    }
}
