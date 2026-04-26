using RynthCore.Loot;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RynthCore.LootEditor;

/// <summary>One row in the per-material salvage combine table.</summary>
public sealed class SalvagePerMaterialRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    private int _materialId;
    public int MaterialId
    {
        get => _materialId;
        set
        {
            if (_materialId == value) return;
            _materialId = value;
            _materialName = MaterialTypes.Name(value);
            Notify();
            Notify(nameof(MaterialName));
        }
    }

    private string _materialName;
    public string MaterialName
    {
        get => _materialName;
        private set { _materialName = value; Notify(); }
    }

    private string _bandsText;
    public string BandsText
    {
        get => _bandsText;
        set { if (_bandsText == value) return; _bandsText = value; Notify(); }
    }

    public SalvagePerMaterialRow(int materialId, string materialName, string bandsText)
    {
        _materialId   = materialId;
        _materialName = materialName;
        _bandsText    = bandsText;
    }
}

/// <summary>(id, "id - name") pair shown in the material picker ComboBox.</summary>
public sealed record MaterialOption(int Id, string Display);
