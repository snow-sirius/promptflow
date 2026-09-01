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
                Height = 40,
                MinWidth = 84,
                Padding = new Thickness(9, 0, 9, 0),
                ToolTip = "打开收藏夹"
            };
            button.Click += RecentFolder_Click;
            RecentFoldersPanel.Children.Add(button);
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
    private void Window_Deactivated(object sender, EventArgs e) => _owner.HandleMenuDeactivated();
}
