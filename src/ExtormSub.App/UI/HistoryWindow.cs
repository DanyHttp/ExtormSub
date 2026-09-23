using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using ExtormSub.App.Infrastructure;
using ExtormSub.Core.History;
using ExtormSub.Core.Subtitles;
using Microsoft.Extensions.Logging;

namespace ExtormSub.App.UI;

public partial class HistoryWindow : Window
{
    public HistoryWindow(HistoryViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        SourceInitialized += (_, _) => Native.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
        Loaded += async (_, _) => await vm.LoadAsync();
        // Ctrl+C copies the selected lines.
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, _) =>
            vm.CopyLines(SegmentList.SelectedItems.Cast<SegmentItem>().OrderBy(s => s.Record.Start).ToList())));
    }
}

public sealed record SessionItem(SessionRecord Record)
{
    public string Title => Record.StartedAt.LocalDateTime.ToString("ddd d MMM · HH:mm", CultureInfo.CurrentCulture);
    public string CountText => $"{Record.SegmentCount} lines";
    public string Preview => Record.Preview ?? "";
}

public sealed record SegmentItem(SegmentRecord Record)
{
    public string Time => Record.Start.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
    public string Original => Record.OriginalText;
    public string Translation => Record.TranslatedText ?? "";
    public bool HasTranslation => !string.IsNullOrEmpty(Record.TranslatedText);
    public string Meta
    {
        get
        {
            var parts = new List<string>();
            if (Record.AsrLatencyMs is { } a) parts.Add($"ASR {a} ms");
            if (Record.TranslationLatencyMs is { } t) parts.Add($"translation {t} ms");
            if (Record.Confidence is { } c) parts.Add($"confidence {c:P0}");
            if (Record.DetectedLanguage is { } l) parts.Add(l);
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>Browse, search, copy, export and delete saved sessions.</summary>
public sealed class HistoryViewModel : ObservableObject
{
    private readonly IHistoryStore _store;
    private readonly ILogger _log;
    private string _search = "";
    private SessionItem? _selected;
    private ExportContent _exportContent = ExportContent.Both;
    private int _loadVersion;

    public HistoryViewModel(IHistoryStore store, ILogger<HistoryViewModel> log)
    {
        _store = store;
        _log = log;
        Copy = new RelayCommand(() => CopyLines(Segments.ToList()), _ => Segments.Count > 0);
        Export = new RelayCommand(p => ExportSession(p as string ?? "srt"), _ => Segments.Count > 0);
        DeleteSession = new RelayCommand(async () => await DeleteAsync(), _ => _selected is not null);
        ClearAll = new RelayCommand(async () => await ClearAsync());
    }

    public static ExportContent[] ContentOptions { get; } = Enum.GetValues<ExportContent>();

    public ObservableCollection<SessionItem> Sessions { get; } = [];
    public ObservableCollection<SegmentItem> Segments { get; } = [];

    public ICommand Copy { get; }
    public ICommand Export { get; }
    public ICommand DeleteSession { get; }
    public ICommand ClearAll { get; }

    public string Search
    {
        get => _search;
        set { if (Set(ref _search, value)) _ = LoadAsync(); }
    }

    public ExportContent ExportContent { get => _exportContent; set => Set(ref _exportContent, value); }

    public SessionItem? SelectedSession
    {
        get => _selected;
        set { if (Set(ref _selected, value)) _ = LoadSegmentsAsync(); }
    }

    public string EmptyText => Sessions.Count > 0 ? "" : string.IsNullOrWhiteSpace(Search) ? "No sessions yet.\nStart listening to create one." : "Nothing matches.";
    public string Header => _selected?.Title ?? "Transcript history";
    public string SubHeader => _selected is null ? "Select a session" :
        $"{_selected.Record.SegmentCount} lines · {_selected.Record.Device} · {_selected.Record.AsrModel}" +
        (_selected.Record.TranslationModel is { } t ? $" · {t}" : "");

    public async Task LoadAsync()
    {
        int version = ++_loadVersion;
        try
        {
            var sessions = await _store.ListSessionsAsync(Search);
            if (version != _loadVersion) return; // a newer search superseded this one
            var keep = _selected?.Record.Id;
            Sessions.Clear();
            foreach (var s in sessions) Sessions.Add(new SessionItem(s));
            SelectedSession = Sessions.FirstOrDefault(s => s.Record.Id == keep) ?? Sessions.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Loading history failed");
        }
        Raise(nameof(EmptyText));
    }

    private async Task LoadSegmentsAsync()
    {
        Segments.Clear();
        Raise(nameof(Header));
        Raise(nameof(SubHeader));
        if (_selected is null) return;
        var id = _selected.Record.Id;
        var segments = await _store.GetSegmentsAsync(id);
        if (_selected?.Record.Id != id) return;
        foreach (var s in segments) Segments.Add(new SegmentItem(s));
        CommandManager.InvalidateRequerySuggested();
    }

    public void CopyLines(IReadOnlyList<SegmentItem> items)
    {
        if (items.Count == 0) return;
        var text = SubtitleExporter.ToText(items.Select(ToLine), ExportContent);
        Clipboard.SetText(text);
    }

    private void ExportSession(string format)
    {
        if (_selected is null) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"ExtormSub {_selected.Record.StartedAt.LocalDateTime:yyyy-MM-dd HHmm}.{format}",
            Filter = format switch
            {
                "txt" => "Text file|*.txt",
                "vtt" => "WebVTT subtitles|*.vtt",
                _ => "SubRip subtitles|*.srt",
            },
        };
        if (dialog.ShowDialog() != true) return;
        var lines = Segments.Select(ToLine).ToList();
        var content = format switch
        {
            "txt" => SubtitleExporter.ToText(lines, ExportContent),
            "vtt" => SubtitleExporter.ToVtt(lines, ExportContent),
            _ => SubtitleExporter.ToSrt(lines, ExportContent),
        };
        try
        {
            // UTF-8 with BOM: players and editors detect Persian text reliably.
            File.WriteAllText(dialog.FileName, content, new UTF8Encoding(true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"Could not save the file:\n{ex.Message}", "ExtormSub", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task DeleteAsync()
    {
        if (_selected is null) return;
        if (MessageBox.Show("Delete this session?", "ExtormSub", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        await _store.DeleteSessionAsync(_selected.Record.Id);
        _selected = null;
        await LoadAsync();
    }

    private async Task ClearAsync()
    {
        if (MessageBox.Show("Delete all saved subtitle history? This cannot be undone.", "ExtormSub",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        await _store.ClearAsync();
        _selected = null;
        await LoadAsync();
    }

    private static SubtitleLine ToLine(SegmentItem s) => new(s.Record.Start, s.Record.End, s.Record.OriginalText, s.Record.TranslatedText);
}
