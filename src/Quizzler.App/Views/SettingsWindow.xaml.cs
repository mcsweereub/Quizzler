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

    /// <summary>PasswordBox.Password can't be bound (by design), so pass it to the view model here.</summary>
    private void OnApiKeyChanged(object sender, RoutedEventArgs e) => _viewModel.NewApiKey = ApiKeyBox.Password;

    /// <summary>Closing with the X behaves like Cancel (reverts a previewed theme).</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_closingFromViewModel) _viewModel.RevertPreview();
        base.OnClosing(e);
    }
}
