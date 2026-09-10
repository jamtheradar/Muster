using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Muster.App.Diagnostics;
using Muster.App.ViewModels;
using Muster.Core.Diagnostics;

namespace Muster.App.Views;

/// <summary>
/// The settings screen. Every option in muster.json, edited through the same store the loader
/// uses, so the file stays hand-editable and the two shapes cannot drift.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;
    private bool _closeConfirmed;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;

        Loaded += OnLoaded;
    }

    /// <summary>
    /// Closes without the discard prompt. The shell calls this when the app is quitting: a modal
    /// question nobody asked for is not something to put in the way of Quit.
    /// </summary>
    public void ForceClose()
    {
        _closeConfirmed = true;
        Close();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await _viewModel.LoadAsync().ConfigureAwait(true);
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (!await _viewModel.SaveAsync().ConfigureAwait(true))
        {
            return;
        }

        if (!_viewModel.RestartRequired)
        {
            return;
        }

        // Config hot-reload is not built, so a save that changes anything structural does nothing
        // visible until relaunch. Saying so here is the difference between "it saved" and "it
        // worked".
        MessageBox.Show(
            this,
            "Saved.\n\nWorkspaces, services, presence and the log folder are read at startup, so "
            + "restart Muster to pick them up. The log level is already live.",
            "Muster · settings saved",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private async void OnReloadClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.HasUnsavedChanges && !ConfirmDiscard("Reload from disk"))
        {
            return;
        }

        await _viewModel.LoadAsync().ConfigureAwait(true);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>Double-click a log file to open it. The row is already selected by then.</summary>
    private void OnLogFileActivated(object sender, MouseButtonEventArgs e) => OpenSelectedLog();

    private void OnLogFileKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            OpenSelectedLog();
        }
    }

    private void OpenSelectedLog()
    {
        if (_viewModel.OpenSelectedLogCommand.CanExecute(null))
        {
            _viewModel.OpenSelectedLogCommand.Execute(null);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Closing is the easy way out of a form this large, and everything in it is thrown away
        // unsaved. Ask once rather than losing a workspace someone just typed in.
        if (!_closeConfirmed && _viewModel.HasUnsavedChanges && !ConfirmDiscard("Close settings"))
        {
            e.Cancel = true;
            return;
        }

        _closeConfirmed = true;
        base.OnClosing(e);
    }

    private bool ConfirmDiscard(string caption)
        => MessageBox.Show(
            this,
            "There are changes here that have not been written to muster.json. Discard them?",
            $"Muster · {caption}",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;
}
