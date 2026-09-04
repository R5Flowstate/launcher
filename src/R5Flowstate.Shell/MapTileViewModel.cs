using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace R5Flowstate.Shell;

/// <summary>One map in the art picker. Art arrives later, off the UI thread.</summary>
public sealed class MapTileViewModel : INotifyPropertyChanged
{
    private ImageSource? _art;
    private bool _isSelected;

    public MapTileViewModel(MapOption option)
    {
        Option = option;
    }

    public MapOption Option { get; }
    public string Stem => Option.Stem;
    public string DisplayName => Option.DisplayName;

    /// <summary>Set only for the lobby, whose art is a pick rather than its own pak.</summary>
    public string? ArtPakPath { get; set; }

    public ImageSource? Art
    {
        get => _art;
        set
        {
            if (ReferenceEquals(_art, value))
                return;
            _art = value;
            OnPropertyChanged();
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
