using System.Windows;
using System.Windows.Controls;
using WpfButton = System.Windows.Controls.Button;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfUserControl = System.Windows.Controls.UserControl;

namespace Inkling.Ui.Wpf;

/// <summary>Rows of trigger → text, editable in place, with Remove and "Add snippet".</summary>
internal sealed class SnippetEditor : WpfUserControl
{
    private readonly StackPanel _rows = new();
    private List<Snippet> _items = [];

    public event Action<List<Snippet>>? Changed;

    public SnippetEditor()
    {
        var header = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = "You type", Opacity = 0.65 });
        var h2 = new TextBlock { Text = "You get", Opacity = 0.65, Margin = new Thickness(8, 0, 0, 0) };
        Grid.SetColumn(h2, 1);
        header.Children.Add(h2);

        var add = new WpfButton { Content = "Add snippet", HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        add.Click += (_, _) =>
        {
            _items.Add(new Snippet());
            Render();
            if (_rows.Children[^1] is Grid g && g.Children[0] is WpfTextBox t) t.Focus();
        };

        var root = new StackPanel();
        root.Children.Add(header);
        root.Children.Add(_rows);
        root.Children.Add(add);
        Content = root;
    }

    public List<Snippet> Items
    {
        get => _items.Where(s => s.Trigger.Trim().Length > 0).Select(s => new Snippet { Trigger = s.Trigger.Trim(), Text = s.Text }).ToList();
        set { _items = value.Select(s => new Snippet { Trigger = s.Trigger, Text = s.Text }).ToList(); Render(); }
    }

    private void Render()
    {
        _rows.Children.Clear();
        foreach (var item in _items)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var trigger = new WpfTextBox { Text = item.Trigger };
            var text = new WpfTextBox { Text = item.Text, Margin = new Thickness(8, 0, 8, 0) };
            var remove = new WpfButton { Content = "Remove" };
            Grid.SetColumn(text, 1);
            Grid.SetColumn(remove, 2);
            var s = item;
            trigger.LostFocus += (_, _) => { if (s.Trigger != trigger.Text) { s.Trigger = trigger.Text; Changed?.Invoke(Items); } };
            text.LostFocus += (_, _) => { if (s.Text != text.Text) { s.Text = text.Text; Changed?.Invoke(Items); } };
            remove.Click += (_, _) => { _items.Remove(s); Render(); Changed?.Invoke(Items); };
            row.Children.Add(trigger);
            row.Children.Add(text);
            row.Children.Add(remove);
            _rows.Children.Add(row);
        }
    }
}
