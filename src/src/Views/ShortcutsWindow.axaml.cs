using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using WinVM.ViewModels;

namespace WinVM.Views;

public partial class ShortcutsWindow : Window
{
    public ShortcutsWindow()
    {
        InitializeComponent();
        var panel = new Grid { ColumnDefinitions = new ColumnDefinitions("170,*") };
        var row = 0;
        foreach (var (keys, action) in MainViewModel.Shortcuts)
        {
            panel.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var kb = new Border { Classes = { "kbd" }, Margin = new Avalonia.Thickness(0, 3), Child = new TextBlock { Text = keys, FontSize = 12 } };
            var tx = new TextBlock { Text = action, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(kb, row); Grid.SetRow(tx, row); Grid.SetColumn(tx, 1);
            panel.Children.Add(kb); panel.Children.Add(tx);
            row++;
        }
        List.Items.Add(panel);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
