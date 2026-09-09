using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace v1per_wpf;

/// <summary>
/// Modal GUI dialogs used by commands that previously used the terminal input
/// line: text prompts, yes/no confirmations and arrow-key item pickers.
/// </summary>
public static class Dialogs
{
    private static Window? _owner;
    private static System.Windows.Threading.Dispatcher? _dispatcher;

    public static void Init(Window owner)
    {
        _owner = owner;
        _dispatcher = owner.Dispatcher;
    }

    public static Window? Owner => _owner;

    private static T OnUi<T>(Func<T> action)
    {
        if (_dispatcher is null || _dispatcher.CheckAccess())
            return action();
        return _dispatcher.Invoke(action);
    }

    private static void OnUi(Action action)
    {
        if (_dispatcher is null || _dispatcher.CheckAccess())
            action();
        else
            _dispatcher.Invoke(action);
    }

    /// <summary>Shows a modal text prompt. Returns the entered text or empty on cancel.</summary>
    public static Task<string> PromptAsync(string message, string defaultValue = "")
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        OnUi(() =>
        {
            var win = PromptWindow(message, defaultValue, out var input);
            if (win.ShowDialog() == true)
                tcs.SetResult(input.Text);
            else
                tcs.SetResult(string.Empty);
        });
        return tcs.Task;
    }

    /// <summary>Shows a modal yes/no confirmation.</summary>
    public static Task<bool> ConfirmAsync(string message)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        OnUi(() =>
        {
            var (win, yes) = ConfirmWindow(message);
            tcs.SetResult(win.ShowDialog() == true && yes);
        });
        return tcs.Task;
    }

    /// <summary>Shows a modal list picker. Returns the selected index or -1 on cancel.</summary>
    public static Task<int> PickAsync(string title, IReadOnlyList<string> items)
    {
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        OnUi(() =>
        {
            var (win, list) = PickWindow(title, items);
            if (win.ShowDialog() == true && list.SelectedIndex >= 0)
                tcs.SetResult(list.SelectedIndex);
            else
                tcs.SetResult(-1);
        });
        return tcs.Task;
    }

    private static Window PromptWindow(string message, string defaultValue, out TextBox input)
    {
        var box = new TextBox { Text = defaultValue, MinWidth = 340, Padding = new Thickness(6, 4, 6, 4), FontFamily = new("Cascadia Code, Consolas"), FontSize = 13 };
        var ok = DialogButton("OK", isDefault: true);
        var cancel = DialogButton("Cancel");

        var win = new Window
        {
            Title = "V1Per",
            Owner = _owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            WindowStyle = WindowStyle.ToolWindow,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            Background = (System.Windows.Media.Brush)Application.Current.FindResource("WindowBg"),
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextPrimary"),
        };
        var label = new TextBlock { Text = message, Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap, MaxWidth = 420 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(label);
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        win.Content = panel;

        ok.Click += (_, _) => { win.DialogResult = true; };
        cancel.Click += (_, _) => { win.DialogResult = false; };
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { win.DialogResult = true; } };
        box.Focus();
        win.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };

        input = box;
        return win;
    }

    private static (Window, bool) ConfirmWindow(string message)
    {
        var yes = DialogButton("Yes", isDefault: true);
        var no = DialogButton("No");
        var win = new Window
        {
            Title = "V1Per",
            Owner = _owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            WindowStyle = WindowStyle.ToolWindow,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            Background = (System.Windows.Media.Brush)Application.Current.FindResource("WindowBg"),
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextPrimary"),
        };
        var label = new TextBlock { Text = message, Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap, MaxWidth = 420 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(no);
        buttons.Children.Add(yes);
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(label);
        panel.Children.Add(buttons);
        win.Content = panel;

        var result = false;
        yes.Click += (_, _) => { result = true; win.DialogResult = true; };
        no.Click += (_, _) => { result = false; win.DialogResult = false; };
        return (win, result);
    }

    private static (Window, ListBox) PickWindow(string title, IReadOnlyList<string> items)
    {
        var list = new ListBox
        {
            MinWidth = 420,
            MinHeight = 260,
            FontFamily = new("Cascadia Code, Consolas"),
            FontSize = 12.5,
            Margin = new Thickness(0, 10, 0, 0),
        };
        list.ItemsSource = items;

        var ok = DialogButton("Select", isDefault: true);
        var cancel = DialogButton("Cancel");
        var hint = new TextBlock { Text = "Use ↑ / ↓ to move, Enter to select, Esc to cancel", Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextSecondary"), FontSize = 11, Margin = new Thickness(0, 8, 0, 0) };

        var win = new Window
        {
            Title = title,
            Owner = _owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            WindowStyle = WindowStyle.ToolWindow,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            Background = (System.Windows.Media.Brush)Application.Current.FindResource("WindowBg"),
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextPrimary"),
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.Bold, Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("AccentGlow") });
        panel.Children.Add(list);
        panel.Children.Add(hint);
        panel.Children.Add(buttons);
        win.Content = panel;

        ok.Click += (_, _) => { win.DialogResult = list.SelectedIndex >= 0; };
        cancel.Click += (_, _) => { win.DialogResult = false; };
        list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { win.DialogResult = list.SelectedIndex >= 0; }
            if (e.Key == Key.Escape) { win.DialogResult = false; }
        };
        list.SelectedIndex = 0;
        list.Focus();
        return (win, list);
    }

    private static Button DialogButton(string text, bool isDefault = false)
    {
        var b = new Button
        {
            Content = text,
            MinWidth = 84,
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(12, 6, 12, 6),
            IsDefault = isDefault,
            Cursor = Cursors.Hand,
        };
        var style = (Style?)Application.Current.FindResource(isDefault ? "AccentButton" : "GhostButton");
        if (style is not null)
            b.Style = style;
        return b;
    }
}