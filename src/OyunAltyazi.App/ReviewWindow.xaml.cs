using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;
using OyunAltyazi.Translation;

namespace OyunAltyazi.App;

public partial class ReviewWindow : Window
{
    private readonly SqliteTranslationCache _cache;
    private readonly string _game;
    private IReadOnlyList<ReviewRow> _rows = [];
    private ReviewRow? _selected;
    private bool _refreshing;

    public ReviewWindow(SqliteTranslationCache cache, string game)
    {
        _cache = cache;
        _game = game;
        InitializeComponent();
        GameLabel.Text = game;
        StatusLabel.Text = "Yerel inceleme · Hiçbir veri gönderilmez.";
        Reload();
    }

    private void Reload(string? select = null)
    {
        _rows = _cache.ReviewRows(_game);
        SetList(select);
    }

    private void SetList(string? select)
    {
        var approved = _rows.Count(r => r.Approved is not null);
        CountLabel.Text = $"{_rows.Count} replik  ·  {approved} onaylı";
        var visible = _rows.Where(r => ApprovedOnly.IsChecked != true || r.Approved is not null).ToArray();
        _refreshing = true;
        try
        {
            Rows.ItemsSource = visible;
            Rows.SelectedItem = visible.FirstOrDefault(r => r.Original == select) ?? visible.FirstOrDefault();
        }
        finally { _refreshing = false; }
        FillEditor(Rows.SelectedItem as ReviewRow);
        EmptyLabel.Text = visible.Length > 0 ? "" : ApprovedOnly.IsChecked == true
            ? "Henüz onaylı replik yok." : "Henüz kayıt yok. Oyunda birkaç replik çevrildikten sonra tekrar aç.";
        ExportButton.IsEnabled = approved > 0;
    }

    private void FillEditor(ReviewRow? row)
    {
        _selected = row;
        Editor.IsEnabled = row is not null;
        SourceBox.Text = row?.CorrectedSource ?? row?.Original ?? "";
        TurkishBox.Text = row?.Display ?? "";
        UndoApproval.IsEnabled = row?.Approved is not null;
    }

    private bool HasEdits => _selected is { } row && (SourceBox.Text != (row.CorrectedSource ?? row.Original) || TurkishBox.Text != row.Display);
    private bool CanDiscard() => !HasEdits || MessageBox.Show(this, "Kaydedilmemiş düzenlemeler silinsin mi?", "Paket atölyesi",
        MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    private void FilterChanged(object sender, RoutedEventArgs e)
    {
        if (Rows is null || _refreshing) return;
        if (!CanDiscard())
        {
            _refreshing = true;
            ApprovedOnly.IsChecked = !(ApprovedOnly.IsChecked == true);
            _refreshing = false;
            return;
        }
        SetList(_selected?.Original);
    }

    private void RowChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing) return;
        if (CanDiscard()) FillEditor(Rows.SelectedItem as ReviewRow);
        else
        {
            _refreshing = true;
            Rows.SelectedItem = _selected;
            _refreshing = false;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!CanDiscard()) e.Cancel = true;
        base.OnClosing(e);
    }

    private void ApproveClick(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } row) return;
        try
        {
            _cache.SaveReviewed(_game, row.Original, TurkishBox.Text, SourceBox.Text);
            Reload(row.Original);
            StatusLabel.Text = "Replik onaylandı. JSON paketine eklenecek.";
        }
        catch (ArgumentException ex) { StatusLabel.Text = ex.Message; }
        catch (SqliteException) { StatusLabel.Text = "Satır kaydedilemedi. Tekrar dene."; }
    }

    private void UndoClick(object sender, RoutedEventArgs e)
    {
        if (_selected is not { Approved: not null } row || !CanDiscard()) return;
        try
        {
            _cache.RemoveReviewed(_game, row.Original);
            Reload(row.Original);
            StatusLabel.Text = "Onay kaldırıldı.";
        }
        catch (SqliteException) { StatusLabel.Text = "Onay kaldırılamadı. Tekrar dene."; }
    }

    private void ExportClick(object sender, RoutedEventArgs e)
    {
        if (HasEdits) { StatusLabel.Text = "Düzenlediğin satırı önce onayla veya düzenlemeyi iptal et."; return; }
        try
        {
            if (_cache.ExportReviewedPack(_game) is not { } json) return;
            var dialog = new SaveFileDialog { Filter = "Oyun paketi (*.json)|*.json", DefaultExt = ".json",
                FileName = Path.GetFileNameWithoutExtension(_game) + "-altyazi.json" };
            if (dialog.ShowDialog(this) != true) return;
            File.WriteAllText(dialog.FileName, json);
            StatusLabel.Text = "Paket kaydedildi. Kullanmak için Çeviri → Oyun paketi seç.";
        }
        catch (InvalidOperationException ex) { StatusLabel.Text = ex.Message; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        { StatusLabel.Text = "Paket kaydedilemedi. Başka bir konum seçip tekrar dene."; }
    }
}
