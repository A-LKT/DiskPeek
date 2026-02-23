using System.Diagnostics;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DiskPeek.Converters;
using DiskPeek.Models;

namespace DiskPeek.Controls;

/// <summary>
/// A custom FrameworkElement that renders a squarified treemap of a FileSystemNode tree.
/// Uses WPF's DrawingVisual pipeline for high-performance rendering.
/// </summary>
public class TreemapControl : FrameworkElement
{
    // ── Dependency Properties ────────────────────────────────────────────────

    public static readonly DependencyProperty RootNodeProperty =
        DependencyProperty.Register(nameof(RootNode), typeof(FileSystemNode), typeof(TreemapControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender,
                (d, _) => ((TreemapControl)d).Rebuild()));

    public FileSystemNode? RootNode
    {
        get => (FileSystemNode?)GetValue(RootNodeProperty);
        set => SetValue(RootNodeProperty, value);
    }

    public static readonly DependencyProperty MaxChildrenProperty =
        DependencyProperty.Register(nameof(MaxChildren), typeof(int), typeof(TreemapControl),
            new FrameworkPropertyMetadata(0, (d, _) => ((TreemapControl)d).Rebuild()));

    /// <summary>Maximum number of children to render. 0 = render all (sorted by size when limited).</summary>
    public int MaxChildren
    {
        get => (int)GetValue(MaxChildrenProperty);
        set => SetValue(MaxChildrenProperty, value);
    }

    // ── Events ───────────────────────────────────────────────────────────────

    public event Action<FileSystemNode>? NodeDoubleClicked;
    public event Action? NavigateUpRequested;

    // ── Internal state ───────────────────────────────────────────────────────

    private readonly VisualCollection _visuals;
    // hit map: rectangle → node, index → visual
    private readonly List<(Rect Rect, FileSystemNode Node, DrawingVisual Visual)> _hitMap = [];

    private FileSystemNode? _hoveredNode;
    private DrawingVisual? _hoveredVisual;
    private Rect _hoveredRect;
    private Rect _explorerIconRect = Rect.Empty;

    private readonly Popup _tooltip;
    private readonly System.Windows.Controls.TextBlock _tooltipText;

    // Directory color palette – 12 distinct hues
    private static readonly Color[] DirPalette =
    [
        Color.FromRgb(0x4E, 0x9A, 0xF0), // blue
        Color.FromRgb(0x5C, 0xC8, 0x5A), // green
        Color.FromRgb(0xF0, 0x7A, 0x4E), // orange
        Color.FromRgb(0xA8, 0x6E, 0xF0), // purple
        Color.FromRgb(0xF0, 0xC8, 0x4E), // yellow
        Color.FromRgb(0x4E, 0xC8, 0xD0), // teal
        Color.FromRgb(0xF0, 0x4E, 0x88), // pink
        Color.FromRgb(0x6B, 0xE8, 0xAF), // mint
        Color.FromRgb(0xE0, 0x6B, 0x6B), // red
        Color.FromRgb(0x6B, 0xA8, 0xE8), // sky
        Color.FromRgb(0xE8, 0xB8, 0x6B), // gold
        Color.FromRgb(0x90, 0xE8, 0x6B), // lime
    ];

    private static readonly Color FileColor = Color.FromRgb(0x55, 0x66, 0x88);
    private static readonly Color HoverOverlay = Color.FromArgb(60, 255, 255, 255);
    private static readonly Color BorderColor = Color.FromRgb(0x0F, 0x0F, 0x1A);

    private static readonly Pen BorderPen = new(new SolidColorBrush(BorderColor), 1.5)
        { LineJoin = PenLineJoin.Miter };

    /// <summary>Dashed border used for directories that haven't been scanned at full depth.</summary>
    private static readonly Pen PartialBorderPen = new(
        new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)), 1.5)
        { LineJoin = PenLineJoin.Miter, DashStyle = DashStyles.Dot };

    private static readonly Typeface LabelTypeface =
        new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private static readonly Typeface SmallTypeface =
        new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    // ── Constructor ──────────────────────────────────────────────────────────

    public TreemapControl()
    {
        _visuals = new VisualCollection(this);

        // Tooltip popup
        _tooltipText = new System.Windows.Controls.TextBlock
        {
            Foreground = Brushes.White,
            Padding = new Thickness(8, 4, 8, 4),
            FontSize = 12,
            MaxWidth = 320,
            TextWrapping = System.Windows.TextWrapping.Wrap,
        };
        _tooltip = new Popup
        {
            Child = new System.Windows.Controls.Border
            {
                Child = _tooltipText,
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Color.FromArgb(220, 20, 20, 40)),
            },
            AllowsTransparency = true,
            Placement = PlacementMode.Absolute,
            IsOpen = false,
        };

        ClipToBounds = true;
        SizeChanged += (_, _) => Rebuild();
    }

    /// <summary>
    /// Forces a full re-render. Call this when the root node's Children have been
    /// updated in-place (e.g. after a deeper scan), since the DependencyProperty
    /// value reference hasn't changed so the normal callback won't fire.
    /// </summary>
    public void ForceRefresh() => Rebuild();

    // ── Visual tree overrides ────────────────────────────────────────────────

    protected override int VisualChildrenCount => _visuals.Count;
    protected override Visual GetVisualChild(int index) => _visuals[index];

    // ── Rendering ────────────────────────────────────────────────────────────

    private void Rebuild()
    {
        _visuals.Clear();
        _hitMap.Clear();
        _hoveredNode = null;
        _hoveredVisual = null;
        _tooltip.IsOpen = false;

        var root = RootNode;
        if (root is null || ActualWidth < 4 || ActualHeight < 4) return;

        var children = root.Children.Where(n => n.Size > 0).ToList();
        if (MaxChildren > 0 && children.Count > MaxChildren)
            children = children.OrderByDescending(n => n.Size).Take(MaxChildren).ToList();
        if (children.Count == 0) return;

        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        long totalSize = children.Sum(n => n.Size);
        double containerArea = bounds.Width * bounds.Height;

        // Normalize: map each node's size to its proportional area
        var normalized = children
            .Select(n => ((double)n.Size / totalSize * containerArea, n))
            .ToList();

        var layout = new List<(Rect Rect, FileSystemNode Node)>();
        SquarifyImpl(normalized, bounds, layout);

        int colorIdx = 0;
        foreach (var (rect, node) in layout)
        {
            if (rect.Width < 1 || rect.Height < 1) continue;

            Color baseColor = node.IsDirectory
                ? DirPalette[colorIdx++ % DirPalette.Length]
                : FileColor;

            var visual = CreateNodeVisual(rect, node, baseColor, false);
            _visuals.Add(visual);
            _hitMap.Add((rect, node, visual));
        }
    }

    private DrawingVisual CreateNodeVisual(Rect rect, FileSystemNode node, Color baseColor, bool hovered)
    {
        var visual = new DrawingVisual();
        using var ctx = visual.RenderOpen();

        // Inset slightly for the border gap effect
        var inset = new Rect(rect.X + 1, rect.Y + 1, Math.Max(1, rect.Width - 2), Math.Max(1, rect.Height - 2));

        var fill = hovered
            ? MixColors(baseColor, HoverOverlay)
            : baseColor;

        // Partially-loaded directories get a dotted white border to signal "more inside"
        bool isPartial = node.IsDirectory && !node.IsFullyLoaded;
        var borderPen  = isPartial ? PartialBorderPen : BorderPen;

        ctx.DrawRectangle(new SolidColorBrush(fill), borderPen, inset);

        if (hovered)
            ctx.DrawRectangle(new SolidColorBrush(HoverOverlay), null, inset);

        // Label rendering: only if the rect is large enough
        double w = inset.Width, h = inset.Height;
        if (w >= 30 && h >= 18)
        {
            // Directory indicator bar
            if (node.IsDirectory && h >= 22)
            {
                var headerH    = Math.Min(22.0, h * 0.25);
                var headerRect = new Rect(inset.X, inset.Y, w, headerH);
                ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(80, 0, 0, 0)), null, headerRect);
            }

            // Name label
            string displayName = node.Name;
            var nameText = MakeFormattedText(displayName, LabelTypeface, 11, Brushes.White, w - 6);
            double textX = inset.X + 3;
            double textY = inset.Y + 3;

            if (nameText.Width <= w - 6 && nameText.Height <= h - 4)
            {
                ctx.DrawText(nameText, new Point(textX, textY));

                // Size label below name
                if (h >= 34)
                {
                    string sizeStr = FileSizeConverter.FormatBytes(node.Size);
                    var sizeText = MakeFormattedText(sizeStr, SmallTypeface, 10,
                        new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), w - 6);
                    double sizeY = textY + nameText.Height + 1;
                    if (sizeY + sizeText.Height <= inset.Bottom - 2)
                        ctx.DrawText(sizeText, new Point(textX, sizeY));
                }
            }

            // "···" badge in bottom-right corner for partially-loaded directories
            if (isPartial && w >= 36 && h >= 24)
            {
                var badge = MakeFormattedText("···", SmallTypeface, 11,
                    new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), w - 4);
                ctx.DrawText(badge, new Point(inset.Right - badge.Width - 3, inset.Bottom - badge.Height - 2));
            }
        }

        // "Open in Explorer" icon — top-right corner, only on hover
        if (hovered)
        {
            var iconRect = GetExplorerIconRect(rect);
            if (!iconRect.IsEmpty)
            {
                ctx.DrawRoundedRectangle(
                    new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)), null, iconRect, 3, 3);
                var arrow = MakeFormattedText("↗", LabelTypeface, 14, Brushes.White, iconRect.Width);
                ctx.DrawText(arrow, new Point(
                    iconRect.X + (iconRect.Width  - arrow.Width)  / 2,
                    iconRect.Y + (iconRect.Height - arrow.Height) / 2));
            }
        }

        return visual;
    }

    private static double GetPixelsPerDip()
    {
        if (Application.Current?.MainWindow is { } win)
            return VisualTreeHelper.GetDpi(win).PixelsPerDip;
        return 1.0;
    }

    private static FormattedText MakeFormattedText(string text, Typeface face, double size,
        Brush foreground, double maxWidth)
    {
        var ft = new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, face, size, foreground, GetPixelsPerDip())
        {
            MaxTextWidth = Math.Max(1, maxWidth),
            Trimming = TextTrimming.CharacterEllipsis,
            MaxLineCount = 1,
        };
        return ft;
    }

    private static Color MixColors(Color base_, Color overlay)
    {
        double a = overlay.A / 255.0;
        return Color.FromRgb(
            (byte)(base_.R * (1 - a) + overlay.R * a),
            (byte)(base_.G * (1 - a) + overlay.G * a),
            (byte)(base_.B * (1 - a) + overlay.B * a));
    }

    // ── Mouse handling ───────────────────────────────────────────────────────

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var pos = e.GetPosition(this);
        var hit = FindHit(pos);

        // Always keep the popup anchored to the cursor
        if (hit != null)
        {
            var screen = PointToScreen(pos);
            _tooltip.HorizontalOffset = screen.X + 14;
            _tooltip.VerticalOffset   = screen.Y + 14;
        }

        // Update cursor: hand when the mouse is over the open-in-explorer icon
        Cursor = _hoveredNode != null && !_explorerIconRect.IsEmpty && _explorerIconRect.Contains(pos)
            ? Cursors.Hand : Cursors.Arrow;

        if (hit?.Node == _hoveredNode) return;

        // Restore old hovered visual
        if (_hoveredNode != null && _hoveredVisual != null)
        {
            int idx = _hitMap.FindIndex(h => h.Node == _hoveredNode);
            if (idx >= 0 && idx < _visuals.Count)
            {
                var (oldRect, oldNode, _) = _hitMap[idx];
                var restored = CreateNodeVisual(oldRect, oldNode,
                    GetNodeColor(idx), false);
                ReplaceVisual(idx, restored);
                _hitMap[idx] = (oldRect, oldNode, restored);
            }
        }

        _hoveredNode = hit?.Node;
        _hoveredVisual = null;

        if (hit is { } h2)
        {
            int idx = _hitMap.FindIndex(x => x.Node == h2.Node);
            if (idx >= 0 && idx < _visuals.Count)
            {
                var (rect, node, _) = _hitMap[idx];
                var highlighted = CreateNodeVisual(rect, node, GetNodeColor(idx), true);
                ReplaceVisual(idx, highlighted);
                _hitMap[idx] = (rect, node, highlighted);
                _hoveredVisual = highlighted;
                _hoveredRect = rect;
                _explorerIconRect = GetExplorerIconRect(rect);
            }

            // Update tooltip
            _tooltipText.Text =
                $"{h2.Node.Name}\n{FileSizeConverter.FormatBytes(h2.Node.Size)}" +
                (h2.Node.IsDirectory ? $"  ·  {h2.Node.FileCount:N0} files" : string.Empty);
            _tooltip.IsOpen = true;
        }
        else
        {
            _tooltip.IsOpen = false;
            _explorerIconRect = Rect.Empty;
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _tooltip.IsOpen = false;
        _explorerIconRect = Rect.Empty;
        Cursor = Cursors.Arrow;

        if (_hoveredNode != null)
        {
            int idx = _hitMap.FindIndex(h => h.Node == _hoveredNode);
            if (idx >= 0 && idx < _visuals.Count)
            {
                var (rect, node, _) = _hitMap[idx];
                var restored = CreateNodeVisual(rect, node, GetNodeColor(idx), false);
                ReplaceVisual(idx, restored);
                _hitMap[idx] = (rect, node, restored);
            }
            _hoveredNode = null;
            _hoveredVisual = null;
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var pos = e.GetPosition(this);

        // Explorer icon takes priority over double-click drill-down
        if (_hoveredNode != null && !_explorerIconRect.IsEmpty && _explorerIconRect.Contains(pos))
        {
            if (e.ClickCount == 1)
                OpenInExplorer(_hoveredNode);
            e.Handled = true;
            return;
        }

        if (e.ClickCount < 2) return;
        var hit = FindHit(pos);
        if (hit.HasValue && hit.Value.Node.IsDirectory)
            NodeDoubleClicked?.Invoke(hit.Value.Node);
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        NavigateUpRequested?.Invoke();
        e.Handled = true;
    }

    private (Rect Rect, FileSystemNode Node)? FindHit(Point pos)
    {
        // Iterate in reverse so top-most (last drawn) wins
        for (int i = _hitMap.Count - 1; i >= 0; i--)
        {
            if (_hitMap[i].Rect.Contains(pos))
                return (_hitMap[i].Rect, _hitMap[i].Node);
        }
        return null;
    }

    private Color GetNodeColor(int hitMapIndex)
    {
        // Recalculate color index by counting directory predecessors
        int colorIdx = 0;
        for (int i = 0; i < hitMapIndex; i++)
            if (_hitMap[i].Node.IsDirectory) colorIdx++;

        var node = _hitMap[hitMapIndex].Node;
        return node.IsDirectory ? DirPalette[colorIdx % DirPalette.Length] : FileColor;
    }

    /// <summary>Returns the hit-rect for the "Open in Explorer" icon on a tile, or Rect.Empty if the tile is too small.</summary>
    private static Rect GetExplorerIconRect(Rect tileRect)
    {
        var inset = new Rect(tileRect.X + 1, tileRect.Y + 1,
                             Math.Max(1, tileRect.Width - 2), Math.Max(1, tileRect.Height - 2));
        if (inset.Width < 48 || inset.Height < 24) return Rect.Empty;
        const double sz = 17, margin = 3;
        return new Rect(inset.Right - sz - margin, inset.Y + margin, sz, sz);
    }

    private static void OpenInExplorer(FileSystemNode node)
    {
        // Root drive: open it directly; everything else: select in its parent folder
        string args = node.Parent is null
            ? $"\"{node.FullPath}\""
            : $"/select,\"{node.FullPath}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
    }

    /// <summary>
    /// Replaces the visual at <paramref name="index"/> in the VisualCollection.
    /// VisualCollection's indexer setter throws if the slot is occupied, so we must
    /// RemoveAt then Insert rather than assigning directly.
    /// </summary>
    private void ReplaceVisual(int index, DrawingVisual newVisual)
    {
        _visuals.RemoveAt(index);
        _visuals.Insert(index, newVisual);
    }

    // ── Squarified Treemap Algorithm ─────────────────────────────────────────

    private static void SquarifyImpl(
        List<(double Area, FileSystemNode Node)> items,
        Rect bounds,
        List<(Rect, FileSystemNode)> result)
    {
        if (items.Count == 0 || bounds.Width < 1 || bounds.Height < 1) return;

        var row = new List<(double Area, FileSystemNode Node)>();
        double rowArea = 0;
        double shortEdge = Math.Min(bounds.Width, bounds.Height);

        for (int i = 0; i < items.Count;)
        {
            var item = items[i];
            if (item.Area <= 0) { i++; continue; }

            double newRowArea = rowArea + item.Area;
            if (row.Count == 0 ||
                WorstAspectRatio([.. row, item], newRowArea, shortEdge) <=
                WorstAspectRatio(row, rowArea, shortEdge))
            {
                row.Add(item);
                rowArea = newRowArea;
                i++;
            }
            else
            {
                bounds = LayoutRow(row, rowArea, bounds, result);
                shortEdge = Math.Min(bounds.Width, bounds.Height);
                row.Clear();
                rowArea = 0;
            }
        }

        if (row.Count > 0)
            LayoutRow(row, rowArea, bounds, result);
    }

    private static double WorstAspectRatio(
        List<(double Area, FileSystemNode Node)> row, double rowTotal, double shortEdge)
    {
        if (row.Count == 0 || rowTotal <= 0 || shortEdge <= 0) return double.MaxValue;
        double stripWidth = rowTotal / shortEdge;
        double worst = 0;
        foreach (var (area, _) in row)
        {
            if (area <= 0) continue;
            double h = area / stripWidth;
            double ratio = stripWidth > h ? stripWidth / h : h / stripWidth;
            if (ratio > worst) worst = ratio;
        }
        return worst;
    }

    private static Rect LayoutRow(
        List<(double Area, FileSystemNode Node)> row, double rowArea,
        Rect bounds, List<(Rect, FileSystemNode)> result)
    {
        double w = bounds.Width, h = bounds.Height;

        if (w >= h)
        {
            // Wide container: strip on the left, items stacked top-to-bottom
            double stripW = rowArea / h;
            double y = bounds.Y;
            foreach (var (area, node) in row)
            {
                double itemH = area / stripW;
                result.Add((new Rect(bounds.X, y, stripW, itemH), node));
                y += itemH;
            }
            return new Rect(bounds.X + stripW, bounds.Y, Math.Max(0, w - stripW), h);
        }
        else
        {
            // Tall container: strip on top, items left-to-right
            double stripH = rowArea / w;
            double x = bounds.X;
            foreach (var (area, node) in row)
            {
                double itemW = area / stripH;
                result.Add((new Rect(x, bounds.Y, itemW, stripH), node));
                x += itemW;
            }
            return new Rect(bounds.X, bounds.Y + stripH, w, Math.Max(0, h - stripH));
        }
    }
}
