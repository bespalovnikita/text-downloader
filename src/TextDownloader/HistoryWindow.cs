using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace TextDownloader;

public sealed class HistoryWindow : Window
{
    public HistoryWindow(IEnumerable<HistoryEntry> history, Action<string?> openBook, Action<string?> openFolder)
    {
        Title = "История — Text Downloader";
        Width = 900; Height = 550; MinWidth = 650; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/TextDownloader;component/Styles.xaml", UriKind.Relative) });
        Background = (Brush)FindResource("WindowBrush");
        Foreground = (Brush)FindResource("TextBrush");
        FontFamily = new FontFamily("Segoe UI Variable, Segoe UI");
        FontSize = 13;
        UseLayoutRounding = true;
        var panel = new DockPanel { Margin = new Thickness(20) };
        var search = new TextBox { Margin = new Thickness(0, 0, 0, 12) };
        var searchLabel = new Label { Content = "Поиск по названию или ссылке", Target = search, Padding = new Thickness(0, 0, 0, 6), Foreground = Foreground };
        DockPanel.SetDock(searchLabel, Dock.Top);
        panel.Children.Add(searchLabel);
        DockPanel.SetDock(search, Dock.Top);
        panel.Children.Add(search);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var open = new Button { Content = "Открыть книгу", Style = (Style)FindResource("PrimaryButton") };
        var folder = new Button { Content = "Открыть папку" };
        var result = new ComboBox { MinWidth = 180, MaxWidth = 340, Margin = new Thickness(0, 0, 6, 0) };
        System.Windows.Automation.AutomationProperties.SetName(result, "Файл книги");
        actions.Children.Add(result);
        actions.Children.Add(open);
        actions.Children.Add(folder);
        actions.IsEnabled = false;
        DockPanel.SetDock(actions, Dock.Bottom); panel.Children.Add(actions);
        var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single };
        var cellStyle = (Style)FindResource("GridTextStyle");
        grid.Columns.Add(new DataGridTextColumn { Header = "Название", Binding = new Binding(nameof(HistoryEntry.Title)), Width = new DataGridLength(1, DataGridLengthUnitType.Star), ElementStyle = cellStyle });
        grid.Columns.Add(new DataGridTextColumn { Header = "Дата", Binding = new Binding(nameof(HistoryEntry.LocalDate)), Width = 145, ElementStyle = cellStyle });
        grid.Columns.Add(new DataGridTextColumn { Header = "Стр. / частей", Binding = new Binding(nameof(HistoryEntry.Pages)), Width = 110, ElementStyle = cellStyle });
        var empty = new TextBlock
        {
            Foreground = (Brush)FindResource("MutedBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        panel.Children.Add(new Grid { Children = { grid, empty } });
        var entries = history.ToArray();
        void UpdateResults()
        {
            var matches = entries.Where(entry => (entry.Title + " " + entry.Url)
                .Contains(search.Text, StringComparison.OrdinalIgnoreCase)).ToArray();
            grid.ItemsSource = matches;
            empty.Text = entries.Length == 0 ? "Скачанные книги появятся здесь" : "По вашему запросу ничего не найдено";
            empty.Visibility = matches.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        search.TextChanged += (_, _) => UpdateResults();
        grid.SelectionChanged += (_, _) =>
        {
            result.ItemsSource = grid.SelectedItem is HistoryEntry entry ? entry.Files : Array.Empty<string>();
            result.SelectedIndex = 0;
            actions.IsEnabled = result.SelectedItem is not null;
        };
        open.Click += (_, _) => openBook(result.SelectedItem as string);
        folder.Click += (_, _) => openFolder(result.SelectedItem as string);
        UpdateResults();
        Content = panel;
    }
}
