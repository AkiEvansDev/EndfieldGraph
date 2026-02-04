using EndfieldGraph.Models;
using EndfieldGraph.Services.Helpers;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace EndfieldGraph.Views.Controls;

public partial class ResourceGraphView : UserControl
{
    public static readonly DependencyProperty LayoutProperty =
        DependencyProperty.Register(
            nameof(Layout),
            typeof(ResourceGraphLayout),
            typeof(ResourceGraphView),
            new PropertyMetadata(null, OnLayoutChanged));

    public ResourceGraphLayout? Layout
    {
        get => (ResourceGraphLayout?)GetValue(LayoutProperty);
        set => SetValue(LayoutProperty, value);
    }

    private const double NodeRadius = 34;
    private const double NodeDiameter = NodeRadius * 2;
    private const double NodeLabelWidth = 80;
    private const double EdgeStroke = 2.0;

    private readonly Dictionary<Guid, FrameworkElement> nodeVisualById = [];
    private readonly Dictionary<Guid, FrameworkElement> badgesByNodeId = [];
    private readonly Dictionary<Guid, FrameworkElement> outLabelByFromId = [];

    private readonly List<FrameworkElement> edgeVisuals = [];
    private readonly Dictionary<(Guid From, Guid To), Path> edgePathByKey = [];
    private readonly Dictionary<Guid, List<Path>> edgePathsByNode = [];

    private bool isPanning;
    private Point panStartMouse;
    private Point panStartTranslate;

    public ResourceGraphView()
    {
        InitializeComponent();

        Loaded += (_, __) =>
        {
            RedrawAll();
            FitToContent();
        };
    }

    private static void OnLayoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (ResourceGraphView)d;
        c.HookLayoutChanged(e.OldValue as ResourceGraphLayout, e.NewValue as ResourceGraphLayout);
        c.RedrawAll();
        c.FitToContent();
    }

    private void HookLayoutChanged(ResourceGraphLayout? oldLayout, ResourceGraphLayout? newLayout)
    {
        if (oldLayout is not null)
        {
            oldLayout.Nodes.CollectionChanged -= OnLayoutCollectionChanged;
            oldLayout.Edges.CollectionChanged -= OnLayoutCollectionChanged;
            oldLayout.OutLabels.CollectionChanged -= OnLayoutCollectionChanged;
        }

        if (newLayout is not null)
        {
            newLayout.Nodes.CollectionChanged += OnLayoutCollectionChanged;
            newLayout.Edges.CollectionChanged += OnLayoutCollectionChanged;
            newLayout.OutLabels.CollectionChanged += OnLayoutCollectionChanged;
        }
    }

    private void OnLayoutCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RedrawAll();
    }

    private void RedrawAll()
    {
        SceneCanvas.Children.Clear();

        nodeVisualById.Clear();
        badgesByNodeId.Clear();
        outLabelByFromId.Clear();

        edgeVisuals.Clear();
        edgePathByKey.Clear();
        edgePathsByNode.Clear();

        if (Layout is null) return;

        foreach (var edge in Layout.Edges)
            DrawEdge(edge);

        foreach (var node in Layout.Nodes)
        {
            DrawNode(node);
            DrawNodeBadges(node);
        }

        DrawOutLabels();
    }

    private void DrawNode(ResourceGraphNode node)
    {
        var container = new Canvas
        {
            Width = Math.Max(NodeLabelWidth, NodeDiameter),
            Height = NodeDiameter + 52,
            IsHitTestVisible = true
        };

        container.MouseEnter += (_, __) =>
        {
            if (badgesByNodeId.TryGetValue(node.Id, out var b))
                b.Visibility = Visibility.Visible;

            HighlightForNode(node.Id);
        };

        container.MouseLeave += (_, __) =>
        {
            if (badgesByNodeId.TryGetValue(node.Id, out var b))
                b.Visibility = Visibility.Collapsed;

            ResetHighlight();
        };

        var ellipse = new Ellipse
        {
            Width = NodeDiameter,
            Height = NodeDiameter,
            StrokeThickness = 1.5,
            Stroke = (Brush)FindResource("TextFillColorSecondaryBrush"),
            Fill = (Brush)FindResource("ControlFillColorDefaultBrush")
        };

        if (node.Icon is not null)
        {
            ellipse.Fill = new ImageBrush(IconPngConverter.ToBitmapImage(node.Icon))
            {
                Stretch = Stretch.UniformToFill,
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center
            };
        }

        Canvas.SetLeft(ellipse, (container.Width - NodeDiameter) / 2);
        Canvas.SetTop(ellipse, 0);
        container.Children.Add(ellipse);

        var tb = new TextBlock
        {
            Width = container.Width,
            MaxWidth = container.Width,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.None,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            LineHeight = 14,
            Margin = new Thickness(0, NodeDiameter, 0, 0),
            Padding = new Thickness(0, 6, 0, 0),
            Text = node.Name,
            Foreground = (Brush)FindResource("TextFillColorPrimaryBrush"),
            MaxHeight = 48
        };

        container.Children.Add(tb);

        var left = node.Position.X - container.Width / 2;
        var top = node.Position.Y - NodeRadius;

        Canvas.SetLeft(container, left);
        Canvas.SetTop(container, top);

        SceneCanvas.Children.Add(container);
        nodeVisualById[node.Id] = container;
    }

    private void DrawNodeBadges(ResourceGraphNode node)
    {
        if (Layout is null) return;

        var incoming = Layout.Edges
            .Where(e => e.ToId == node.Id)
            .ToList();

        if (incoming.Count == 0)
            return;

        var containerWidth = Math.Max(NodeLabelWidth, NodeDiameter);

        var nodeLeft = node.Position.X - containerWidth / 2;
        var nodeTop = node.Position.Y - NodeRadius;

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };

        var width = 0.0;
        var height = 0.0;
        foreach (var edge in incoming.OrderByDescending(e => e.NeedCount))
        {
            var child = Layout.Nodes.FirstOrDefault(n => n.Id == edge.FromId);

            var chip = new Border
            {
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                BorderBrush = (Brush)FindResource("ControlStrokeColorDefaultBrush"),
                Background = (Brush)FindResource("ControlFillColorDefaultBrush"),
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(4, 0, 0, 0),
            };

            var chipContent = new StackPanel { Orientation = Orientation.Horizontal };

            if (child?.Icon is not null)
            {
                chipContent.Children.Add(new Image
                {
                    Source = IconPngConverter.ToBitmapImage(child.Icon),
                    Width = 16,
                    Height = 16,
                    Stretch = Stretch.UniformToFill,
                    Margin = new Thickness(0, 0, 4, 0),
                });
            }

            chipContent.Children.Add(new TextBlock
            {
                Text = $"×{edge.NeedCount}",
                Foreground = (Brush)FindResource("TextFillColorPrimaryBrush"),
                VerticalAlignment = VerticalAlignment.Center,
            });

            chip.Child = chipContent;
            panel.Children.Add(chip);

            chip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            width += chip.DesiredSize.Width;
            height = Math.Max(height, chip.DesiredSize.Height);
        }

        var badgesX = node.Position.X - width / 2 - 4;
        var badgesY = node.Position.Y - NodeRadius - height - 8;

        Canvas.SetLeft(panel, badgesX);
        Canvas.SetTop(panel, badgesY);

        SceneCanvas.Children.Add(panel);
        edgeVisuals.Add(panel);

        badgesByNodeId[node.Id] = panel;
    }

    private void DrawEdge(ResourceGraphEdge edge)
    {
        if (Layout is null) return;

        var from = Layout.Nodes.FirstOrDefault(n => n.Id == edge.FromId);
        var to = Layout.Nodes.FirstOrDefault(n => n.Id == edge.ToId);
        if (from is null || to is null) return;

        var start = new Point(from.Position.X - NodeRadius, from.Position.Y);
        var end = new Point(to.Position.X + NodeRadius, to.Position.Y);

        const double bendOffset = 26;
        var bendX = end.X + bendOffset;

        bendX = Math.Min(bendX, start.X - 20);

        var fig = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
        fig.Segments.Add(new LineSegment(new Point(bendX, start.Y), true));
        fig.Segments.Add(new LineSegment(new Point(bendX, end.Y), true));
        fig.Segments.Add(new LineSegment(end, true));

        var geometry = new PathGeometry();
        geometry.Figures.Add(fig);

        var path = new Path
        {
            Data = geometry,
            Stroke = (Brush)FindResource("TextFillColorSecondaryBrush"),
            StrokeThickness = EdgeStroke,
            SnapsToDevicePixels = true
        };

        SceneCanvas.Children.Add(path);
        edgeVisuals.Add(path);

        edgePathByKey[(edge.FromId, edge.ToId)] = path;

        if (!edgePathsByNode.TryGetValue(edge.FromId, out var listFrom))
            edgePathsByNode[edge.FromId] = listFrom = [];
        listFrom.Add(path);

        if (!edgePathsByNode.TryGetValue(edge.ToId, out var listTo))
            edgePathsByNode[edge.ToId] = listTo = [];
        listTo.Add(path);
    }

    private void DrawOutLabels()
    {
        if (Layout is null) return;

        foreach (var ol in Layout.OutLabels)
        {
            var from = Layout.Nodes.FirstOrDefault(n => n.Id == ol.FromId);
            if (from is null) continue;

            var start = new Point(from.Position.X - NodeRadius, from.Position.Y);

            var isLeaf = !Layout.Edges.Any(e => e.ToId == ol.FromId);
            var labelText = isLeaf
                ? $"×{ol.NeedCount}"
                : $"×{ol.NeedCount}  ⏱{ol.TimeSeconds}s";

            var label = new Border
            {
                Background = (Brush)FindResource("ControlFillColorDefaultBrush"),
                BorderBrush = (Brush)FindResource("ControlStrokeColorDefaultBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 4, 8, 4),
                Child = new TextBlock
                {
                    Text = labelText,
                    Foreground = (Brush)FindResource("TextFillColorPrimaryBrush"),
                }
            };

            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            const double labelPadX = 10;

            var labelX = start.X - label.DesiredSize.Width - labelPadX;
            var labelY = start.Y - label.DesiredSize.Height;

            Canvas.SetLeft(label, labelX);
            Canvas.SetTop(label, labelY);

            SceneCanvas.Children.Add(label);
            edgeVisuals.Add(label);

            outLabelByFromId[ol.FromId] = label;
        }
    }

    private void OnViewportMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var zoomDelta = e.Delta > 0 ? 1.12 : 1 / 1.12;

        var worldBefore = ScreenToWorld(e.GetPosition((IInputElement)sender));

        ZoomTransform.ScaleX = Clamp(ZoomTransform.ScaleX * zoomDelta, 0.2, 3.5);
        ZoomTransform.ScaleY = ZoomTransform.ScaleX;

        var worldAfter = ScreenToWorld(e.GetPosition((IInputElement)sender));
        var worldDelta = worldAfter - worldBefore;

        PanTransform.X += worldDelta.X * ZoomTransform.ScaleX;
        PanTransform.Y += worldDelta.Y * ZoomTransform.ScaleY;
    }

    private void OnViewportMouseDown(object sender, MouseButtonEventArgs e)
    {
        Focus();

        if (e.ChangedButton == MouseButton.Left)
        {
            if (e.OriginalSource is DependencyObject d && FindParentNodeContainer(d) is not null)
                return;

            isPanning = true;
            panStartMouse = e.GetPosition(this);
            panStartTranslate = new Point(PanTransform.X, PanTransform.Y);
            Mouse.Capture((IInputElement)sender);
            e.Handled = true;
        }
    }

    private void OnViewportMouseMove(object sender, MouseEventArgs e)
    {
        if (!isPanning) return;

        var cur = e.GetPosition(this);
        var delta = cur - panStartMouse;

        PanTransform.X = panStartTranslate.X + delta.X;
        PanTransform.Y = panStartTranslate.Y + delta.Y;
    }

    private void OnViewportMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!isPanning) return;

        isPanning = false;
        Mouse.Capture(null);
    }

    private Canvas? FindParentNodeContainer(DependencyObject src)
    {
        DependencyObject? cur = src;
        while (cur is not null)
        {
            if (cur is Canvas c && nodeVisualById.ContainsValue(c))
                return c;

            cur = VisualTreeHelper.GetParent(cur);
        }

        return null;
    }

    private Point ScreenToWorld(Point screen)
    {
        var s = ZoomTransform.ScaleX;
        if (Math.Abs(s) < 0.0001) s = 1;

        var x = (screen.X - PanTransform.X) / s;
        var y = (screen.Y - PanTransform.Y) / s;
        return new Point(x, y);
    }

    private static double Clamp(double v, double min, double max) => Math.Max(min, Math.Min(max, v));

    private void OnFitClick(object sender, RoutedEventArgs e) => FitToContent();

    public void FitToContent()
    {
        if (Layout is null || Layout.Nodes.Count == 0)
            return;

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        foreach (var n in Layout.Nodes)
        {
            minX = Math.Min(minX, n.Position.X - 120);
            minY = Math.Min(minY, n.Position.Y - 120);
            maxX = Math.Max(maxX, n.Position.X + 120);
            maxY = Math.Max(maxY, n.Position.Y + 120);
        }

        var worldW = Math.Max(1, maxX - minX);
        var worldH = Math.Max(1, maxY - minY);

        var viewportW = Math.Max(1, ActualWidth - 40);
        var viewportH = Math.Max(1, ActualHeight - 120);

        var scale = Math.Min(viewportW / worldW, viewportH / worldH);
        scale = Clamp(scale, 0.25, 2.5);

        ZoomTransform.ScaleX = scale;
        ZoomTransform.ScaleY = scale;

        var worldCenter = new Point(minX + worldW / 2, minY + worldH / 2);
        var viewCenter = new Point(ActualWidth / 2, ActualHeight / 2);

        PanTransform.X = viewCenter.X - worldCenter.X * scale;
        PanTransform.Y = viewCenter.Y - worldCenter.Y * scale;
    }

    private void ResetHighlight()
    {
        foreach (var v in edgeVisuals)
            v.Opacity = 1;

        foreach (var v in nodeVisualById.Values)
            v.Opacity = 1;

        foreach (var p in edgePathByKey.Values)
            p.Opacity = 1;

        foreach (var v in nodeVisualById.Values)
            v.Opacity = 1;
    }

    private void HighlightForNode(Guid nodeId)
    {
        if (Layout is null) return;

        foreach (var v in edgeVisuals)
            v.Opacity = 0.12;

        foreach (var v in nodeVisualById.Values)
            v.Opacity = 0.35;

        if (edgePathsByNode.TryGetValue(nodeId, out var paths))
            foreach (var p in paths)
                p.Opacity = 1;

        var related = new HashSet<Guid> { nodeId };
        foreach (var e in Layout.Edges)
        {
            if (e.FromId == nodeId || e.ToId == nodeId)
            {
                related.Add(e.FromId);
                related.Add(e.ToId);
            }
        }

        foreach (var id in related)
            if (nodeVisualById.TryGetValue(id, out var nv))
                nv.Opacity = 1;

        foreach (var id in related)
            if (outLabelByFromId.TryGetValue(id, out var lbl))
                lbl.Opacity = 1;

        if (badgesByNodeId.TryGetValue(nodeId, out var b))
            b.Opacity = 1;
    }
}
