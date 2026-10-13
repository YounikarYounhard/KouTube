using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace KouTube;

/// <summary>
/// "Подборка": 1) channels, keywords, freshness; 2) how much space and how to split it between
/// videos and Shorts; 3) the picks as cards — ✕ swaps one for the next best. Then «Скачать».
/// </summary>
public partial class PickWindow : Window
{
    Mount _m;
    int _step = 1;
    Recommend.Selection? _sel;
    List<Recommend.Found> _found = [];
    const long Gb = 1L << 30;

    public PickWindow(Mount m)
    {
        InitializeComponent();
        _m = m;
        var c = Lib.Config;
        (c.MaxAgeDays is <= 14 ? AgeNew : c.MinAgeDays is >= 3 ? AgeSettled : AgeAny).IsChecked = true;
        Target.ItemsSource = Lib.Mounts.Where(x => x.Online).ToList();
        Target.SelectedItem = m;
        Split.Value = m.Storage.Budget.ShortsShare * 100;
        Chips();
        Show(1);
    }

    void Show(int step)
    {
        _step = step;
        P1.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        P2.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        P3.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (t, i) in new[] { (Step1, 1), (Step2, 2), (Step3, 3) }) t.Opacity = i == step ? 1 : 0.4;
        BackBtn.Visibility = step > 1 ? Visibility.Visible : Visibility.Hidden;
        NextBtn.Content = step < 3 ? "Далее" : "Скачать";
        NextBtn.IsEnabled = step != 1 || Lib.Config.Sources.Count + Lib.Config.Keywords.Count + Lib.Config.Wanted.Count > 0 || Lib.Videos().Count > 0;
        if (step == 1) { ImplicitHint(); AgainHint(); }
    }

    // ---------- step 1 ----------

    static Border Pill(string text, Action? remove, Action? click = null)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold });
        if (remove != null)
        {
            var x = new Button { Content = "✕", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(6, 0, 6, 0), MinWidth = 0, MinHeight = 0, FontSize = 11 };
            x.Click += (_, _) => remove();
            sp.Children.Add(x);
        }
        var b = new Border { Child = sp, Background = new SolidColorBrush(Color.FromRgb(0x27, 0x27, 0x27)), CornerRadius = new CornerRadius(16),
            Padding = new Thickness(14, 6, remove != null ? 6 : 14, 6), Margin = new Thickness(0, 0, 8, 8), Cursor = click != null ? Cursors.Hand : null };
        if (click != null) b.MouseLeftButtonUp += (_, _) => click();
        return b;
    }

    void Chips()
    {
        var c = Lib.Config;
        ChannelChips.Children.Clear();
        foreach (var s in c.Sources)
            ChannelChips.Children.Add(Pill(s.Name, () => { Lib.Update(x => x with { Sources = x.Sources.Where(o => o.Url != s.Url).ToList() }); Chips(); }));
        WantedChips.Children.Clear();
        foreach (var w in c.Wanted)
            WantedChips.Children.Add(Pill((w.Kind == "short" ? "Shorts · " : "") + (w.Title.Length > 48 ? w.Title[..47] + "…" : w.Title),
                () => { Lib.Update(x => x with { Wanted = x.Wanted.Where(o => o.Id != w.Id).ToList() }); Chips(); }));
        KeywordChips.Children.Clear();
        foreach (var k in c.Keywords)
            KeywordChips.Children.Add(Pill(k, () => { Lib.Update(x => x with { Keywords = x.Keywords.Where(o => o != k).ToList() }); Chips(); }));
        Suggestions.Children.Clear();
        var it = Recommend.Learn(Lib.Videos());
        foreach (var w in it.Topics.Where(w => !c.Keywords.Contains(w)).Take(14))
            Suggestions.Children.Add(Pill("+ " + w, null, () => AddKeyword(w)));
        SuggestTitle.Visibility = Suggestions.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (IsLoaded) Show(_step);
    }

    /// <summary>«Как в прошлый раз»: the same channels, words, freshness and space, straight to the picks.</summary>
    void AgainHint()
    {
        var c = Lib.Config;
        AgainBox.Visibility = c.LastPickGb is > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (c.LastPickGb is not { } gb) return;
        var what = c.Sources.Select(s => s.Name).Concat(c.Keywords.Select(k => "«" + k + "»")).ToList();
        AgainText.Text = $"Прошлая подборка: {gb:0} ГБ, Shorts {_m.Storage.Budget.ShortsShare:P0}" +
            (what.Count > 0 ? ", " + string.Join(", ", what.Take(6)) + (what.Count > 6 ? $" и ещё {what.Count - 6}" : "") : ", по вашей библиотеке") +
            (c.Wanted.Count > 0 ? $", плюс {c.Wanted.Count} по ссылкам" : "") + ".";
    }

    async void OnAgain(object sender, RoutedEventArgs e)
    {
        SaveAge();
        OnTarget(sender, null!);
        Total.Value = Math.Min(Total.Maximum, Lib.Config.LastPickGb ?? 10);
        await Build();
    }

    void SaveAge() => Lib.Update(c => c with { MinAgeDays = AgeSettled.IsChecked == true ? 3 : null, MaxAgeDays = AgeNew.IsChecked == true ? 14 : null });

    void OnWantedKey(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) OnAddWanted(sender, e); }

    async void OnAddWanted(object sender, RoutedEventArgs e)
    {
        var ids = Recommend.VideoIds(WantedBox.Text).Where(id => Lib.Config.Wanted.All(w => w.Id != id)).ToList();
        if (ids.Count == 0) { WantedStatus.Text = WantedBox.Text.Trim().Length > 0 ? "Не вижу здесь ссылки на видео YouTube" : ""; return; }
        WantedBtn.IsEnabled = false;
        var bad = 0;
        foreach (var (id, i) in ids.Select((x, i) => (x, i)))
        {
            WantedStatus.Text = $"Смотрю ссылку {i + 1} из {ids.Count}…";
            try { var w = await Task.Run(() => Recommend.ProbeWanted(id)); Lib.Update(c => c with { Wanted = [.. c.Wanted, w] }); Chips(); }
            catch (Exception) { bad++; }
        }
        WantedBtn.IsEnabled = true;
        WantedBox.Clear();
        WantedStatus.Text = bad == 0 ? "" : $"Не удалось открыть {bad} — видео удалено, закрыто или нет связи с YouTube";
    }

    /// <summary>When nothing is chosen, say what will be used instead — the library's own favourites.</summary>
    void ImplicitHint()
    {
        var c = Lib.Config;
        if (c.Sources.Count + c.Keywords.Count > 0 || c.LastPickGb != null) { ImplicitText_Box.Visibility = Visibility.Collapsed; return; }
        var imp = Recommend.Implicit(c, Recommend.Learn(Lib.Videos()));
        ImplicitText.Text = imp.Sources.Count + imp.Keywords.Count == 0
            ? "Ничего не выбрано — добавьте хотя бы один канал или слово."
            : "Ничего не выбрано — подберу по вашей библиотеке: " + string.Join(", ", imp.Sources.Select(s => s.Name).Concat(imp.Keywords.Select(k => "«" + k + "»"))) + ".";
        ImplicitText_Box.Visibility = Visibility.Visible;
        NextBtn.IsEnabled = imp.Sources.Count + imp.Keywords.Count > 0;
    }

    async void OnFindChannel(object sender, RoutedEventArgs e)
    {
        var q = ChannelQuery.Text.Trim();
        if (q.Length == 0) return;
        FindBtn.IsEnabled = false;
        ChannelResults.Children.Clear();
        ChannelResults.Children.Add(new TextBlock { Text = "Ищу…", Opacity = 0.7 });
        try { _found = await Task.Run(() => Recommend.FindChannels(q)); }
        catch (Exception ex) { _found = []; ChannelResults.Children.Clear(); ChannelResults.Children.Add(new TextBlock { Text = "Не удалось найти: " + ex.Message }); return; }
        finally { FindBtn.IsEnabled = true; }
        ChannelResults.Children.Clear();
        if (_found.Count == 0) ChannelResults.Children.Add(new TextBlock { Text = "Ничего не нашлось", Opacity = 0.7 });
        foreach (var f in _found)
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4), MaxWidth = 620, HorizontalAlignment = HorizontalAlignment.Left };
            var add = new Button { Content = "Добавить", Padding = new Thickness(14, 4, 14, 4) };
            add.Click += (_, _) =>
            {
                if (!Lib.Config.Sources.Any(s => s.Url == f.Url)) Lib.Update(c => c with { Sources = [.. c.Sources, new Source(f.Url, f.Name, "channel")] });
                ChannelResults.Children.Clear(); ChannelQuery.Clear(); Chips();
            };
            DockPanel.SetDock(add, Dock.Right);
            row.Children.Add(add);
            row.Children.Add(new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0),
                Text = f.Name + (f.Subscribers is { } s ? $"   ·   {Subs(s)} подписчиков" : "") });
            ChannelResults.Children.Add(row);
        }
    }

    static string Subs(long s) => s >= 1_000_000 ? $"{s / 1e6:0.#} млн" : s >= 1000 ? $"{s / 1e3:0} тыс." : s.ToString();

    void OnChannelKey(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) OnFindChannel(sender, e); }
    void OnKeywordKey(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) OnAddKeyword(sender, e); }
    void OnAddKeyword(object sender, RoutedEventArgs e) { AddKeyword(KeywordBox.Text.Trim()); KeywordBox.Clear(); }

    void AddKeyword(string k)
    {
        if (k.Length == 0 || Lib.Config.Keywords.Contains(k)) return;
        Lib.Update(c => c with { Keywords = [.. c.Keywords, k] });
        Chips();
    }

    // ---------- step 2 ----------

    void OnTarget(object sender, SelectionChangedEventArgs e)
    {
        if (Target.SelectedItem is not Mount m) return;
        _m = m;
        var sp = m.Space();
        var free = sp?.Free ?? 0;
        var room = Math.Max(0, free - m.Storage.Budget.ReserveBytes);
        SpaceInfo.Text = sp is { } s ? $"Свободно {Ui.Gb(s.Free)} из {Ui.Gb(s.Total)}. {Ui.Gb(m.Storage.Budget.ReserveBytes)} всегда остаётся для ваших файлов (меняется в «Настройках»)."
                                     : "Носитель не подключён";
        Total.Maximum = Math.Max(1, Math.Floor(room / (double)Gb));
        if (Total.Value == 0 || Total.Value > Total.Maximum) Total.Value = Math.Min(Total.Maximum, Lib.Config.LastPickGb ?? 10);
        OnSliders(sender, e);
    }

    void OnSliders(object sender, RoutedEventArgs e)
    {
        if (TotalText == null || VideoText == null) return;
        Total.Value = Math.Round(Total.Value);
        TotalText.Text = $"{Total.Value:0} ГБ";
        var shorts = Total.Value * Split.Value / 100;
        VideoText.Text = $"{Total.Value - shorts:0.#} ГБ";
        ShortText.Text = $"{shorts:0.#} ГБ";
    }

    // ---------- step 3 ----------

    async Task Build()
    {
        Show(3);
        NextBtn.IsEnabled = false;
        PickProgress.Visibility = Visibility.Visible;
        VideoPicks.ItemsSource = null; ShortPicks.ItemsSource = null;
        VideosHeader.Text = ""; ShortsHeader.Text = "";
        void St(string s) => Ui.OnUi(() => PickStatus.Text = s);
        try
        {
            var it = Recommend.Learn(Lib.Videos());
            var c = Recommend.Implicit(Lib.Config, it);
            var cands = await Task.Run(() => Recommend.Candidates(c, St));
            var skip = Lib.DoNotDownload();
            skip.UnionWith(Lib.Config.Seen);
            foreach (var r in Downloader.Rows) skip.Add(r.Item.Id);
            // links you added: in, unless already downloaded (then they leave the list for good)
            var wanted = Lib.Config.Wanted.Where(w => !skip.Contains(w.Id)).ToList();
            if (wanted.Count != Lib.Config.Wanted.Count) Lib.Update(x => x with { Wanted = Lib.Config.Wanted.Where(w => !Lib.DoNotDownload().Contains(w.Id)).ToList() });
            var ranked = Recommend.Rank(cands, it, skip).Where(p => wanted.All(w => w.Id != p.Id)).ToList();
            var total = (long)(Total.Value * Gb);
            var shorts = (long)(total * Split.Value / 100);
            Lib.Update(x => x with { LastPickGb = Total.Value });
            _sel = new Recommend.Selection(ranked, total - shorts, shorts, wanted.Select(Recommend.ToPick).ToList()).Fill();
            VideoPicks.ItemsSource = _sel.Videos;
            ShortPicks.ItemsSource = _sel.Shorts;
            _sel.Videos.CollectionChanged += (_, _) => Headers();
            _sel.Shorts.CollectionChanged += (_, _) => Headers();
            Headers();
            St(ranked.Count == 0 ? "Подходящих новых видео не нашлось — добавьте каналы или ключевые слова." : "✕ — убрать видео, на его место встанет следующее. Глаз — уже смотрел, больше не предлагать. Ни то ни другое не портит рекомендации.");
        }
        catch (Exception ex) { St("Не удалось подобрать: " + ex.Message); }
        finally { PickProgress.Visibility = Visibility.Collapsed; }
    }

    void Headers()
    {
        if (_sel == null) return;
        VideosHeader.Text = $"Видео · {_sel.Videos.Count} шт. · ≈ {Ui.Gb(_sel.Used(false))} из {Ui.Gb(_sel.VideoBudget)}";
        ShortsHeader.Text = $"Shorts · {_sel.Shorts.Count} шт. · ≈ {Ui.Gb(_sel.Used(true))} из {Ui.Gb(_sel.ShortBudget)}";
        var n = _sel.Videos.Count + _sel.Shorts.Count;
        NextBtn.Content = n == 0 ? "Скачать" : $"Скачать {n} (≈ {Ui.Gb(_sel.Used(false) + _sel.Used(true))})";
        NextBtn.IsEnabled = n > 0;
    }

    void OnRemovePick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not Pick p) return;
        if (p.Source == "ссылка") Lib.Update(c => c with { Wanted = c.Wanted.Where(w => w.Id != p.Id).ToList() });   // you took it back
        _sel?.Remove(p);
    }

    /// <summary>Watched on YouTube already: out of this pick and every later one; likes and dislikes are left alone.</summary>
    void OnSeenPick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not Pick p) return;
        Lib.Update(c => c with { Seen = [.. c.Seen, p.Id] });
        _sel?.Remove(p);
    }

    // ---------- buttons ----------

    async void OnNext(object sender, RoutedEventArgs e)
    {
        switch (_step)
        {
            case 1:
                SaveAge();
                Show(2);
                OnTarget(sender, null!);
                break;
            case 2:
                if (Total.Value <= 0) return;
                _m.Storage = _m.Storage with { UpdatedAt = Format.Now(), Budget = _m.Storage.Budget with { ShortsShare = Split.Value / 100 } };
                try { Format.WriteAtomic(Path.Combine(_m.Ml, "storage.json"), Format.Seal(Format.ToJson(_m.Storage))); } catch (IOException) { }
                await Build();
                break;
            default:
                if (_sel == null) return;
                foreach (var p in _sel.Videos.Concat(_sel.Shorts))
                    Downloader.Rows.Add(new QRow(new QueueItem
                    {
                        Id = p.Id, Url = p.Url, Title = p.Title, Channel = p.Channel, DurationSec = p.DurationSec ?? 0,
                        Published = p.Published?.ToString("yyyy-MM-dd"), Kind = p.Kind, Bytes = p.Estimate, Approx = true,
                        StorageId = _m.Ref.StorageId, Source = "подборка: " + p.Source, Reason = p.Reason, ThumbUrl = p.Thumb,
                    }));
                Downloader.Changed();
                DialogResult = true;
                break;
        }
    }

    void OnBack(object sender, RoutedEventArgs e) { if (_step > 1) Show(_step - 1); }
    void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
