// InitWindow.xaml.cs

using FlyPhotos.Infra.Configuration;
using FlyPhotos.Infra.Localization;
using FlyPhotos.Infra.Utils;
using FlyPhotos.Services;
using FlyPhotos.Services.Library;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace FlyPhotos.UI.Views;

public sealed partial class InitWindow
{
    private readonly ObservableCollection<LibrarySearchResult> _searchResults = [];
    private Settings? _settingsWindow;
    private int _searchVersion;

    public InitWindow()
    {
        InitializeComponent();

        Util.SetWindowIcon(this);

        // Title property is used only by TaskBar label. Actual TitleBar is customized using AppWindow.TitleBar.
        Title = L.Get("TitleTextBlock/Text").Replace("FlyPhotos - ", string.Empty);

        var titleBar = AppWindow.TitleBar;
        titleBar.ExtendsContentIntoTitleBar = true;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = Colors.Gray;

        ((FrameworkElement)Content).RequestedTheme = AppConfig.Settings.Theme;

        MainLayout.KeyDown += delegate(object _, KeyRoutedEventArgs args)
        {
            if (args.Key == VirtualKey.Escape) Close();
        };

        (AppWindow.Presenter as OverlappedPresenter)?.PreferredMinimumWidth = 400;
        (AppWindow.Presenter as OverlappedPresenter)?.PreferredMinimumHeight = 600;

        SearchResultsListView.ItemsSource = _searchResults;
        SearchRatingComboBox.SelectedIndex = 0;
        LibraryIndexerService.Instance.StatusChanged += LibraryIndexerService_OnStatusChanged;
        Loaded += InitWindow_Loaded;
        Closed += InitWindow_Closed;
    }

    public string SelectedFile { get; private set; }

    private async void OpenFileHyperlink_Click(Hyperlink sender, HyperlinkClickEventArgs args)
    {
        await PickAndProcessFileAsync();
    }

    private void DropArea_DragOver(object sender, DragEventArgs e)
    {
        // Check if the dragged content contains storage items (files)
        // If it's a file, show the "copy" icon.
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
            e.AcceptedOperation = DataPackageOperation.Copy;

    }

    private async void DropArea_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        if (items.Count == 0) return;
        var file = items[0] as StorageFile;
        if (file != null && CodecDiscovery.SupportedExtensions.Contains(file.FileType))
            ProcessSelectedFile(file);
        else
            await ShowMessageDialog(L.Get("UnsupportedFileAlert/Title"), L.Get("UnsupportedFileAlert/Description"));
    }

    private async void InitWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshLibraryDashboardAsync();
        await RunSearchAsync();
    }

    private void InitWindow_Closed(object sender, WindowEventArgs args)
    {
        LibraryIndexerService.Instance.StatusChanged -= LibraryIndexerService_OnStatusChanged;
        if (_settingsWindow != null)
            _settingsWindow.Closed -= SettingsWindow_Closed;
    }

    private void LibraryIndexerService_OnStatusChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() => _ = RefreshLibraryDashboardAsync());
    }

    private async Task RefreshLibraryDashboardAsync()
    {
        var count = await LibraryCatalogService.Instance.GetIndexedAssetCountAsync();
        var status = LibraryIndexerService.Instance.GetStatusSnapshot();

        IndexedPhotoCountTextBlock.Text = count.ToString(CultureInfo.InvariantCulture);
        IndexingStatusTextBlock.Text = status.ErrorMessage == null
            ? status.StatusText
            : $"{status.StatusText} ({status.ErrorMessage})";
        WatchedFoldersTextBlock.Text = AppConfig.Settings.WatchedFolders.Count == 0
            ? "No watched folders configured."
            : string.Join(Environment.NewLine, AppConfig.Settings.WatchedFolders);
    }

    private async void SearchTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        await RunSearchAsync();
    }

    private async void SearchDateTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        await RunSearchAsync();
    }

    private async void SearchRatingComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        await RunSearchAsync();
    }

    private async Task RunSearchAsync()
    {
        var currentVersion = Interlocked.Increment(ref _searchVersion);
        var parsedDate = ParseDateFilter(SearchDateTextBox.Text);
        var minimumRating = TryGetMinimumRating();
        var results = await LibraryCatalogService.Instance.SearchAsync(new LibrarySearchQuery(
            SearchTextBox.Text,
            parsedDate,
            minimumRating));

        if (currentVersion != _searchVersion)
            return;

        _searchResults.Clear();
        foreach (var result in results)
            _searchResults.Add(result);
    }

    private async void SearchResultsListView_OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not LibrarySearchResult result)
            return;

        if (!File.Exists(result.FilePath))
        {
            await ShowMessageDialog("File not found", "The selected catalog entry no longer exists on disk.");
            await RefreshLibraryDashboardAsync();
            await RunSearchAsync();
            return;
        }

        SelectedFile = result.FilePath;
        Close();
    }

    private void ButtonOpenSettings_OnClick(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow == null)
        {
            _settingsWindow = new Settings();
            _settingsWindow.SetWindowSize(900, 768);
            _settingsWindow.CenterOnScreen();
            _settingsWindow.Closed += SettingsWindow_Closed;
            _settingsWindow.Activate();
        }
        else
        {
            _settingsWindow.Activate();
        }
    }

    private void SettingsWindow_Closed(object sender, WindowEventArgs args)
    {
        if (_settingsWindow != null)
            _settingsWindow.Closed -= SettingsWindow_Closed;

        _settingsWindow = null;
        DispatcherQueue.TryEnqueue(() =>
        {
            _ = RefreshLibraryDashboardAsync();
            _ = RunSearchAsync();
        });
    }


    // Shared logic for opening the file picker
    private async Task PickAndProcessFileAsync()
    {
        var filePicker = new FileOpenPicker();

        var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(filePicker, windowHandle);

        filePicker.ViewMode = PickerViewMode.Thumbnail;
        filePicker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;

        foreach (var ext in CodecDiscovery.SupportedExtensions)
            filePicker.FileTypeFilter.Add(ext);


        StorageFile file = await filePicker.PickSingleFileAsync();
        if (file != null)
            ProcessSelectedFile(file);

    }

    private void ProcessSelectedFile(StorageFile file)
    {
        SelectedFile = file.Path;
        Close();
    }

    private async Task ShowMessageDialog(string title, string content)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            CloseButtonText = L.Get("MessageClose_Ok"),
            XamlRoot = Content.XamlRoot
        };
        await dialog.ShowAsync();
    }

    private DateTimeOffset? ParseDateFilter(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return null;

        return DateTimeOffset.TryParse(rawText, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var parsed)
            ? new DateTimeOffset(parsed.Year, parsed.Month, parsed.Day, 0, 0, 0, TimeSpan.Zero)
            : null;
    }

    private int? TryGetMinimumRating()
    {
        if (SearchRatingComboBox.SelectedItem is not ComboBoxItem item)
            return null;

        var tag = item.Tag?.ToString();
        return int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rating)
            ? rating
            : null;
    }
}