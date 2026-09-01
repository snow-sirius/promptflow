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
        RecentFoldersPanel.Children.Clear();
        foreach (var folder in folders.Where(f => f.LastUsedAt.HasValue).OrderByDescending(f => f.LastUsedAt).Take(4))
        {
            var button = new WpfButton
            {
                Content = folder.Name,
                Tag = folder,
                Height = 36,
                MinWidth = 84,
                Padding = new Thickness(9, 0, 9, 0),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalContentAlignment = System.Windows.VerticalAlignment.Center,
                ToolTip = "打开收藏夹"
            };
            button.AllowDrop = true;
            // Capture the editor that was active immediately before the
            // action-bar interaction. This keeps paste reliable when a folder
            // is opened from the shortcut strip.
            button.PreviewMouseLeftButtonDown += (_, _) => _owner.RefreshPasteTarget();
            button.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(typeof(ClipboardItem)) ? System.Windows.DragDropEffects.Move : System.Windows.DragDropEffects.None; e.Handled = true; };
            button.Drop += (_, e) =>
            {
                if (e.Data.GetData(typeof(ClipboardItem)) is ClipboardItem item)
                    _owner.MoveItemToFolder(item, folder.Id);
                e.Handled = true;
            };
            button.Click += RecentFolder_Click;
            RecentFoldersPanel.Children.Add(button);
        }

        // Keep the create action in the scrollable strip so it remains the
        // final item after all recent folders, including when the strip overflows.
        var addButton = new WpfButton
        {
            Content = "+",
            Width = 34,
            Height = 36,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalContentAlignment = System.Windows.VerticalAlignment.Center,
            ToolTip = "新建收藏夹"
        };
        addButton.Click += NewFolder_Click;
        RecentFoldersPanel.Children.Add(addButton);
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
