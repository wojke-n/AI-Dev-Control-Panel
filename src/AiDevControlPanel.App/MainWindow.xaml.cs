using System.ComponentModel;
using System.Windows;
using AiDevControlPanel.App.ViewModels;

namespace AiDevControlPanel.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _closingConfirmed;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_closingConfirmed)
        {
            base.OnClosing(e);
            return;
        }

        if (_viewModel.HasRunningJobs &&
            MessageBox.Show(
                "Claude is still working. Stop the running jobs and exit? They continue when the app starts again.",
                "AI-Dev-Control-Panel",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }

        // Stop the scheduler (and kill Claude processes) before the window goes away.
        e.Cancel = true;
        IsEnabled = false;
        await _viewModel.StopJobsAsync();
        _closingConfirmed = true;
        Close();
    }
}
