using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using WpfButton = System.Windows.Controls.Button;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfUserControl = System.Windows.Controls.UserControl;

namespace Inkling.Ui.Wpf;

/// <summary>An editable list of app (process) names: one row per app with Remove, plus "add" from running apps or typed in.</summary>
internal sealed class AppListEditor : WpfUserControl
{
    private readonly StackPanel _rows = new();
    private readonly WpfComboBox _picker = new() { IsEditable = true, MinWidth = 260, ToolTip = "Pick a running app or type its process name (without .exe)" };
    private List<string> _items = [];

    public event Action<List<string>>? Changed;

    public AppListEditor()
    {
        var add = new WpfButton { Content = "Add", Margin = new Thickness(8, 0, 0, 0) };
        add.Click += (_, _) => AddCurrent();
        _picker.DropDownOpened += (_, _) => FillRunningApps();
        _picker.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { AddCurrent(); e.Handled = true; } };

        var addRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(add, Dock.Right);
        addRow.Children.Add(add);
        addRow.Children.Add(_picker);

        var root = new StackPanel();
        root.Children.Add(_rows);
        root.Children.Add(addRow);
        Content = root;
    }

    public List<string> Items
    {
        get => [.. _items];
        set { _items = [.. value]; Render(); }
    }

    private void AddCurrent()
    {
        var name = (_picker.Text ?? "").Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        var paren = name.IndexOf(" (", StringComparison.Ordinal); // "Notepad (Notepad)" from the picker
        if (paren > 0 && name.EndsWith(')')) name = name[(paren + 2)..^1];
        if (name.Length == 0 || _items.Contains(name, StringComparer.OrdinalIgnoreCase)) { _picker.Text = ""; return; }
        _items.Add(name);
        _picker.Text = "";
        Render();
        Changed?.Invoke(Items);
    }

    private void Render()
    {
        _rows.Children.Clear();
        if (_items.Count == 0)
        {
            _rows.Children.Add(new TextBlock { Text = "None", Opacity = 0.6 });
            return;
        }
        foreach (var item in _items)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var remove = new WpfButton { Content = "Remove", Padding = new Thickness(10, 2, 10, 2) };
            var name = item;
            remove.Click += (_, _) => { _items.Remove(name); Render(); Changed?.Invoke(Items); };
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            row.Children.Add(new TextBlock { Text = item, VerticalAlignment = VerticalAlignment.Center });
            _rows.Children.Add(row);
        }
    }

    /// <summary>Apps with a visible window right now, as "Friendly name (process)".</summary>
    private void FillRunningApps()
    {
        var entries = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.MainWindowHandle == 0 || p.ProcessName.Equals("Inkling", StringComparison.OrdinalIgnoreCase)) continue;
                string friendly = p.ProcessName;
                try { friendly = p.MainModule?.FileVersionInfo.FileDescription is { Length: > 0 } d ? d.Trim() : p.ProcessName; } catch { }
                entries.Add(friendly.Equals(p.ProcessName, StringComparison.OrdinalIgnoreCase) ? p.ProcessName : $"{friendly} ({p.ProcessName})");
            }
            catch { }
            finally { p.Dispose(); }
        }
        _picker.ItemsSource = entries.ToList();
    }
}
