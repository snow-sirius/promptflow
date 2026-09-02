using System.Windows;
using System.Windows.Controls;
using PromptFlow.Models;
using WpfButton = System.Windows.Controls.Button;

namespace PromptFlow;

public partial class ActionBarWindow : Window
{
    private readonly MainWindow _owner;

    public ActionBarWindow(MainWindow owner)
    {
        _owner = owner;
        InitializeComponent();
    }

    public void RefreshFolders(IEnumerable<Folder> folders)
    {
        ShortcutSlotsGrid.Children.Clear();
        var slots = _owner.GetShortcutFolders(folders);
        for (var slotIndex = 0; slotIndex < slots.Count; slotIndex++)
        {
            var folder = slots[slotIndex];
            var isEmpty = folder is null;
            var button = new WpfButton
            {
                Content = new TextBlock
                {
                    Text = folder?.Name ?? "（暂无）",
                    Foreground = isEmpty
                        ? _owner.TryFindResource("MutedBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gray
                        : _owner.TryFindResource("InkBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Black,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextAlignment = TextAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                },
                Tag = folder,
                Height = 36,
                MinWidth = 0,
                Padding = new Thickness(6, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalContentAlignment = System.Windows.VerticalAlignment.Center,
                ToolTip = folder?.Name ?? "将收藏夹卡片拖到这里"
            };
            button.AllowDrop = true;
            // Capture the editor that was active immediately before the
            // action-bar interaction. This keeps paste reliable when a folder
            // is opened from the shortcut strip.
            button.PreviewMouseLeftButtonDown += (_, _) => _owner.RefreshPasteTarget();
            var targetSlot = slotIndex;
            button.DragOver += (_, e) =>
            {
                e.Effects = e.Data.GetDataPresent(typeof(ClipboardItem)) || e.Data.GetDataPresent(typeof(Folder))
                    ? System.Windows.DragDropEffects.Move
                    : System.Windows.DragDropEffects.None;
                button.Background = e.Effects == System.Windows.DragDropEffects.None
                    ? System.Windows.Media.Brushes.Transparent
                    : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(227, 241, 243));
                e.Handled = true;
            };
            button.DragLeave += (_, _) => button.Background = System.Windows.Media.Brushes.Transparent;
            button.Drop += (_, e) =>
            {
                if (e.Data.GetData(typeof(Folder)) is Folder draggedFolder)
                    _owner.HandleShortcutFolderDrop(targetSlot, draggedFolder);
                else if (e.Data.GetData(typeof(ClipboardItem)) is ClipboardItem item && folder is not null)
                    _owner.MoveItemToFolder(item, folder.Id);
                button.Background = System.Windows.Media.Brushes.Transparent;
                e.Handled = true;
            };
            button.PreviewMouseRightButtonDown += (_, e) =>
            {
                if (folder is not null)
                {
                    _owner.ShowShortcutSlotMenu(targetSlot, folder, button);
                    e.Handled = true;
                }
            };
            button.Click += RecentFolder_Click;
            Grid.SetColumn(button, slotIndex);
            ShortcutSlotsGrid.Children.Add(button);
        }
    }

    private void History_Click(object sender, RoutedEventArgs e) => _owner.SelectHistoryFromBar();
    private void Favorites_Click(object sender, RoutedEventArgs e) => _owner.SelectFavoritesFromBar();
    private void NewFolder_Click(object sender, RoutedEventArgs e) => _owner.CreateFolderFromBar();
    private void Settings_Click(object sender, RoutedEventArgs e) => _owner.ShowSettingsFromBar();
    private void Close_Click(object sender, RoutedEventArgs e) => _owner.CloseFromBar();
    private void RecentFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as WpfButton)?.Tag is Folder folder) _owner.SelectRecentFromBar(folder);
    }
}
