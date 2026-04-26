using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RynthCore.LootEditor;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _forceClose;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel(this);
        DataContext = _vm;

        // (Confirm-on-close was disabled when the dirty-flag was made private during
        // the VTank model migration. Re-add via a public IsDirty later if desired.)
        _ = _forceClose; // silence unused-field warning
    }
}
