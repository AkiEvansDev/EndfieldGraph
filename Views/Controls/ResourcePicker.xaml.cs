using EndfieldGraph.ViewModels.Common;
using EndfieldGraph.ViewModels.Resource;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace EndfieldGraph.Views.Controls;

public partial class ResourcePicker : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(
            nameof(ItemsSource),
            typeof(IEnumerable<ResourceViewModel>),
            typeof(ResourcePicker),
            new PropertyMetadata(null, OnInputsChanged));

    public IEnumerable<ResourceViewModel>? ItemsSource
    {
        get => (IEnumerable<ResourceViewModel>?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public static readonly DependencyProperty TakenItemsProperty =
        DependencyProperty.Register(
            nameof(TakenItems),
            typeof(IEnumerable<InputViewModel>),
            typeof(ResourcePicker),
            new PropertyMetadata(null, OnInputsChanged));

    public IEnumerable<InputViewModel>? TakenItems
    {
        get => (IEnumerable<InputViewModel>?)GetValue(TakenItemsProperty);
        set => SetValue(TakenItemsProperty, value);
    }

    public static readonly DependencyProperty SelectedIdProperty =
        DependencyProperty.Register(
            nameof(SelectedId),
            typeof(Guid),
            typeof(ResourcePicker),
            new FrameworkPropertyMetadata(Guid.Empty,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnSelectedIdChanged));

    public Guid SelectedId
    {
        get => (Guid)GetValue(SelectedIdProperty);
        set => SetValue(SelectedIdProperty, value);
    }

    public static readonly DependencyProperty SearchPlaceholderProperty =
        DependencyProperty.Register(nameof(SearchPlaceholder), typeof(string), typeof(ResourcePicker),
            new PropertyMetadata("Search..."));

    public string SearchPlaceholder
    {
        get => (string)GetValue(SearchPlaceholderProperty);
        set => SetValue(SearchPlaceholderProperty, value);
    }

    public static readonly DependencyProperty SearchTextProperty =
        DependencyProperty.Register(nameof(SearchText), typeof(string), typeof(ResourcePicker),
            new PropertyMetadata(string.Empty, OnSearchTextChanged));

    public string SearchText
    {
        get => (string)GetValue(SearchTextProperty);
        set => SetValue(SearchTextProperty, value);
    }

    public static readonly DependencyProperty ViewItemsProperty =
        DependencyProperty.Register(nameof(ViewItems), typeof(ObservableCollection<ResourceViewModel>), typeof(ResourcePicker),
            new PropertyMetadata(null));

    public ObservableCollection<ResourceViewModel> ViewItems
    {
        get => (ObservableCollection<ResourceViewModel>)GetValue(ViewItemsProperty);
        private set => SetValue(ViewItemsProperty, value);
    }

    public static readonly DependencyProperty SelectedNameProperty =
        DependencyProperty.Register(nameof(SelectedName), typeof(string), typeof(ResourcePicker),
            new PropertyMetadata("Select..."));

    public string SelectedName
    {
        get => (string)GetValue(SelectedNameProperty);
        private set => SetValue(SelectedNameProperty, value);
    }

    public static readonly DependencyProperty SelectedIconProperty =
        DependencyProperty.Register(nameof(SelectedIcon), typeof(byte[]), typeof(ResourcePicker),
            new PropertyMetadata(null));

    public byte[]? SelectedIcon
    {
        get => (byte[]?)GetValue(SelectedIconProperty);
        private set => SetValue(SelectedIconProperty, value);
    }

    private ICollectionView? view;

    private INotifyCollectionChanged? itemsNotify;
    private INotifyCollectionChanged? takenNotify;

    public ResourcePicker()
    {
        InitializeComponent();

        ViewItems = [];
        view = CollectionViewSource.GetDefaultView(ViewItems);
        view.Filter = Filter;

        Unloaded += ResourcePickerUnloaded;
    }

    private void OnButtonRootClick(object sender, RoutedEventArgs e)
    {
        Flyout.IsOpen = !Flyout.IsOpen;
        e.Handled = true;
    }

    private void OnFlyoutOpened(object sender, RoutedEventArgs e)
    {
        RefreshAll(rebuild: true);

        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }));
    }

    private void OnFlyoutClosed(object sender, RoutedEventArgs e)
    {
        SearchText = "";
    }

    private void OnSearchBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!Flyout.IsOpen) return;

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Flyout.IsOpen = false;
            return;
        }

        if (List.Items.Count == 0)
            return;

        if (e.Key == Key.Down)
        {
            e.Handled = true;

            var i = List.SelectedIndex;
            if (i < 0) i = 0;
            else i = Math.Min(i + 1, List.Items.Count - 1);

            List.SelectedIndex = i;
            List.ScrollIntoView(List.SelectedItem);
            return;
        }

        if (e.Key == Key.Up)
        {
            e.Handled = true;

            var i = List.SelectedIndex;
            if (i < 0) i = List.Items.Count - 1;
            else i = Math.Max(i - 1, 0);

            List.SelectedIndex = i;
            List.ScrollIntoView(List.SelectedItem);
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitFromList();
            return;
        }
    }

    private void OnListMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => CommitFromList();

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitFromList();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Flyout.IsOpen = false;
        }
    }

    private void CommitFromList()
    {
        if (List.SelectedItem is not ResourceViewModel r) return;
        if (r.Id == Guid.Empty) return;

        SelectedId = r.Id;
        Flyout.IsOpen = false;
    }

    private static void OnInputsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (ResourcePicker)d;
        c.HookCollectionChanged();
        c.RefreshAll(rebuild: true);
    }

    private static void OnSelectedIdChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (ResourcePicker)d;
        c.RefreshAll(rebuild: true);
    }

    private static void OnSearchTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (ResourcePicker)d;
        c.RefreshAll(rebuild: false);
    }

    private void HookCollectionChanged()
    {
        if (itemsNotify is not null)
            itemsNotify.CollectionChanged -= SourceChanged;
        if (takenNotify is not null)
            takenNotify.CollectionChanged -= SourceChanged;

        itemsNotify = ItemsSource as INotifyCollectionChanged;
        takenNotify = TakenItems as INotifyCollectionChanged;

        if (itemsNotify is not null)
            itemsNotify.CollectionChanged += SourceChanged;
        if (takenNotify is not null)
            takenNotify.CollectionChanged += SourceChanged;
    }

    private void SourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshAll(rebuild: true);
    }

    private void RefreshAll(bool rebuild)
    {
        if (rebuild)
            RebuildCore();

        view ??= CollectionViewSource.GetDefaultView(ViewItems);
        view.Filter = Filter;
        view.Refresh();

        SyncSelectedBadge();
        SyncListSelection();
    }

    private void RebuildCore()
    {
        var all = ItemsSource?.Where(x => x.Id != Guid.Empty).ToList()
                  ?? [];

        var taken = new HashSet<Guid>();

        if (TakenItems is not null)
        {
            foreach (var x in TakenItems)
            {
                if (x.Id != Guid.Empty)
                    taken.Add(x.Id);
            }
        }

        if (SelectedId != Guid.Empty)
            taken.Remove(SelectedId);

        var filtered = all.Where(item => !taken.Contains(item.Id)).ToList();

        ViewItems.Clear();
        foreach (var item in filtered)
            ViewItems.Add(item);
    }

    private bool Filter(object obj)
    {
        if (obj is not ResourceViewModel r)
            return false;

        if (SelectedId != Guid.Empty && r.Id == SelectedId)
            return true;

        var q = (SearchText ?? "").Trim();
        if (string.IsNullOrWhiteSpace(q))
            return true;

        return r.Name.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void SyncSelectedBadge()
    {
        var selected = ItemsSource?.FirstOrDefault(x => x.Id == SelectedId);

        if (selected is null)
        {
            SelectedName = "Select...";
            SelectedIcon = null;
            return;
        }

        SelectedName = selected.Name;
        SelectedIcon = selected.Icon;
    }

    private void SyncListSelection()
    {
        if (!Flyout.IsOpen) return;
        if (SelectedId == Guid.Empty) return;

        var selObj = ViewItems.FirstOrDefault(x => x.Id == SelectedId);
        List.SelectedItem = selObj;

        if (selObj is not null)
            List.ScrollIntoView(selObj);
    }

    private void ResourcePickerUnloaded(object sender, RoutedEventArgs e)
    {
        if (itemsNotify is not null)
            itemsNotify.CollectionChanged -= SourceChanged;
        if (takenNotify is not null)
            takenNotify.CollectionChanged -= SourceChanged;

        Unloaded -= ResourcePickerUnloaded;
    }
}
