using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RynthCore.LootEditor;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _forceClose;

    public MainWindow() : this(null) { }

    /// <param name="openPath">A profile to open at startup (from the command line), or null.</param>
    public MainWindow(string? openPath)
    {
        InitializeComponent();
        _vm = new MainViewModel(this);
        DataContext = _vm;
        if (!string.IsNullOrWhiteSpace(openPath))
            _vm.OpenFromCommandLine(openPath);

        // (Confirm-on-close was disabled when the dirty-flag was made private during
        // the VTank model migration. Re-add via a public IsDirty later if desired.)
        _ = _forceClose; // silence unused-field warning
    }
}
