using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Windmill;

/// <summary>
/// Draws and edits custom auto-arrange layouts: split a zone in two, drag a divider, remove a zone, choose which
/// pick goes where, space columns or rows evenly. Edits happen on a copy and are written by Save (or straight away
/// with Auto-save on); Undo and Redo step through every change made since the window opened.
/// </summary>
internal sealed class LayoutEditorWindow : Window
{
    const double MaxW = 640, MaxH = 380, Hit = 14;
    const string IconFont = "Segoe Fluent Icons, Segoe MDL2 Assets";

    // The canvas is a scaled copy of this display's work area, so zone sizes can be read off in pixels.
    double W = MaxW, H = 360;
    int _workW = 1920, _workH = 1080;

    List<SplitNode> _work = ArrangeStore.Clone(ArrangeStore.All); // what's being edited; saved on Save
    readonly Stack<(string json, int index)> _undo = new(), _redo = new();
    string _saved = ArrangeStore.Serialize(ArrangeStore.All); // as last written, to tell whether anything changed
    bool _autoSave;

    bool Dirty => ArrangeStore.Serialize(_work) != _saved;

    readonly ListBox _list = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    readonly Canvas _canvas = new() { Width = MaxW, Height = 360, Background = Brushes.Transparent, ClipToBounds = true };
    readonly StackPanel _right = new();
    readonly Border _frame = new() { CornerRadius = new CornerRadius(6), ClipToBounds = true };
    readonly TextBlock _base = Caption(""), _hint = Caption(""), _status = Caption("");
    readonly TextBox _nameBox = new() { Width = 260, FontSize = 15, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Layout name, shown in the snap menu" };
    readonly TextBlock _count = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, MinWidth = 22, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    Button _fewer = null!, _more = null!;
    (string json, int index) _nameBefore; // how things were before typing a name, for Undo
    readonly Button _undoBtn, _redoBtn, _saveBtn, _deleteBtn;
    readonly Button _splitCols, _splitRows, _remove, _evenCols, _evenRows;
    readonly ComboBox _number = new() { MinWidth = 76, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Which pick goes in this zone. Keys 1-6 do the same." };
    readonly CheckBox _autoBox = new() { Content = "Auto-save", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 12, 0) };
    SplitNode? _selected;
    (SplitNode node, NRect rect)? _drag;

    // Cut tool: while on, clicking a zone cuts it where the dashed line is (Shift turns the line across).
    readonly ToggleButton _cutBtn;
    Border _canvasCard = null!;
    bool _cutMode;
    Point? _cutAt; // pointer on the canvas while cutting
    readonly System.Windows.Shapes.Line _cutLine = new()
    {
        Stroke = Brushes.White, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 3, 2 }, IsHitTestVisible = false,
    };
    double _dragStart;
    (string json, int index) _dragBefore; // how things were before the drag, for Undo
    bool _updating;

    SplitNode? Current => _list.SelectedIndex >= 0 && _list.SelectedIndex < _work.Count ? _work[_list.SelectedIndex] : null;

    public LayoutEditorWindow()
    {
        Title = "Custom layouts";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ThemeMode = ThemeMode.System;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 14;
        SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");

        _undoBtn = IconButton(Glyph(""), null, "Undo (Ctrl+Z)", Undo);
        _redoBtn = IconButton(Glyph(""), null, "Redo (Ctrl+Y)", Redo);
        _saveBtn = IconButton(Glyph(""), "Save", "Save your layouts (Ctrl+S)", Commit);
        _saveBtn.SetResourceReference(StyleProperty, "AccentButtonStyle");
        _saveBtn.Margin = new Thickness(0);
        _deleteBtn = IconButton(Glyph("\uE74D"), null, "Delete layout", Delete, "Removes the selected layout. Undo brings it back.");

        const string Box = "M1.5,2.5 H14.5 V13.5 H1.5 Z ";
        _splitCols = IconButton(Drawn(Box + "M8,2.5 V13.5"), null, "Split side by side", () => Split(vertical: true), "Cuts the selected zone into a left and a right half.");
        _splitRows = IconButton(Drawn(Box + "M1.5,8 H14.5"), null, "Split top and bottom", () => Split(vertical: false), "Cuts the selected zone into a top and a bottom half.");
        _remove = IconButton(Drawn(Box + "M5.5,8 H10.5"), null, "Remove zone", Remove, "Deletes the selected zone. The zone next to it takes the space.");
        _evenCols = IconButton(Drawn(Box + "M5.83,2.5 V13.5 M10.17,2.5 V13.5"), null, "Even columns", () => Space(vertical: true), "Makes the columns the same width: around the selected zone, or everywhere when no zone is selected.");
        _evenRows = IconButton(Drawn(Box + "M1.5,6.17 H14.5 M1.5,9.83 H14.5"), null, "Even rows", () => Space(vertical: false), "Makes the rows the same height: around the selected zone, or everywhere when no zone is selected.");
        _cutBtn = new ToggleButton
        {
            Content = Glyph("\uE8C6"), Padding = new Thickness(9, 7, 9, 7), Margin = new Thickness(0, 0, 8, 8),
            ToolTip = Tip("Cut tool (C)", "Click a zone to cut it where the dashed line is. Hold Shift to cut top and bottom instead. Esc stops."),
        };
        ToolTipService.SetInitialShowDelay(_cutBtn, 300);
        System.Windows.Automation.AutomationProperties.SetName(_cutBtn, "Cut tool");
        _cutBtn.Checked += (_, _) => SetCut(true);
        _cutBtn.Unchecked += (_, _) => SetCut(false);

        // Left: the list of layouts.
        var left = new DockPanel { Width = 230 };
        var leftHead = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        leftHead.Children.Add(Heading("Your layouts"));
        leftHead.Children.Add(Caption("Used by the snap menu and auto-arrange. Scroll the wheel in either to switch layouts."));
        DockPanel.SetDock(leftHead, Dock.Top);
        left.Children.Add(leftHead);
        var leftButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        leftButtons.Children.Add(IconButton(Glyph(""), "New layout", "New layout", New, "Starts a layout with two zones side by side."));
        leftButtons.Children.Add(_deleteBtn);
        DockPanel.SetDock(leftButtons, Dock.Bottom);
        left.Children.Add(leftButtons);
        left.Children.Add(Card(_list, padding: 6));

        // Right: header with undo / save, the canvas, then the tools.
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 12), LastChildFill = true };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(_undoBtn);
        actions.Children.Add(_redoBtn);
        actions.Children.Add(_autoBox);
        actions.Children.Add(_saveBtn);
        DockPanel.SetDock(actions, Dock.Right);
        head.Children.Add(actions);
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        nameRow.Children.Add(_nameBox);
        head.Children.Add(nameRow);
        head.Margin = new Thickness(0, 0, 0, 12);

        // How many windows: − adds/removes a zone without having to pick one first.
        _fewer = IconButton(Glyph("\uE738"), null, "One window fewer", Fewer, "Removes the highest-numbered zone.");
        _more = IconButton(Glyph("\uE710"), null, "One window more", More, "Splits the biggest zone in half.");
        _fewer.Margin = _more.Margin = new Thickness(0);
        var stepper = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var wl = new TextBlock { Text = "Windows", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        stepper.Children.Add(wl);
        stepper.Children.Add(_fewer);
        _count.Margin = new Thickness(10, 0, 10, 0);
        stepper.Children.Add(_count);
        stepper.Children.Add(_more);
        var countRow = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(stepper, Dock.Left);
        countRow.Children.Add(stepper);
        _base.Margin = new Thickness(20, 0, 0, 0);
        _base.VerticalAlignment = VerticalAlignment.Center;
        countRow.Children.Add(_base);

        _frame.Child = _canvas;
        var canvasCard = _canvasCard = Card(_frame, padding: 11);
        canvasCard.BorderThickness = new Thickness(2); // the border turns accent-coloured while cutting
        canvasCard.HorizontalAlignment = HorizontalAlignment.Left;
        _hint.Margin = new Thickness(2, 8, 0, 0);
        _hint.HorizontalAlignment = _status.HorizontalAlignment = HorizontalAlignment.Left;

        // One row of tools for the selected zone: split, remove | even spacing | number.
        var tools = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        foreach (var b in new UIElement[] { _cutBtn, Divider(), _splitCols, _splitRows, _remove, Divider(), _evenCols, _evenRows, Divider() }) tools.Children.Add(b);
        var numberPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8), VerticalAlignment = VerticalAlignment.Center };
        numberPanel.Children.Add(new TextBlock { Text = "Number", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        numberPanel.Children.Add(_number);
        _number.SelectionChanged += (_, _) => { if (!_updating && _number.SelectedIndex >= 0) SetNumber(_number.SelectedIndex); };
        tools.Children.Add(numberPanel);

        _status.Margin = new Thickness(2, 12, 0, 0);

        var right = _right;
        right.Children.Add(head);
        right.Children.Add(countRow);
        right.Children.Add(canvasCard);
        right.Children.Add(_hint);
        right.Children.Add(tools);
        right.Children.Add(_status);

        var root = new Grid { Margin = new Thickness(24) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(right, 2);
        root.Children.Add(left);
        root.Children.Add(right);
        Content = root;

        _autoSave = Settings.Load().ArrangeEditorAutoSave;
        _autoBox.IsChecked = _autoSave;
        _autoBox.ToolTip = "Save every change straight away";
        _autoBox.Checked += (_, _) => SetAutoSave(true);
        _autoBox.Unchecked += (_, _) => SetAutoSave(false);

        _list.SelectionChanged += (_, _) => { if (!_updating) { _selected = null; Render(); } };
        _canvas.MouseMove += OnDragMove;
        _canvas.MouseMove += (_, e) => { if (_cutMode) { _cutAt = e.GetPosition(_canvas); ShowCut(); } };
        _canvas.MouseLeave += (_, _) => { _cutAt = null; ShowCut(); };
        PreviewKeyUp += (_, e) => { if (_cutMode && e.Key is Key.LeftShift or Key.RightShift) ShowCut(); };
        _canvas.MouseLeftButtonUp += (_, _) => EndDrag();
        // Alt+Tab, a UAC prompt or a message box can take the mouse mid-drag, and no button-up follows.
        _canvas.LostMouseCapture += (_, _) => EndDrag();
        PreviewKeyDown += OnKey;
        _nameBox.GotKeyboardFocus += (_, _) => _nameBefore = (ArrangeStore.Serialize(_work), _list.SelectedIndex);
        _nameBox.TextChanged += (_, _) =>
        {
            if (_updating || Current is not { } cur) return;
            cur.Name = _nameBox.Text;
            UpdateStatus();
        };
        _nameBox.LostKeyboardFocus += (_, _) => NameDone();
        _nameBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { NameDone(); Keyboard.ClearFocus(); } };
        Closing += OnClosing;

        Loaded += (_, _) => { FitToDisplay(); Render(); };
        FitToDisplay();
        RefreshList(select: 0);
    }

    // ---------- small UI helpers ----------

    static TextBlock Heading(string text) => new() { Text = text, FontSize = 16, FontWeight = FontWeights.SemiBold };

    static TextBlock Caption(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return t;
    }

    static Border Card(UIElement child, double padding)
    {
        var b = new Border { Child = child, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(padding) };
        b.SetResourceReference(Border.BackgroundProperty, "ControlFillColorDefaultBrush");
        b.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        return b;
    }

    /// <summary>A thin vertical line between groups of toolbar buttons.</summary>
    static Border Divider()
    {
        var d = new Border { Width = 1, Height = 22, Margin = new Thickness(4, 0, 12, 8), VerticalAlignment = VerticalAlignment.Center };
        d.SetResourceReference(Border.BackgroundProperty, "DividerStrokeColorDefaultBrush");
        return d;
    }

    static TextBlock Glyph(string glyph) =>
        new() { Text = glyph, FontFamily = new FontFamily(IconFont), FontSize = 16, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>A 16 px line icon drawn in the button's text colour, which dims with the button when it's disabled.</summary>
    static Path Drawn(string data)
    {
        var p = new Path
        {
            Data = Geometry.Parse(data), Width = 16, Height = 16, StrokeThickness = 1.4,
            StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            VerticalAlignment = VerticalAlignment.Center,
        };
        p.SetBinding(Shape.StrokeProperty, new Binding { Path = new PropertyPath(TextElement.ForegroundProperty), RelativeSource = RelativeSource.Self });
        return p;
    }

    /// <param name="text">Label next to the icon, or null for an icon-only button.</param>
    /// <param name="tip">Tooltip title (also the name screen readers announce).</param>
    /// <param name="detail">Optional second tooltip line saying what the button does.</param>
    static Button IconButton(FrameworkElement icon, string? text, string tip, Action click, string? detail = null)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(icon);
        if (text != null)
            content.Children.Add(new TextBlock { Text = text, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });

        object tooltip = tip;
        if (detail != null)
        {
            var t = new StackPanel { MaxWidth = 280 };
            t.Children.Add(new TextBlock { Text = tip, FontWeight = FontWeights.SemiBold });
            t.Children.Add(new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            tooltip = t;
        }
        var b = new Button
        {
            Content = content, ToolTip = tooltip, Margin = new Thickness(0, 0, 8, 8),
            Padding = text != null ? new Thickness(12, 7, 14, 7) : new Thickness(9, 7, 9, 7),
        };
        ToolTipService.SetShowOnDisabled(b, true); // so a greyed-out tool still says what it does
        ToolTipService.SetInitialShowDelay(b, 300);
        b.Click += (_, _) => click();
        System.Windows.Automation.AutomationProperties.SetName(b, tip); // screen readers; content is a panel
        return b;
    }

    // ---------- canvas size ----------

    /// <summary>Shapes the canvas like the work area of the display this window is on.</summary>
    void FitToDisplay()
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var screen = handle != IntPtr.Zero ? System.Windows.Forms.Screen.FromHandle(handle) : System.Windows.Forms.Screen.PrimaryScreen;
        var area = screen?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        _workW = Math.Max(1, area.Width);
        _workH = Math.Max(1, area.Height);
        double k = Math.Min(MaxW / _workW, MaxH / _workH);
        W = _workW * k;
        H = _workH * k;
        _canvas.Width = _frame.Width = W;
        _canvas.Height = _frame.Height = H;
        _right.Width = W + 26; // the preview card's width, so the header and tools line up with it
        _base.Text = $"Sizes are pixels on this display ({_workW} × {_workH}). Layouts are saved as proportions, so they fit any screen.";
    }

    // ---------- list ----------

    static string CountLabel(SplitNode n)
    {
        int c = ArrangeStore.Leaves(n).Count;
        return $"{c} window{(c == 1 ? "" : "s")}";
    }

    /// <summary>A list entry: a tiny picture of the layout and its window count.</summary>
    object Entry(SplitNode n)
    {
        const double tw = 56, th = 32;
        var mini = new Canvas { Width = tw, Height = th };
        foreach (var (_, r) in ArrangeStore.Leaves(n))
        {
            var cell = new Border { Width = Math.Max(0, r.W * tw - 2), Height = Math.Max(0, r.H * th - 2), CornerRadius = new CornerRadius(2) };
            cell.SetResourceReference(Border.BackgroundProperty, "AccentFillColorDefaultBrush");
            Canvas.SetLeft(cell, r.X * tw + 1);
            Canvas.SetTop(cell, r.Y * th + 1);
            mini.Children.Add(cell);
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 4, 2, 4) };
        row.Children.Add(mini);
        var text = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = ArrangeStore.DisplayName(n, _work.IndexOf(n)), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 130 });
        text.Children.Add(Caption(CountLabel(n)));
        row.Children.Add(text);
        return row;
    }

    void RefreshList(int select, SplitNode? keep = null)
    {
        _updating = true;
        _list.Items.Clear();
        foreach (var n in _work) _list.Items.Add(Entry(n));
        _list.SelectedIndex = _work.Count == 0 ? -1 : Math.Clamp(select, 0, _work.Count - 1);
        _updating = false;
        _selected = keep;
        Render();
    }

    // ---------- saving and history ----------

    /// <summary>Call before every edit, so Undo can go back to how things were.</summary>
    void Snapshot()
    {
        _undo.Push((ArrangeStore.Serialize(_work), _list.SelectedIndex));
        _redo.Clear();
    }

    /// <summary>After an edit: mark unsaved (or save, with Auto-save on) and redraw.</summary>
    void Changed(SplitNode? keep, int? select = null)
    {
        if (_autoSave) Commit();
        RefreshList(select ?? _list.SelectedIndex, keep);
    }

    string? _saveError; // why the last save failed, until one succeeds

    void Commit()
    {
        _saveError = ArrangeStore.Replace(ArrangeStore.Clone(_work));
        if (_saveError == null) _saved = ArrangeStore.Serialize(_work);
        UpdateStatus();
    }

    void SetAutoSave(bool on)
    {
        _autoSave = on;
        // Only this one setting changes; if the file can't be read right now, don't overwrite it with defaults.
        if (Settings.TryLoad(out var s))
        {
            s.ArrangeEditorAutoSave = on;
            try { s.Save(); } catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
        }
        if (on && Dirty) Commit();
        UpdateStatus();
    }

    void Undo() => Step(_undo, _redo);
    void Redo() => Step(_redo, _undo);

    void Step(Stack<(string json, int index)> from, Stack<(string json, int index)> to)
    {
        if (from.Count == 0) return;
        to.Push((ArrangeStore.Serialize(_work), _list.SelectedIndex));
        var (json, index) = from.Pop();
        _work = ArrangeStore.Deserialize(json);
        Changed(keep: null, select: index);
        // Render leaves a focused name box alone so typing isn't disturbed; after an undo it must show the result.
        _updating = true;
        _nameBox.Text = Current?.Name ?? "";
        _nameBox.CaretIndex = _nameBox.Text.Length;
        _updating = false;
        _nameBefore = (ArrangeStore.Serialize(_work), _list.SelectedIndex); // the rename baseline is now this state
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (_drag != null) return; // undo or renumbering now would leave the drag editing a stale tree
        if (_nameBox.IsKeyboardFocused && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key is Key.Z or Key.Y)
            NameDone(); // so Ctrl+Z undoes the rename typed so far, not the edit before it
        if (_cutMode && e.Key is Key.LeftShift or Key.RightShift) { ShowCut(); return; }
        bool typing = _nameBox.IsKeyboardFocused;
        if (!typing && Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.C && Current != null)
        {
            _cutBtn.IsChecked = !_cutMode;
            e.Handled = true;
            return;
        }
        if (_cutMode && e.Key == Key.Escape) { _cutBtn.IsChecked = false; e.Handled = true; return; }
        int digit = e.Key is >= Key.D1 and <= Key.D6 ? e.Key - Key.D1 : e.Key is >= Key.NumPad1 and <= Key.NumPad6 ? e.Key - Key.NumPad1 : -1;
        if (digit >= 0 && Keyboard.Modifiers == ModifierKeys.None && !_nameBox.IsKeyboardFocused && _selected != null) SetNumber(digit);
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z) Undo();
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Y) Redo();
        else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.Z) Redo();
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S) Commit();
        else return;
        e.Handled = true;
    }

    void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!Dirty) return;
        var answer = MessageBox.Show(this, "Save the changes to your layouts?", "Auto-arrange layouts",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);
        if (answer == MessageBoxResult.Yes)
        {
            Commit();
            if (_saveError != null) e.Cancel = true; // stay open with the error showing, rather than lose the edits
        }
        else if (answer == MessageBoxResult.Cancel) e.Cancel = true;
    }

    void UpdateStatus()
    {
        _undoBtn.IsEnabled = _undo.Count > 0;
        _redoBtn.IsEnabled = _redo.Count > 0;
        bool dirty = Dirty;
        _saveBtn.IsEnabled = dirty;
        _status.Text = _saveError != null ? $"Couldn't save your layouts: {_saveError}"
            : _autoSave ? "Auto-save is on: every change is saved right away."
            : dirty ? "You have unsaved changes." : "All changes saved.";
    }

    // ---------- edits ----------

    void New()
    {
        Snapshot();
        _work.Add(new SplitNode
        {
            Name = $"Layout {_work.Count + 1}", Vertical = true, A = new SplitNode { Index = 0 }, B = new SplitNode { Index = 1 },
        });
        Changed(keep: null, select: _work.Count - 1);
    }

    void Delete()
    {
        if (Current is not { } cur) return;
        Snapshot();
        int at = _list.SelectedIndex;
        _work.Remove(cur);
        Changed(keep: null, select: at);
    }

    void Split(bool vertical)
    {
        if (Current is not { } root || _selected is not { IsLeaf: true } leaf) return;
        int count = ArrangeStore.Leaves(root).Count;
        if (count >= Arranger.MaxWindows) return;
        Snapshot();
        leaf.Vertical = vertical;
        leaf.Ratio = 0.5;
        leaf.A = new SplitNode { Index = leaf.Index };
        leaf.B = new SplitNode { Index = count };
        Changed(keep: leaf.B);
    }

    void Remove()
    {
        if (Current is not { } root || _selected is not { IsLeaf: true } leaf || root.IsLeaf) return;
        Snapshot();
        RemoveLeaf(root, leaf);
        Changed(keep: null);
    }

    /// <summary>Removes the last-numbered zone; its neighbour takes the space.</summary>
    void Fewer()
    {
        if (Current is not { } root || root.IsLeaf) return;
        Snapshot();
        var last = ArrangeStore.Leaves(root).MaxBy(l => l.leaf.Index).leaf;
        var (sibling, holder) = RemoveLeaf(root, last);
        Changed(keep: _selected == last ? null : _selected == sibling ? holder : _selected);
    }

    /// <summary>Adds a zone by halving the biggest one across its longer side.</summary>
    void More()
    {
        if (Current is not { } root) return;
        var leaves = ArrangeStore.Leaves(root);
        if (leaves.Count >= Arranger.MaxWindows) return;
        Snapshot();
        var (leaf, r) = leaves.MaxBy(l => l.rect.W * _workW * l.rect.H * _workH);
        leaf.Vertical = r.W * _workW >= r.H * _workH;
        leaf.Ratio = 0.5;
        leaf.A = new SplitNode { Index = leaf.Index };
        leaf.B = new SplitNode { Index = leaves.Count };
        Changed(keep: leaf.B);
    }

    /// <summary>Typing a name is one undo step, counted when the name box loses focus.</summary>
    void NameDone()
    {
        if (ArrangeStore.Serialize(_work) == _nameBefore.json) return;
        _undo.Push(_nameBefore);
        _redo.Clear();
        _nameBefore = (ArrangeStore.Serialize(_work), _list.SelectedIndex);
        Changed(keep: _selected);
    }

    /// <summary>
    /// Removes a zone; its neighbour takes the space. The neighbour's contents move into the parent node, so the
    /// neighbour object itself leaves the tree: returns it and the node that now holds it.
    /// </summary>
    static (SplitNode sibling, SplitNode holder) RemoveLeaf(SplitNode root, SplitNode leaf)
    {
        var parent = Parents(root).First(p => p.A == leaf || p.B == leaf);
        var sibling = parent.A == leaf ? parent.B! : parent.A!;
        int gone = leaf.Index;
        parent.Vertical = sibling.Vertical; parent.Ratio = sibling.Ratio;
        parent.A = sibling.A; parent.B = sibling.B; parent.Index = sibling.Index;
        foreach (var (l, _) in ArrangeStore.Leaves(root))
            if (l.Index > gone) l.Index--;
        return (sibling, parent);
    }

    /// <summary>
    /// Makes every column (or row) in the chain of splits around the selected zone the same size; with no zone
    /// selected, every such chain in the layout. A column that is split further counts as one and keeps its insides.
    /// </summary>
    void Space(bool vertical)
    {
        if (Current is not { } root) return;
        var tops = _selected != null
            ? (RunTop(root, vertical) is { } top ? new List<SplitNode> { top } : new List<SplitNode>())
            : RunTops(root, vertical);
        if (tops.Count == 0) return;
        Snapshot();
        int Count(SplitNode n) => !n.IsLeaf && n.Vertical == vertical ? Count(n.A!) + Count(n.B!) : 1;
        void Even(SplitNode n)
        {
            if (n.IsLeaf || n.Vertical != vertical) return;
            n.Ratio = (double)Count(n.A!) / Count(n);
            Even(n.A!);
            Even(n.B!);
        }
        foreach (var t in tops) Even(t);
        Changed(keep: _selected);
    }

    /// <summary>The outermost split of every chain of same-direction splits in the layout.</summary>
    static List<SplitNode> RunTops(SplitNode root, bool vertical)
    {
        var tops = new List<SplitNode>();
        void Walk(SplitNode n, SplitNode? parent)
        {
            if (n.IsLeaf) return;
            if (n.Vertical == vertical && (parent == null || parent.Vertical != vertical)) tops.Add(n);
            Walk(n.A!, n);
            Walk(n.B!, n);
        }
        Walk(root, null);
        return tops;
    }

    /// <summary>The outermost split of the same-direction run nearest the selected zone, or null if there is none.</summary>
    SplitNode? RunTop(SplitNode root, bool vertical)
    {
        if (_selected == null) return null;
        var chain = new List<SplitNode>(); // the selected zone's ancestors, nearest first
        bool Find(SplitNode n)
        {
            if (n == _selected) return true;
            if (n.IsLeaf || !(Find(n.A!) || Find(n.B!))) return false;
            chain.Add(n);
            return true;
        }
        Find(root);
        int i = chain.FindIndex(n => n.Vertical == vertical);
        if (i < 0) return null;
        while (i + 1 < chain.Count && chain[i + 1].Vertical == vertical) i++;
        return chain[i];
    }

    /// <summary>Gives the selected zone pick number <paramref name="index"/> + 1, swapping with the zone that had it.</summary>
    void SetNumber(int index)
    {
        if (Current is not { } root || _selected is not { IsLeaf: true } leaf || leaf.Index == index) return;
        var other = ArrangeStore.Leaves(root).FirstOrDefault(l => l.leaf.Index == index).leaf;
        if (other == null) return; // no zone that high
        Snapshot();
        other.Index = leaf.Index;
        leaf.Index = index;
        Changed(keep: leaf);
    }

    static IEnumerable<SplitNode> Parents(SplitNode n)
    {
        if (n.IsLeaf) yield break;
        yield return n;
        foreach (var p in Parents(n.A!).Concat(Parents(n.B!))) yield return p;
    }

    // ---------- drawing ----------

    void Render()
    {
        _canvas.Children.Clear();
        foreach (var b in new[] { _splitCols, _splitRows, _remove, _evenCols, _evenRows }) b.IsEnabled = false;
        _number.IsEnabled = false;
        _cutBtn.IsEnabled = false;
        _deleteBtn.IsEnabled = _nameBox.IsEnabled = Current != null;
        int zones = Current is { } cur ? ArrangeStore.Leaves(cur).Count : 0;
        _count.Text = Current != null ? zones.ToString() : "–";
        _fewer.IsEnabled = zones > 1;
        _more.IsEnabled = Current != null && zones < Arranger.MaxWindows;
        string name = Current?.Name ?? "";
        if (!_nameBox.IsKeyboardFocused && _nameBox.Text != name)
        {
            bool was = _updating;
            _updating = true;
            _nameBox.Text = name;
            _updating = was;
        }
        if (_drag == null) UpdateStatus(); // it serializes every layout; while dragging, the drop does it once

        if (Current is not { } root)
        {
            var empty = new StackPanel { Width = W, HorizontalAlignment = HorizontalAlignment.Center };
            empty.Children.Add(new TextBlock { Text = "No layouts yet", FontSize = 18, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
            var sub = Caption("Press New layout to start with two zones, then split and resize them.");
            sub.HorizontalAlignment = HorizontalAlignment.Center;
            sub.TextAlignment = TextAlignment.Center;
            empty.Children.Add(sub);
            Canvas.SetTop(empty, H / 2 - 24);
            _canvas.Children.Add(empty);
            _hint.Text = "";
            return;
        }

        var accent = TryFindResource("AccentFillColorDefaultBrush") is SolidColorBrush ab ? ab.Color : SystemParameters.WindowGlassColor;
        int count = ArrangeStore.Leaves(root).Count;

        foreach (var (leaf, r) in ArrangeStore.Leaves(root))
        {
            bool sel = leaf == _selected;
            var box = new Border
            {
                Width = Math.Max(0, r.W * W - 6), Height = Math.Max(0, r.H * H - 6),
                CornerRadius = new CornerRadius(6), Cursor = _cutMode ? Cursors.Cross : Cursors.Hand,
                Background = new SolidColorBrush(Color.FromArgb(sel ? (byte)235 : (byte)140, accent.R, accent.G, accent.B)),
                BorderBrush = sel ? Brushes.White : Brushes.Transparent, BorderThickness = new Thickness(2),
                Child = new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = (leaf.Index + 1).ToString(), FontSize = 26, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White,
                            HorizontalAlignment = HorizontalAlignment.Center,
                        },
                        new TextBlock
                        {
                            Text = $"{Math.Round(r.W * _workW)} × {Math.Round(r.H * _workH)}", FontSize = 12, Foreground = Brushes.White,
                            Opacity = 0.85, HorizontalAlignment = HorizontalAlignment.Center,
                        },
                    },
                },
            };
            Canvas.SetLeft(box, r.X * W + 3);
            Canvas.SetTop(box, r.Y * H + 3);
            var l = leaf; var lr = r;
            box.MouseLeftButtonDown += (_, e) =>
            {
                if (_cutMode) Cut(l, lr, e.GetPosition(_canvas));
                else { _selected = l; Render(); }
            };
            _canvas.Children.Add(box);
        }

        // Dividers go on top, so they win over the zones they separate.
        void Dividers(SplitNode n, NRect r)
        {
            if (n.IsLeaf) return;
            var (a, b) = ArrangeStore.Halves(n, r);
            // A short pill marks the divider; it stretches along the whole line while hovered or dragged.
            double length = n.Vertical ? r.H * H : r.W * W;
            var pill = new Border
            {
                Background = Brushes.White, CornerRadius = new CornerRadius(3), IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            void Pill(bool hot)
            {
                double along = hot ? length - 10 : Math.Min(44, length - 10), across = hot ? 6 : 4;
                pill.Width = n.Vertical ? across : along;
                pill.Height = n.Vertical ? along : across;
                pill.Opacity = hot ? 1 : 0.8;
            }
            var bar = new Border
            {
                Background = Brushes.Transparent, Cursor = n.Vertical ? Cursors.SizeWE : Cursors.SizeNS, Child = pill,
                Width = n.Vertical ? Hit : r.W * W, Height = n.Vertical ? r.H * H : Hit,
                ToolTip = "Drag to resize",
            };
            Pill(_drag?.node == n);
            bar.MouseEnter += (_, _) => Pill(true);
            bar.MouseLeave += (_, _) => Pill(_drag?.node == n);
            Canvas.SetLeft(bar, n.Vertical ? b.X * W - Hit / 2 : r.X * W);
            Canvas.SetTop(bar, n.Vertical ? r.Y * H : b.Y * H - Hit / 2);
            var node = n; var rect = r;
            bar.MouseLeftButtonDown += (_, e) =>
            {
                _dragBefore = (ArrangeStore.Serialize(_work), _list.SelectedIndex); // one undo step per drag
                _drag = (node, rect);
                _dragStart = node.Ratio;
                _canvas.CaptureMouse();
                e.Handled = true;
            };
            _canvas.Children.Add(bar);
            Dividers(n.A!, a);
            Dividers(n.B!, b);
        }
        Dividers(root, new NRect(0, 0, 1, 1));

        bool picked = _selected != null;
        _splitCols.IsEnabled = _splitRows.IsEnabled = picked && count < Arranger.MaxWindows;
        _remove.IsEnabled = picked && count > 1;
        _number.IsEnabled = picked;
        bool wasUpdating = _updating;
        _updating = true;
        if (_number.Items.Count != count)
        {
            _number.Items.Clear();
            for (int i = 1; i <= count; i++) _number.Items.Add($"#{i}");
        }
        _number.SelectedIndex = picked ? _selected!.Index : -1;
        _updating = wasUpdating;
        _evenCols.IsEnabled = picked ? RunTop(root, vertical: true) != null : RunTops(root, vertical: true).Count > 0;
        _evenRows.IsEnabled = picked ? RunTop(root, vertical: false) != null : RunTops(root, vertical: false).Count > 0;

        _cutBtn.IsEnabled = true;
        _hint.Text = _cutMode
            ? count >= Arranger.MaxWindows
                ? "Cutting: this layout already has 6 zones, the most it can have. Press C or Esc to stop cutting."
                : "Cutting: click a zone to cut it at the dashed line. Hold Shift to cut top and bottom. Press C or Esc to stop."
            : picked
            ? $"Zone #{_selected!.Index + 1} selected. Press 1-{count} to change its number. In auto-arrange, #1 is the first window you pick."
            : count >= Arranger.MaxWindows
                ? "Click a zone to select it. Drag the white handles to resize. (6 zones is the most a layout can have.)"
                : "Click a zone to select it, then split it. Drag the white handles between zones to resize. Press C to cut freely.";
        ShowCut();
    }

    static object Tip(string title, string detail)
    {
        var t = new StackPanel { MaxWidth = 280 };
        t.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
        t.Children.Add(new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        return t;
    }

    void SetCut(bool on)
    {
        _cutMode = on;
        if (on) _selected = null; // the selection tools don't apply while cutting
        if (on) _canvasCard.BorderBrush = TryFindResource("AccentFillColorDefaultBrush") as Brush ?? Brushes.DodgerBlue;
        else _canvasCard.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        Render();
    }

    static bool ShiftDown => Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);

    /// <summary>Where along the zone a cut at <paramref name="p"/> lands, snapped to quarters and thirds when close.</summary>
    double CutRatio(NRect r, Point p, bool vertical)
    {
        double v = vertical ? (p.X / W - r.X) / r.W : (p.Y / H - r.Y) / r.H;
        foreach (var snap in new[] { 0.25, 1 / 3.0, 0.5, 2 / 3.0, 0.75 })
            if (Math.Abs(v - snap) < 0.03) v = snap;
        return Math.Clamp(v, 0.1, 0.9);
    }

    /// <summary>Draws the dashed cut line through the zone under the pointer (or hides it).</summary>
    void ShowCut()
    {
        _canvas.Children.Remove(_cutLine);
        if (!_cutMode || _cutAt is not { } p || Current is not { } root || _drag != null) return;
        var leaves = ArrangeStore.Leaves(root);
        if (leaves.Count >= Arranger.MaxWindows) return;
        var (leaf, r) = leaves.FirstOrDefault(l => l.rect.Contains(p.X / W, p.Y / H));
        if (leaf == null) return;
        bool vertical = !ShiftDown;
        double k = CutRatio(r, p, vertical);
        if (vertical)
        {
            _cutLine.X1 = _cutLine.X2 = (r.X + r.W * k) * W;
            _cutLine.Y1 = r.Y * H + 6;
            _cutLine.Y2 = (r.Y + r.H) * H - 6;
        }
        else
        {
            _cutLine.Y1 = _cutLine.Y2 = (r.Y + r.H * k) * H;
            _cutLine.X1 = r.X * W + 6;
            _cutLine.X2 = (r.X + r.W) * W - 6;
        }
        _canvas.Children.Add(_cutLine);
    }

    void Cut(SplitNode leaf, NRect r, Point p)
    {
        if (Current is not { } root) return;
        int count = ArrangeStore.Leaves(root).Count;
        if (count >= Arranger.MaxWindows) return;
        bool vertical = !ShiftDown;
        Snapshot();
        leaf.Vertical = vertical;
        leaf.Ratio = CutRatio(r, p, vertical);
        leaf.A = new SplitNode { Index = leaf.Index };
        leaf.B = new SplitNode { Index = count };
        Changed(keep: null);
    }

    void EndDrag()
    {
        if (_drag == null) return;
        var node = _drag.Value.node;
        _drag = null; // first: releasing capture below raises LostMouseCapture, which comes back here
        _canvas.ReleaseMouseCapture();
        if (node.Ratio == _dragStart) { Render(); return; } // a click on a handle isn't an edit
        _undo.Push(_dragBefore);
        _redo.Clear();
        Changed(keep: _selected);
    }

    void OnDragMove(object sender, MouseEventArgs e)
    {
        if (_drag is not { } d || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(_canvas);
        double v = d.node.Vertical ? (p.X / W - d.rect.X) / d.rect.W : (p.Y / H - d.rect.Y) / d.rect.H;
        foreach (var snap in new[] { 0.25, 1 / 3.0, 0.5, 2 / 3.0, 0.75 })
            if (Math.Abs(v - snap) < 0.02) v = snap;
        d.node.Ratio = Math.Clamp(v, 0.1, 0.9);
        Render();
    }
}
