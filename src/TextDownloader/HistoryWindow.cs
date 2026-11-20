using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace TextDownloader;

public sealed class HistoryWindow : Window
{
    public HistoryWindow(IEnumerable<HistoryEntry> history, Action<string?> openBook, Action<string?> openFolder)
    {
        Title = "История — Text Downloader";
        Width = 900; Height = 550; MinWidth = 650; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = System.Windows.Media.Brushes.White;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/TextDownloader;component/Styles.xaml", UriKind.Relative) });
        Background = (System.Windows.Media.Brush)FindResource("PanelBrush");
        Foreground = System.Windows.Media.Brushes.WhiteSmoke;
        var panel = new DockPanel { Margin = new Thickness(16) };
        var search = new TextBox { Margin = new Thickness(0, 0, 0, 12), ToolTip = "Поиск по названию или URL" };
        DockPanel.SetDock(search, Dock.Top); panel.Children.Add(search);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var open = new Button { Content = "Открыть книгу" };
        var folder = new Button { Content = "Открыть папку" };
        var result = new ComboBox { MinWidth = 180, MaxWidth = 380, Margin = new Thickness(6, 0, 0, 0) };
        actions.Children.Add(open); actions.Children.Add(folder); actions.Children.Add(result);
        DockPanel.SetDock(actions, Dock.Bottom); panel.Children.Add(actions);
        var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single };
        grid.Columns.Add(new DataGridTextColumn { Header = "Название", Binding = new System.Windows.Data.Binding("Title"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Дата", Binding = new System.Windows.Data.Binding("LocalDate"), Width = 145 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Страниц", Binding = new System.Windows.Data.Binding("Pages"), Width = 75 });
        panel.Children.Add(grid);
        var entries = history.ToArray();
        grid.ItemsSource = entries;
        search.TextChanged += (_, _) => grid.ItemsSource = entries.Where(entry => (entry.Title + " " + entry.Url).Contains(search.Text, StringComparison.OrdinalIgnoreCase)).ToArray();
        grid.SelectionChanged += (_, _) =>
        {
            result.ItemsSource = grid.SelectedItem is HistoryEntry entry ? entry.Files : Array.Empty<string>();
            result.SelectedIndex = 0;
        };
        open.Click += (_, _) => openBook(result.SelectedItem as string);
        folder.Click += (_, _) => openFolder(result.SelectedItem as string);
        Content = panel;
    }
}
