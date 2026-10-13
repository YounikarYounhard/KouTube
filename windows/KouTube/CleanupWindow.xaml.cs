using System.ComponentModel;
using System.Windows;

namespace KouTube;

/// <summary>
/// Plugged in after watching: "these are watched — remove them and get new ones?". The phone never
/// deletes on its own; this is the one place files go away, with every video's own tick.
/// </summary>
public partial class CleanupWindow : Window
{
    public sealed class Row(Video v) : INotifyPropertyChanged
    {
        bool _delete = true;
        public event PropertyChangedEventHandler? PropertyChanged;
        public Video V { get; } = v;
        public bool Delete { get => _delete; set { _delete = value; PropertyChanged?.Invoke(this, new(nameof(Delete))); PropertyChanged?.Invoke(this, new(nameof(CoverOpacity))); Changed?.Invoke(); } }
        public double CoverOpacity => _delete ? 1 : 0.4;
        public string Title => V.Title;
        public string Channel => V.Meta.Channel ?? "";
        public string Size => Ui.Gb(V.Found?.Size ?? V.Meta.Size ?? 0);
        public object? Cover => new MainWindow.LibRow(V).Cover;
        public Action? Changed;
    }

    /// <summary>What the user chose: 0 later, 1 delete, 2 delete and pick new ones.</summary>
    public int Choice { get; private set; }
    public List<Video> ToDelete => _rows.Where(r => r.Delete).Select(r => r.V).ToList();
    readonly List<Row> _rows;
    readonly Mount _m;

    public CleanupWindow(Mount m, List<Video> watched)
    {
        InitializeComponent();
        _m = m;
        _rows = watched.Select(v => new Row(v)).ToList();
        foreach (var r in _rows) r.Changed = Update;
        List.ItemsSource = _rows;
        Update();
    }

    void Update()
    {
        var sel = _rows.Where(r => r.Delete).ToList();
        var bytes = sel.Sum(r => r.V.Found?.Size ?? 0);
        Header.Text = $"На «{_m.Name}» просмотрено {_rows.Count} — {Ui.Gb(_rows.Sum(r => r.V.Found?.Size ?? 0))}";
        DeleteAndPickText.Text = sel.Count == 0 ? "Удалить и подобрать новые" : $"Удалить {sel.Count} ({Ui.Gb(bytes)}) и подобрать новые";
        DeleteAndPick.IsEnabled = OnlyDelete.IsEnabled = sel.Count > 0;
    }

    void OnLater(object sender, RoutedEventArgs e) { Choice = 0; DialogResult = false; }
    void OnDelete(object sender, RoutedEventArgs e) { Choice = 1; DialogResult = true; }
    void OnDeleteAndPick(object sender, RoutedEventArgs e) { Choice = 2; DialogResult = true; }
}
