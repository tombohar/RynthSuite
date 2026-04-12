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

        Closing += async (_, e) =>
        {
            if (_forceClose || !_vm.IsDirty) return;
            e.Cancel = true;
            var dlg = new ConfirmDialog("Unsaved changes will be lost. Continue?");
            var result = await dlg.ShowDialog<bool>(this);
            if (result) { _forceClose = true; Close(); }
        };
    }
}
