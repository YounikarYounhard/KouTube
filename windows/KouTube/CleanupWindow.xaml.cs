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
        bool _delete = !Lib.InPlaylist(v);
        bool _canDelete = !Lib.InPlaylist(v);
        public event PropertyChangedEventHandler? PropertyChanged;
        public Video V { get; } = v;
        public bool CanDelete
        {
            get => _canDelete;
            set
            {
                _canDelete = value;
                if (!value) Delete = false;
                PropertyChanged?.Invoke(this, new(nameof(CanDelete)));
            }
        }
        public bool Delete { get => _delete; set { _delete = value; PropertyChanged?.Invoke(this, new(nameof(Delete))); PropertyChanged?.Invoke(this, new(nameof(CoverOpacity))); Changed?.Invoke(); } }
        public double CoverOpacity => _delete ? 1 : 0.4;
        public string Title => V.Title;
        public string Channel => string.Join(" · ", new[] { V.Meta.Channel, Lib.InPlaylist(V) ? "В плейлисте" : null,
            Lib.StaleUnwatched(V, Format.Now(), true) ? "не смотрели 30+ дней" : null }.Where(s => !string.IsNullOrEmpty(s)));
        public string Size => Ui.Gb(V.Found?.Size ?? V.Meta.Size ?? 0);
        public object? Cover => new MainWindow.LibRow(V).Cover;
        public Action? Changed;
    }

    /// <summary>What the user chose: 0 later, 1 delete, 2 delete and pick new ones.</summary>
    public int Choice { get; private set; }
    public bool PlaylistFilesAllowed => IncludePlaylists.IsChecked == true;
    public List<Video> ToDelete => _rows.Where(r => r.Delete).Select(r => r.V).ToList();
    readonly List<Row> _rows;
    readonly Mount _m;
    readonly bool _stale;

    public CleanupWindow(Mount m, List<Video> watched, bool stale = false)
    {
        InitializeComponent();
        _m = m; _stale = stale;
        if (stale) { Title = "Обновить контент"; Hint.Text = "Не просмотрено ни разу по синхронизированной истории, добавлено 30+ дней назад. Сначала подключите носители всех устройств, чтобы история была актуальной. Снимите галочки с нужных видео. Удаление только после нажатия кнопки ниже."; }
        _rows = watched.Select(v => new Row(v)).ToList();
        IncludePlaylists.Visibility = watched.Any(Lib.InPlaylist) ? Visibility.Visible : Visibility.Collapsed;
        foreach (var r in _rows) r.Changed = Update;
        List.ItemsSource = _rows;
        Update();
    }

    void OnPlaylistOption(object sender, RoutedEventArgs e)
    {
        if (_rows == null) return;
        foreach (var row in _rows.Where(r => Lib.InPlaylist(r.V))) row.CanDelete = PlaylistFilesAllowed;
        Update();
    }

    void Update()
    {
        var sel = _rows.Where(r => r.Delete).ToList();
        var bytes = sel.Sum(r => r.V.Found?.Size ?? 0);
        Header.Text = $"На «{_m.Name}» {(_stale ? "старых непросмотренных" : "просмотрено")} {_rows.Count} — {Ui.Gb(_rows.Sum(r => r.V.Found?.Size ?? 0))}";
        DeleteAndPickText.Text = sel.Count == 0 ? "Удалить и подобрать новые" : $"Удалить {sel.Count} ({Ui.Gb(bytes)}) и подобрать новые";
        DeleteAndPick.IsEnabled = OnlyDelete.IsEnabled = sel.Count > 0;
    }

    void OnLater(object sender, RoutedEventArgs e) { Choice = 0; DialogResult = false; }
    void OnDelete(object sender, RoutedEventArgs e) { Choice = 1; DialogResult = true; }
    void OnDeleteAndPick(object sender, RoutedEventArgs e) { Choice = 2; DialogResult = true; }
}
