using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Interop;
using Forms = System.Windows.Forms;
using PromptFlow.Models;
using PromptFlow.Services;
using System.IO;
using System.Runtime.InteropServices;
using WpfButton = System.Windows.Controls.Button;
using WpfListBox = System.Windows.Controls.ListBox;
using WpfDragEventArgs = System.Windows.DragEventArgs;
using WpfGiveFeedbackEventHandler = System.Windows.GiveFeedbackEventHandler;
using WpfMessageBox = System.Windows.MessageBox;
using WpfPoint = System.Windows.Point;

namespace PromptFlow;

public partial class MainWindow : Window
{
    private readonly SettingsService _settings = new();
    private StorageRepository _repository;
    private ClipboardMonitor? _monitor;
    private NativeHotkeyService? _hotkey;
    private TrayService? _tray;
    private readonly ActionBarWindow _actionBar;
    private readonly ObservableCollection<ClipboardItem> _items = new();
    private readonly ObservableCollection<ClipboardItem> _favoriteItems = new();
    private readonly ObservableCollection<Folder> _folders = new();
    private bool _favoritesMode;
    private bool _directFolderMode;
    private bool _suppressFolderSelection;
    private bool _contextMenuOpen;
    private ContextMenu? _activeContextMenu;
    private long _selectedFolder;
    private WpfPoint _dragStart;
    private IntPtr _targetWindow;
    private IntPtr _targetFocusWindow;
    private long _draggedFolder;
    private bool _suppressNextItemClick;
    private bool _suppressNextFolderClick;
    private Border? _dragHighlight;
    private System.Windows.Controls.Primitives.Popup? _dragPreviewPopup;
    private IntPtr _outsideMouseHook;
    private LowLevelMouseProc? _outsideMouseProc;
    private long _outsideClickEnabledAt;

    public MainWindow()
    {
        InitializeComponent();
        _actionBar = new ActionBarWindow(this);
        _actionBar.SourceInitialized += (_, _) => ConfigureAsNonActivatingWindow(_actionBar);
        _repository = new StorageRepository(_settings.Current.DataDirectory);
        ItemList.ItemsSource = _items; FavoriteItemList.ItemsSource = _favoriteItems; FolderList.ItemsSource = _folders;
        SourceInitialized += (_, _) => InitializeNativeServices();
        PreviewMouseRightButtonDown += MainWindow_PreviewMouseRightButtonDown;
        ItemList.PreviewMouseLeftButtonDown += (_, e) => _dragStart = e.GetPosition(ItemList);
        ItemList.PreviewMouseMove += ItemList_PreviewMouseMove;
        FavoriteItemList.PreviewMouseLeftButtonDown += (_, e) => _dragStart = e.GetPosition(FavoriteItemList);
        FavoriteItemList.PreviewMouseMove += FavoriteItemList_PreviewMouseMove;
        FolderList.PreviewMouseLeftButtonDown += (_, e) => _dragStart = e.GetPosition(FolderList);
        FolderList.PreviewMouseMove += FolderList_PreviewMouseMove;
    }

    private void InitializeNativeServices()
    {
        ConfigureAsNonActivatingWindow(this);
        _outsideMouseProc = OutsideMouseCallback;
        _outsideMouseHook = SetWindowsHookEx(WhMouseLl, _outsideMouseProc, GetModuleHandle(null), 0);
        StartMonitor();
        _hotkey = new NativeHotkeyService(this); _hotkey.Triggered += (_, _) => Dispatcher.Invoke(TogglePopup); _hotkey.RegistrationFailed += (_, text) => StatusText.Text = text; _hotkey.Register(_settings.Current.Hotkey);
        _tray = new TrayService { MonitorEnabled = _settings.Current.MonitorEnabled }; _tray.OpenRequested += (_, _) => ShowPopup(); _tray.SettingsRequested += (_, _) => ShowSettings(); _tray.ToggleMonitorRequested += (_, _) => ToggleMonitor(); _tray.ExitRequested += (_, _) => System.Windows.Application.Current.Shutdown();
    }

    private void StartMonitor()
    {
        _monitor = new ClipboardMonitor(_repository, _settings);
        _monitor.ItemCaptured += (_, _) => Dispatcher.Invoke(RefreshItems);
        _monitor.Notice += (_, text) => StatusText.Text = text;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e) { RefreshItems(); LoadSettingsFields(); if (Environment.GetCommandLineArgs().Contains("--background")) Hide(); else ShowPopup(); }
    private void TogglePopup() { if (IsVisible || _actionBar.IsVisible) HideMenus(); else ShowPopup(); }
    public void ShowPopup()
    {
        RememberTargetWindow();
        var cursor = Forms.Cursor.Position;
        var work = Forms.Screen.FromPoint(cursor).WorkingArea;
        var fromDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice;
        var scaleX = fromDevice?.M11 ?? 1d;
        var scaleY = fromDevice?.M22 ?? 1d;
        var point = new WpfPoint(cursor.X * scaleX, cursor.Y * scaleY);
        var workLeft = work.Left * scaleX;
        var workTop = work.Top * scaleY;
        var workRight = work.Right * scaleX;
        var workBottom = work.Bottom * scaleY;
        var left = point.X + 16 * scaleX;
        if (left + Width > workRight - 8 * scaleX) left = point.X - Width - 16 * scaleX;
        left = Math.Min(Math.Max(left, workLeft + 8 * scaleX), workRight - Width - 8 * scaleX);

        var barHeight = _actionBar.Height;
        var barTop = point.Y - barHeight * 0.68;
        barTop = Math.Min(Math.Max(barTop, workTop + 8 * scaleY), workBottom - barHeight - 8 * scaleY);
        var menuTop = barTop - Height - 10 * scaleY;
        if (menuTop < workTop + 8 * scaleY)
            menuTop = Math.Min(barTop + barHeight + 10 * scaleY, workBottom - Height - 8 * scaleY);

        Left = left;
        Top = menuTop;
        _actionBar.Left = left;
        _actionBar.Top = barTop;
        Show();
        _actionBar.Show();
        KeepWindowNonActivating(this);
        KeepWindowNonActivating(_actionBar);
        // Starting from a taskbar/tray click can leave one mouse message in the
        // low-level hook queue. Do not mistake that initiating click for an
        // outside click and immediately dismiss the menus.
        _outsideClickEnabledAt = Environment.TickCount64 + 350;
    }
    // The popup windows deliberately use WS_EX_NOACTIVATE so the editor keeps
    // focus. Their Deactivated event therefore fires during normal display and
    // cannot be used as an outside-click signal; OutsideMouseCallback owns that
    // responsibility instead.
    internal void HandleMenuDeactivated() { }
    private void HideMenus()
    {
        _activeContextMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
        _activeContextMenu = null;
        _contextMenuOpen = false;
        Hide();
        _actionBar.Hide();
    }

    private void OpenContextMenu(ContextMenu menu)
    {
        _activeContextMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
        _activeContextMenu = menu;
        _contextMenuOpen = true;
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_activeContextMenu, menu))
            {
                _activeContextMenu = null;
                _contextMenuOpen = false;
            }
        };
        menu.IsOpen = true;
    }

    private void CloseContextMenuOnly()
    {
        if (_activeContextMenu is null) return;
        _activeContextMenu.IsOpen = false;
        _activeContextMenu = null;
        _contextMenuOpen = false;
    }
    private void Close_Click(object sender, RoutedEventArgs e) => HideMenus();
    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }

    private void RememberTargetWindow()
    {
        var foreground = GetForegroundWindow();
        var own = new WindowInteropHelper(this).Handle;
        var bar = new WindowInteropHelper(_actionBar).Handle;
        if (foreground != IntPtr.Zero && foreground != own && foreground != bar)
        {
            _targetWindow = foreground;
            _targetFocusWindow = GetFocusedControl(foreground);
        }
    }

    private async void PasteToTarget(ClipboardItem item, bool plainText)
    {
        try
        {
            // A recent-folder click is handled by the non-activating action bar.
            // Re-read the foreground target when the cached handle is stale so
            // that opening a folder from the shortcut bar still pastes into the
            // editor that was active before the popup appeared.
            var own = new WindowInteropHelper(this).Handle;
            var bar = new WindowInteropHelper(_actionBar).Handle;
            if (_targetWindow == IntPtr.Zero || !IsWindow(_targetWindow) || _targetWindow == own || _targetWindow == bar)
                RememberTargetWindow();
            _monitor?.IgnoreNextClipboardChange();
            if (!ClipboardMonitor.TryPaste(item, plainText, out var error))
            {
                StatusText.Text = $"无法写入剪贴板：{error}";
                return;
            }

            var target = _targetWindow;
            HideMenus();
            if (target == IntPtr.Zero || !await PasteAsync(target, _targetFocusWindow))
                StatusText.Text = "粘贴未送达：请确认目标窗口未以管理员权限运行";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"粘贴失败：{ex.Message}";
        }
    }

    private static async Task<bool> PasteAsync(IntPtr target, IntPtr targetFocus)
    {
        await Task.Delay(80);
        if (!FocusTargetWindow(target, targetFocus)) return false;
        await Task.Delay(35);
        return SendPasteKeystroke();
    }

    private void RefreshItems()
    {
        _items.Clear(); _favoriteItems.Clear();
        var folders = _repository.GetFolders();
        if (_favoritesMode)
        {
            _folders.Clear(); foreach (var folder in folders) _folders.Add(folder);
            if (_selectedFolder > 0) foreach (var item in _repository.GetFolderItems(_selectedFolder)) _favoriteItems.Add(item);
            else if (folders.Count > 0) foreach (var item in _repository.GetFolderItems(folders[0].Id)) _favoriteItems.Add(item);
        }
        else
        {
            IEnumerable<ClipboardItem> entries = _directFolderMode && _selectedFolder > 0
                ? _repository.GetFolderItems(_selectedFolder).OrderBy(i => i.CreatedAt)
                : _repository.GetHistory(_settings.Current.MaxHistoryItems);
            foreach (var item in entries) _items.Add(item);
        }
        if (_favoritesMode && _selectedFolder > 0)
        {
            _suppressFolderSelection = true;
            FolderList.SelectedItem = _folders.FirstOrDefault(f => f.Id == _selectedFolder);
            _suppressFolderSelection = false;
        }
        _actionBar.RefreshFolders(folders);
        HistoryContent.Visibility = _favoritesMode ? Visibility.Collapsed : Visibility.Visible;
        FavoritesContent.Visibility = _favoritesMode ? Visibility.Visible : Visibility.Collapsed;
        var visibleCount = _favoritesMode ? _favoriteItems.Count : _items.Count;
        StatusText.Text = _settings.Current.MonitorEnabled ? $"监听中 · {visibleCount} 条" : "已暂停监听";

        // Items are ordered for bottom-anchored browsing; wait for the refreshed
        // containers to be generated before moving the viewport to the last row.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_favoritesMode)
            {
                ScrollListToEnd(FavoriteItemList, _favoriteItems.Count > 0 ? _favoriteItems[^1] : null);
            }
            else if (_items.Count > 0)
            {
                ScrollListToEnd(ItemList, _items[^1]);
            }
        }), DispatcherPriority.Background);
    }
    private static void ScrollListToEnd(WpfListBox list, object? lastItem)
    {
        list.UpdateLayout();
        var viewer = FindVisualChild<ScrollViewer>(list);
        if (viewer is not null) viewer.ScrollToEnd();
        else if (lastItem is not null) list.ScrollIntoView(lastItem);
    }
    private void History_Click(object sender, RoutedEventArgs e) { _favoritesMode=false; _directFolderMode=false; _selectedFolder=0; RefreshItems(); }
    private void Favorites_Click(object sender, RoutedEventArgs e) { _favoritesMode=true; _directFolderMode=false; _selectedFolder=_repository.GetFolders().FirstOrDefault()?.Id ?? 0; RefreshItems(); }
    private void RecentFolder_Click(object sender, RoutedEventArgs e) { if ((sender as WpfButton)?.Tag is Folder folder) SelectRecentFromBar(folder); }
    private void FolderList_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_suppressFolderSelection && FolderList.SelectedItem is Folder folder) { _selectedFolder=folder.Id; _directFolderMode=false; _favoritesMode=true; _repository.MarkFolderUsed(folder.Id); RefreshItems(); } }
    private void Folder_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_suppressNextFolderClick)
        {
            _suppressNextFolderClick = false;
            e.Handled = true;
            return;
        }
        if (sender is Border { DataContext: Folder folder })
        {
            SelectFolder(folder);
            e.Handled = true;
        }
    }

    private void SelectFolder(Folder folder)
    {
        _selectedFolder = folder.Id;
        _directFolderMode = false;
        _favoritesMode = true;
        _repository.MarkFolderUsed(folder.Id);
        _suppressFolderSelection = true;
        FolderList.SelectedItem = folder;
        _suppressFolderSelection = false;
        RefreshItems();
    }
    private void Favorite_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as WpfButton)?.Tag is not ClipboardItem item) return;
        var menu = new ContextMenu();
        foreach (var folder in _repository.GetFolders())
        {
            var entry = new MenuItem { Header = folder.Name, Tag = folder };
            entry.Click += (_, _) => { _repository.AddToFolder(item.Id, folder.Id); _selectedFolder = 0; RefreshItems(); };
            menu.Items.Add(entry);
        }
        if (item.IsFavorite)
        {
            if (menu.Items.Count > 0) menu.Items.Add(new Separator());
            var remove = new MenuItem { Header = "取消收藏" };
            remove.Click += (_, _) => { _repository.RemoveFromAllFolders(item.Id); RefreshItems(); };
            menu.Items.Add(remove);
        }
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        var create = new MenuItem { Header = "新建收藏夹…" };
        create.Click += (_, _) =>
        {
            menu.IsOpen = false;
            var name = Prompt("新建收藏夹", "收藏夹名称", this);
            if (string.IsNullOrWhiteSpace(name)) return;
            try
            {
                var id = _repository.CreateFolder(name.Trim());
                _repository.AddToFolder(item.Id, id);
                RefreshItems();
            }
            catch (Exception ex) { ShowOperationError("创建收藏夹失败", ex); }
        };
        menu.Items.Add(create); OpenContextMenu(menu);
    }
    private void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        HideMenus();
        var name = Prompt("新建收藏夹", "收藏夹名称", this);
        if (string.IsNullOrWhiteSpace(name)) return;
        try { _repository.CreateFolder(name.Trim()); RefreshItems(); }
        catch (Exception ex) { ShowOperationError("创建收藏夹失败", ex); }
    }
    private void ClearHistory_Click(object sender, RoutedEventArgs e) { if (WpfMessageBox.Show("确定清空未收藏的历史记录？", "PromptFlow", MessageBoxButton.YesNo, MessageBoxImage.Question)==MessageBoxResult.Yes) { _repository.ClearHistory(); RefreshItems(); } }
    private void FolderList_Drop(object sender, System.Windows.DragEventArgs e)
    {
        var target = FindParent<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as Folder;
        if (e.Data.GetData(typeof(ClipboardItem)) is ClipboardItem item && target is not null)
        {
            if (_draggedFolder > 0 && _draggedFolder != target.Id) _repository.RemoveFromFolder(item.Id, _draggedFolder);
            _repository.AddToFolder(item.Id, target.Id);
            RefreshItems();
            return;
        }
        if (e.Data.GetData(typeof(Folder)) is Folder source && target is not null && source.Id != target.Id)
        {
            var ordered = _folders.Select(f => f.Id).ToList();
            var targetIndex = ordered.IndexOf(target.Id);
            var sourceIndex = ordered.IndexOf(source.Id);
            ordered.Remove(source.Id);
            if (sourceIndex < targetIndex) targetIndex--;
            ordered.Insert(Math.Clamp(targetIndex, 0, ordered.Count), source.Id);
            _repository.ReorderFolders(ordered); RefreshItems();
        }
    }
    private void ItemList_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetData(typeof(ClipboardItem)) is not ClipboardItem item) return;
        if (_selectedFolder > 0 && FindParent<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is ClipboardItem target && target.Id != item.Id)
        {
            var ordered = _items.Select(x => x.Id).ToList();
            var targetIndex = ordered.IndexOf(target.Id);
            var sourceIndex = ordered.IndexOf(item.Id);
            ordered.Remove(item.Id);
            if (sourceIndex < targetIndex) targetIndex--;
            ordered.Insert(Math.Clamp(targetIndex, 0, ordered.Count), item.Id);
            _repository.ReorderFolderItems(_selectedFolder, ordered); RefreshItems(); return;
        }
    }
    private void FavoriteItemList_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetData(typeof(ClipboardItem)) is not ClipboardItem item || _selectedFolder <= 0) return;
        if (FindParent<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is ClipboardItem target && target.Id != item.Id)
        {
            var ordered = _favoriteItems.Select(x => x.Id).ToList();
            var targetIndex = ordered.IndexOf(target.Id);
            var sourceIndex = ordered.IndexOf(item.Id);
            ordered.Remove(item.Id);
            if (sourceIndex < targetIndex) targetIndex--;
            ordered.Insert(Math.Clamp(targetIndex, 0, ordered.Count), item.Id);
            _repository.ReorderFolderItems(_selectedFolder, ordered); RefreshItems();
        }
    }
    private void ItemList_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || (e.GetPosition(ItemList) - _dragStart).Length < 8) return;
        if (FindParent<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is ClipboardItem item)
        {
            _draggedFolder = _directFolderMode ? _selectedFolder : 0;
            RunItemDrag(ItemList, item);
            _suppressNextItemClick = true;
            _draggedFolder = 0;
        }
    }
    private void FavoriteItemList_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || (e.GetPosition(FavoriteItemList) - _dragStart).Length < 8) return;
        if (FindParent<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is ClipboardItem item)
        {
            _draggedFolder = _selectedFolder;
            RunItemDrag(FavoriteItemList, item);
            _suppressNextItemClick = true;
            _draggedFolder = 0;
        }
    }
    private void FolderList_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || (e.GetPosition(FolderList) - _dragStart).Length < 8) return;
        if (FindParent<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is Folder folder)
        {
            OpenDragPreview(folder.Name);
            WpfGiveFeedbackEventHandler feedback = (_, args) =>
            {
                UpdateDragPreview();
                args.UseDefaultCursors = true;
            };
            FolderList.GiveFeedback += feedback;
            try { DragDrop.DoDragDrop(FolderList, folder, System.Windows.DragDropEffects.Move); }
            finally
            {
                FolderList.GiveFeedback -= feedback;
                CloseDragPreview();
                ClearDragHighlight();
            }
            _suppressNextFolderClick = true;
        }
    }

    private void RunItemDrag(WpfListBox source, ClipboardItem item)
    {
        OpenDragPreview(item.Preview);
        WpfGiveFeedbackEventHandler feedback = (_, args) =>
        {
            UpdateDragPreview();
            args.UseDefaultCursors = true;
        };
        source.GiveFeedback += feedback;
        try { DragDrop.DoDragDrop(source, item, System.Windows.DragDropEffects.Move); }
        finally
        {
            source.GiveFeedback -= feedback;
            CloseDragPreview();
            ClearDragHighlight();
        }
    }

    private void OpenDragPreview(string text)
    {
        CloseDragPreview();
        var previewText = string.IsNullOrWhiteSpace(text) ? "剪贴板条目" : text.ReplaceLineEndings(" ");
        var content = new Border
        {
            Width = 250,
            Height = 54,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(232, 255, 255, 255)),
            BorderBrush = TryFindResource("AccentBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.DeepSkyBlue,
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 0, 12, 0),
            Opacity = 0.86,
            Child = new TextBlock
            {
                Text = previewText,
                Foreground = TryFindResource("InkBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Black,
                FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                TextAlignment = TextAlignment.Left
            }
        };
        _dragPreviewPopup = new System.Windows.Controls.Primitives.Popup
        {
            AllowsTransparency = true,
            // Keep the preview alive while the native drag loop moves across
            // different controls. It is explicitly closed in the drag finally
            // block, so it cannot leak after a cancelled drop.
            StaysOpen = true,
            IsHitTestVisible = false,
            Placement = System.Windows.Controls.Primitives.PlacementMode.AbsolutePoint,
            Child = content
        };
        _dragPreviewPopup.IsOpen = true;
        UpdateDragPreview();
    }

    private void UpdateDragPreview()
    {
        if (_dragPreviewPopup is null) return;
        var cursor = Forms.Cursor.Position;
        var fromDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice;
        var scaleX = fromDevice?.M11 ?? 1d;
        var scaleY = fromDevice?.M22 ?? 1d;
        _dragPreviewPopup.HorizontalOffset = cursor.X * scaleX + 14;
        _dragPreviewPopup.VerticalOffset = cursor.Y * scaleY + 14;
    }

    private void CloseDragPreview()
    {
        if (_dragPreviewPopup is null) return;
        _dragPreviewPopup.IsOpen = false;
        _dragPreviewPopup.Child = null;
        _dragPreviewPopup = null;
    }

    private void List_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not WpfListBox list) return;
        var viewer = FindVisualChild<ScrollViewer>(list);
        if (viewer is null) return;
        viewer.ScrollToVerticalOffset(Math.Max(0, viewer.VerticalOffset - e.Delta / 3d));
        e.Handled = true;
    }

    private void ItemList_DragOver(object sender, WpfDragEventArgs e) => SetDragEffect(e, typeof(ClipboardItem));
    private void FavoriteItemList_DragOver(object sender, WpfDragEventArgs e) => SetDragEffect(e, typeof(ClipboardItem));
    private void FolderList_DragOver(object sender, WpfDragEventArgs e) => SetDragEffect(e, typeof(ClipboardItem), typeof(Folder));
    private void ItemCard_DragOver(object sender, WpfDragEventArgs e) { SetDragEffect(e, typeof(ClipboardItem)); HighlightCard(sender as Border); }
    private void FolderCard_DragOver(object sender, WpfDragEventArgs e) { SetDragEffect(e, typeof(ClipboardItem), typeof(Folder)); HighlightCard(sender as Border); }
    private void ItemCard_DragLeave(object sender, WpfDragEventArgs e) => ClearDragHighlight();
    private void FolderCard_DragLeave(object sender, WpfDragEventArgs e) => ClearDragHighlight();
    private void ItemCard_Drop(object sender, WpfDragEventArgs e)
    {
        try
        {
            if (e.Data.GetData(typeof(ClipboardItem)) is ClipboardItem source &&
                (sender as Border)?.DataContext is ClipboardItem target && source.Id != target.Id)
            {
                var folderId = _favoritesMode || _directFolderMode ? _selectedFolder : 0;
                if (folderId > 0)
                {
                    var ordered = (_favoritesMode ? _favoriteItems : _items).Select(x => x.Id).ToList();
                    var sourceIndex = ordered.IndexOf(source.Id);
                    var targetIndex = ordered.IndexOf(target.Id);
                    if (sourceIndex >= 0 && targetIndex >= 0)
                    {
                        ordered.RemoveAt(sourceIndex);
                        if (sourceIndex < targetIndex) targetIndex--;
                        ordered.Insert(Math.Clamp(targetIndex, 0, ordered.Count), source.Id);
                        _repository.ReorderFolderItems(folderId, ordered);
                        RefreshItems();
                    }
                }
            }
        }
        finally
        {
            ClearDragHighlight();
            e.Handled = true;
        }
    }
    private void FolderCard_Drop(object sender, WpfDragEventArgs e)
    {
        try
        {
            if ((sender as Border)?.DataContext is not Folder target) return;
            if (e.Data.GetData(typeof(ClipboardItem)) is ClipboardItem item)
            {
                MoveItemToFolder(item, target.Id);
            }
            else if (e.Data.GetData(typeof(Folder)) is Folder source && source.Id != target.Id)
            {
                var ordered = _folders.Select(f => f.Id).ToList();
                var targetIndex = ordered.IndexOf(target.Id);
                ordered.Remove(source.Id);
                if (ordered.Count > 0 && targetIndex >= ordered.Count) targetIndex = ordered.Count - 1;
                ordered.Insert(Math.Clamp(targetIndex, 0, ordered.Count), source.Id);
                _repository.ReorderFolders(ordered);
                RefreshItems();
            }
        }
        finally
        {
            ClearDragHighlight();
            e.Handled = true;
        }
    }

    private static void SetDragEffect(WpfDragEventArgs e, params Type[] types)
    {
        e.Effects = types.Any(e.Data.GetDataPresent) ? System.Windows.DragDropEffects.Move : System.Windows.DragDropEffects.None;
    }

    private void HighlightCard(Border? card)
    {
        if (card is null || ReferenceEquals(_dragHighlight, card)) return;
        ClearDragHighlight();
        _dragHighlight = card;
        card.BorderBrush = TryFindResource("AccentBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.DeepSkyBlue;
        card.BorderThickness = new Thickness(2);
    }

    private void ClearDragHighlight()
    {
        if (_dragHighlight is null) return;
        _dragHighlight.BorderBrush = TryFindResource("OutlineBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.LightGray;
        _dragHighlight.BorderThickness = new Thickness(1);
        _dragHighlight = null;
    }
    private void Item_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e) { if (sender is Border b && b.Child is Grid grid && grid.Children.OfType<Border>().FirstOrDefault() is Border preview) preview.Visibility=Visibility.Visible; }
    private void Item_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e) { if (sender is Border b && b.Child is Grid grid && grid.Children.OfType<Border>().FirstOrDefault() is Border preview) preview.Visibility=Visibility.Collapsed; }
    private void Item_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_suppressNextItemClick) { _suppressNextItemClick = false; e.Handled = true; return; }
        if (FindParent<WpfButton>(e.OriginalSource as DependencyObject) is not null) return;
        if (FindParent<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is ClipboardItem item)
        {
            PasteToTarget(item, false);
            e.Handled = true;
        }
    }

    private void MainWindow_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindParent<ListBoxItem>(source) is ListBoxItem row && row.DataContext is ClipboardItem item)
        {
            var menu = new ContextMenu(); var paste = new MenuItem { Header = "粘贴原格式" }; paste.Click += (_, _) => PasteToTarget(item, false); var plain = new MenuItem { Header = "粘贴为纯文本" }; plain.Click += (_, _) => PasteToTarget(item, true); var edit = new MenuItem { Header = "编辑词条" }; edit.Click += (_, _) => EditItem(item); var display = new MenuItem { Header = "编辑展示文字" }; display.Click += (_, _) => { var text=Prompt("展示文字", item.DisplayText); if(text is not null){item.DisplayText=text;_repository.UpdateItem(item);RefreshItems();} }; var delete = new MenuItem { Header = "删除词条" }; delete.Click += (_, _) => { if (WpfMessageBox.Show("确定删除此词条？", "PromptFlow", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes) { _repository.DeleteItem(item.Id); RefreshItems(); } }; menu.Items.Add(paste); menu.Items.Add(plain); menu.Items.Add(new Separator()); menu.Items.Add(edit); menu.Items.Add(display); menu.Items.Add(new Separator()); menu.Items.Add(delete); OpenContextMenu(menu); e.Handled=true;
        }
        else if (e.OriginalSource is DependencyObject folderSource && FindParent<ListBoxItem>(folderSource) is ListBoxItem folderRow && folderRow.DataContext is Folder folder)
        { var menu = new ContextMenu(); var lockItem=new MenuItem{Header=folder.IsLocked?"解锁收藏夹":"锁定收藏夹"}; lockItem.Click+=(_,_)=>{_repository.SetFolderLock(folder.Id,!folder.IsLocked);RefreshItems();}; var delete = new MenuItem { Header = "删除收藏夹" }; delete.Click += (_, _) => { if (WpfMessageBox.Show("确定删除此收藏夹？其中的词条不会被删除。", "PromptFlow", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes) { _repository.DeleteFolder(folder.Id); if (_selectedFolder == folder.Id) _selectedFolder = 0; RefreshItems(); } }; menu.Items.Add(lockItem); menu.Items.Add(new Separator()); menu.Items.Add(delete); OpenContextMenu(menu); e.Handled=true; }
    }
    private static T? FindParent<T>(DependencyObject? child) where T:DependencyObject { while(child is not null){if(child is T match)return match;child=System.Windows.Media.VisualTreeHelper.GetParent(child);} return null; }
    private static T? FindVisualChild<T>(DependencyObject? parent) where T : DependencyObject
    {
        if (parent is null) return null;
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            var nested = FindVisualChild<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    private void EditItem(ClipboardItem item){var text=Prompt("编辑词条内容",item.TextContent??item.DisplayText);if(text is not null){item.TextContent=text;item.DisplayText=text.ReplaceLineEndings(" ").Trim();_repository.UpdateItem(item);RefreshItems();}}
    private static string? Prompt(string title, string value, Window? owner = null)
    {
        var dialog = new InputDialog(title, value)
        {
            Owner = owner is { IsVisible: true } ? owner : null,
            Topmost = true,
            ShowActivated = true,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }

    private void ShowOperationError(string title, Exception exception)
    {
        StatusText.Text = $"{title}：{exception.Message}";
        WpfMessageBox.Show(this, $"{title}\n{exception.Message}", "PromptFlow", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings();
    private void ShowSettings()
    {
        var dialog = new SettingsWindow(_settings.Current, _repository.GetExclusions())
        {
            Owner = IsVisible ? this : null,
            Topmost = true,
            ShowActivated = true,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        dialog.SaveRequested += (_, args) => ApplySettings(args.Settings, args.Exclusions);
        HideMenus();
        dialog.ShowDialog();
    }
    private void ApplySettings(AppSettings next, IEnumerable<string> exclusions)
    {
        var previous = _settings.Current;
        if (string.IsNullOrWhiteSpace(next.DataDirectory)) next = next with { DataDirectory = SettingsService.GetDefaultDataDirectory() };
        if (_hotkey is not null && !string.Equals(next.Hotkey, previous.Hotkey, StringComparison.OrdinalIgnoreCase) && !_hotkey.Register(next.Hotkey))
        {
            next = next with { Hotkey = previous.Hotkey };
            _hotkey.Register(previous.Hotkey);
        }
        var pathChanged = !string.Equals(next.DataDirectory, _settings.Current.DataDirectory, StringComparison.OrdinalIgnoreCase);
        Directory.CreateDirectory(next.DataDirectory);
        if (pathChanged) { var oldDb=Path.Combine(_settings.Current.DataDirectory,"promptflow.db"); var newDb=Path.Combine(next.DataDirectory,"promptflow.db"); if (File.Exists(oldDb)&&!File.Exists(newDb)) File.Copy(oldDb,newDb); }
        _settings.Save(next);
        if (pathChanged) { _monitor?.Dispose(); _repository.Dispose(); _repository=new StorageRepository(next.DataDirectory); StartMonitor(); }
        _repository.SetExclusions(exclusions); _monitor?.RefreshSettings(); if (_tray is not null) _tray.MonitorEnabled=next.MonitorEnabled; RefreshItems();
    }
    private void LoadSettingsFields() { }
    private void ToggleMonitor() => ApplySettings(_settings.Current with { MonitorEnabled = !_settings.Current.MonitorEnabled }, _repository.GetExclusions());
    public void ClearHistoryFromSettings() { _repository.ClearHistory(); RefreshItems(); }
    protected override void OnClosed(EventArgs e){if(_outsideMouseHook!=IntPtr.Zero)UnhookWindowsHookEx(_outsideMouseHook);_actionBar.Close();_monitor?.Dispose();_hotkey?.Dispose();_tray?.Dispose();_repository.Dispose();base.OnClosed(e);}

    internal void SelectHistoryFromBar() => History_Click(this, new RoutedEventArgs());
    internal void SelectFavoritesFromBar() => Favorites_Click(this, new RoutedEventArgs());
    internal void RefreshPasteTarget() => RememberTargetWindow();
    internal void SelectRecentFromBar(Folder folder) { _selectedFolder = folder.Id; _repository.MarkFolderUsed(folder.Id); _favoritesMode = false; _directFolderMode = true; RefreshItems(); }
    internal void MoveItemToFolder(ClipboardItem item, long folderId)
    {
        try
        {
            if (_draggedFolder > 0 && _draggedFolder != folderId) _repository.RemoveFromFolder(item.Id, _draggedFolder);
            _repository.AddToFolder(item.Id, folderId);
            RefreshItems();
        }
        catch (Exception ex) { ShowOperationError("移动词条失败", ex); }
    }
    internal void CreateFolderFromBar() => NewFolder_Click(this, new RoutedEventArgs());
    internal void ShowSettingsFromBar() => ShowSettings();
    internal void CloseFromBar() => HideMenus();

    private void ConfigureAsNonActivatingWindow(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var source = HwndSource.FromHwnd(handle);
        source?.AddHook(WindowMessageHook);
        var styles = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        SetWindowLongPtr(handle, GwlExStyle, new IntPtr(styles | WsExNoActivate));
    }

    private static void KeepWindowNonActivating(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        SetWindowPos(handle, HwndTopmost, 0, 0, 0, 0, SwpNoActivate | SwpNoMove | SwpNoSize | SwpShowWindow);
    }

    private static IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmMouseActivate)
        {
            handled = true;
            return new IntPtr(MaNoActivate);
        }
        return IntPtr.Zero;
    }

    private IntPtr OutsideMouseCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && Environment.TickCount64 >= _outsideClickEnabledAt &&
            IsMouseButtonDown(wParam) && (IsVisible || _actionBar.IsVisible))
        {
            var point = Marshal.PtrToStructure<MouseHookStruct>(lParam).Point;
            var clickedRoot = GetAncestor(WindowFromPoint(point), GaRoot);

            // Context menus are separate popup windows. The first click outside
            // the popup only dismisses the popup; a later click can dismiss the
            // floating windows themselves.
            if (_contextMenuOpen)
            {
                var menuHandle = GetContextMenuHandle();
                var menuRoot = menuHandle == IntPtr.Zero ? IntPtr.Zero : GetAncestor(menuHandle, GaRoot);
                if (menuRoot == IntPtr.Zero || clickedRoot != menuRoot)
                    Dispatcher.BeginInvoke(CloseContextMenuOnly, DispatcherPriority.Input);
            }
            else
            {
                var own = new WindowInteropHelper(this).Handle;
                var bar = new WindowInteropHelper(_actionBar).Handle;
                if (clickedRoot != own && clickedRoot != bar)
                    Dispatcher.BeginInvoke(HideMenus, DispatcherPriority.Input);
            }
        }
        return CallNextHookEx(_outsideMouseHook, code, wParam, lParam);
    }

    private IntPtr GetContextMenuHandle()
    {
        if (_activeContextMenu is null || !_activeContextMenu.IsOpen) return IntPtr.Zero;
        return (PresentationSource.FromVisual(_activeContextMenu) as HwndSource)?.Handle ?? IntPtr.Zero;
    }

    private static bool IsMouseButtonDown(IntPtr message)
    {
        var value = message.ToInt32();
        return value == WmLButtonDown || value == WmRButtonDown || value == WmMButtonDown || value == WmXButtonDown;
    }

    private static bool FocusTargetWindow(IntPtr target, IntPtr targetFocus)
    {
        if (!IsWindow(target)) return false;
        var currentThread = GetCurrentThreadId();
        var targetThread = GetWindowThreadProcessId(target, out _);
        var attached = targetThread != 0 && targetThread != currentThread && AttachThreadInput(currentThread, targetThread, true);
        try
        {
            // Restoring an already visible/maximized target changes its user's window
            // layout. Only restore when the target is actually minimized.
            if (IsIconic(target)) ShowWindow(target, SwRestore);
            BringWindowToTop(target);
            SetForegroundWindow(target);
            if (targetFocus != IntPtr.Zero && IsWindow(targetFocus)) SetFocus(targetFocus);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (GetForegroundWindow() == target) return true;
                SetForegroundWindow(target);
                Thread.Sleep(20);
            }
            return GetForegroundWindow() == target;
        }
        finally
        {
            if (attached) AttachThreadInput(currentThread, targetThread, false);
        }
    }

    private static IntPtr GetFocusedControl(IntPtr window)
    {
        var thread = GetWindowThreadProcessId(window, out _);
        var info = new GuiThreadInfo { CbSize = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        return thread != 0 && GetGUIThreadInfo(thread, ref info) ? info.HwndFocus : IntPtr.Zero;
    }

    private static bool SendPasteKeystroke()
    {
        var inputs = new[]
        {
            new Input { Type = InputKeyboard, Union = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = VkControl } } },
            new Input { Type = InputKeyboard, Union = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = VkV } } },
            new Input { Type = InputKeyboard, Union = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = VkV, Flags = KeyEventKeyUp } } },
            new Input { Type = InputKeyboard, Union = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = VkControl, Flags = KeyEventKeyUp } } }
        };
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        return sent == inputs.Length;
    }

    private const ushort VkControl = 0x11, VkV = 0x56;
    private const uint KeyEventKeyUp = 0x0002;
    private const int WmMouseActivate = 0x0021, MaNoActivate = 3, GwlExStyle = -20;
    private const long WsExNoActivate = 0x08000000;
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoActivate = 0x0010, SwpShowWindow = 0x0040, SwRestore = 9, InputKeyboard = 1;

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, uint command);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint idThread, ref GuiThreadInfo info);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc proc, IntPtr module, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, [MarshalAs(UnmanagedType.LPArray), In] Input[] inputs, int size);

    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public InputUnion Union;
    }
    // INPUT is 40 bytes on x64: the union is sized for the largest MOUSEINPUT
    // member even when we only send KEYBDINPUT records.
    [StructLayout(LayoutKind.Explicit, Size = 32)] private struct InputUnion { [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort VirtualKey; public ushort ScanCode; public uint Flags; public uint Time; public UIntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo
    {
        public uint CbSize, Flags;
        public IntPtr HwndActive, HwndFocus, HwndCapture, HwndMenuOwner, HwndMoveSize, HwndCaret;
        public Rect RcCaret;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseHookStruct { public Point Point; public uint MouseData, Flags, Time; public IntPtr ExtraInfo; }
    private delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);
    private const int WhMouseLl = 14, WmLButtonDown = 0x0201, WmRButtonDown = 0x0204, WmMButtonDown = 0x0207, WmXButtonDown = 0x020B;
    private const uint GaRoot = 2;
}

public sealed class InputDialog : Window
{
    private readonly System.Windows.Controls.TextBox _box; public string Value => _box.Text;
    public InputDialog(string title,string value){Title=title;Width=420;Height=170;WindowStartupLocation=WindowStartupLocation.CenterScreen;ResizeMode=ResizeMode.NoResize;WindowStyle=WindowStyle.ToolWindow;var panel=new StackPanel{Margin=new Thickness(18)};_box=new System.Windows.Controls.TextBox{Text=value};panel.Children.Add(_box);var buttons=new StackPanel{Orientation=System.Windows.Controls.Orientation.Horizontal,HorizontalAlignment=System.Windows.HorizontalAlignment.Right,Margin=new Thickness(0,15,0,0)};var ok=new WpfButton{Content="确定",IsDefault=true,Width=80};ok.Click+=(_,_)=>{DialogResult=true;Close();};var cancel=new WpfButton{Content="取消",IsCancel=true,Width=80};buttons.Children.Add(ok);buttons.Children.Add(cancel);panel.Children.Add(buttons);Content=panel;}
}
