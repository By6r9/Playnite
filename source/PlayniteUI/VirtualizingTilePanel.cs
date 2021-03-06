﻿using System.Windows.Controls.Primitives;
using System.Windows.Controls;
using System.Windows;
using System.Windows.Media;
using System;
using System.Diagnostics;
using System.Collections.Specialized;
using System.ComponentModel;

namespace PlayniteUI
{
    class VirtualizingTilePanel : VirtualizingPanel, IScrollInfo
    {
        private static readonly DependencyProperty ContainerSizeProperty = DependencyProperty.Register("ContainerSize", typeof(Size), typeof(VirtualizingTilePanel));

        // Dependency property that controls the size of the child elements
        public static readonly DependencyProperty ItemWidthProperty
           = DependencyProperty.RegisterAttached("ItemWidth", typeof(double), typeof(VirtualizingTilePanel), new FrameworkPropertyMetadata(150d, OnItemsSourceChanged));

        // Accessor for the child size dependency property
        public double ItemWidth
        {
            get { return (double)GetValue(ItemWidthProperty); }
            set { SetValue(ItemWidthProperty, value); }
        }

        private static void OnItemsSourceChanged(DependencyObject obj, DependencyPropertyChangedEventArgs e)
        {
            var panel = obj as VirtualizingTilePanel;
            if (panel._itemsControl == null)
            {
                return;
            }

            var storage = panel.GetItemStorageProvider();
            foreach (var item in panel._itemsControl.Items)
            {
                storage.ClearItemValue(item, ContainerSizeProperty);
            }

            panel.InvalidateMeasure();
            panel._owner.InvalidateScrollInfo();
            panel.SetVerticalOffset(0);
        }

        private IRecyclingItemContainerGenerator Generator;

        private ItemContainerGenerator GeneratorContainer
        {
            get => (ItemContainerGenerator)Generator;
        }

        public ItemsControl _itemsControl;

        public VirtualizingTilePanel()
        {

            if (!DesignerProperties.GetIsInDesignMode(this))
            {
                Dispatcher.BeginInvoke((Action)delegate
                {
                    _itemsControl = ItemsControl.GetItemsOwner(this);
                    Generator = (IRecyclingItemContainerGenerator)ItemContainerGenerator;
                    InvalidateMeasure();
                });
            }


            // For use in the IScrollInfo implementation
            this.RenderTransform = _trans;
        }

        /// <summary>
        /// Measure the children
        /// </summary>
        /// <param name="availableSize">Size available</param>
        /// <returns>Size desired</returns>
        protected override Size MeasureOverride(Size availableSize)
        {
            if (_itemsControl == null)
            {
                if (availableSize.Width == double.PositiveInfinity || availableSize.Height == double.PositiveInfinity)
                {
                    return Size.Empty;
                }
                else
                {
                    return availableSize;
                }
            }

            UpdateScrollInfo(availableSize);

            // Figure out range that's visible based on layout algorithm            
            GetVisibleRange(out var firstVisibleItemIndex, out var lastVisibleItemIndex);
            if (lastVisibleItemIndex == -1)
            {
                return availableSize;
            }

            // We need to access InternalChildren before the generator to work around a bug
            UIElementCollection children = this.InternalChildren;

            CleanUpItems(firstVisibleItemIndex, lastVisibleItemIndex);

            // Get the generator position of the first visible data item
            GeneratorPosition startPos = Generator.GeneratorPositionFromIndex(firstVisibleItemIndex);

            // Get index where we'd insert the child for this position. If the item is realized
            // (position.Offset == 0), it's just position.Index, otherwise we have to add one to
            // insert after the corresponding child
            int childIndex = (startPos.Offset == 0) ? startPos.Index : startPos.Index + 1;

            using (Generator.StartAt(startPos, GeneratorDirection.Forward, true))
            {
                for (int itemIndex = firstVisibleItemIndex; itemIndex <= lastVisibleItemIndex; ++itemIndex, ++childIndex)
                {
                    // Get or create the child
                    UIElement child = Generator.GenerateNext(out var newlyRealized) as UIElement;

                    if (newlyRealized)
                    {
                        // Figure out if we need to insert the child at the end or somewhere in the middle
                        if (childIndex >= children.Count)
                        {
                            AddInternalChild(child);
                        }
                        else
                        {
                            InsertInternalChild(childIndex, child);
                        }
                        Generator.PrepareItemContainer(child);
                    }
                    //If we get a recycled element
                    else if (!InternalChildren.Contains(child))
                    {
                        InsertInternalChild(childIndex, child);
                        ItemContainerGenerator.PrepareItemContainer(child);
                    }

                    // Measurements will depend on layout algorithm
                    child.Measure(GetInitialChildSize(child));
                }
            }

            return availableSize;
        }

        /// <summary>
        /// Revirtualize items that are no longer visible
        /// </summary>
        /// <param name="minDesiredGenerated">first item index that should be visible</param>
        /// <param name="maxDesiredGenerated">last item index that should be visible</param>
        private void CleanUpItems(int minDesiredGenerated, int maxDesiredGenerated)
        {
            for (int i = Children.Count - 1; i >= 0; i--)
            {
                GeneratorPosition childGeneratorPosition = new GeneratorPosition(i, 0);
                int iIndex = ItemContainerGenerator.IndexFromGeneratorPosition(childGeneratorPosition);
                if (iIndex < minDesiredGenerated || iIndex > maxDesiredGenerated)
                {
                    Generator.Recycle(childGeneratorPosition, 1);
                    RemoveInternalChildRange(i, 1);
                }
            }
        }

        /// <summary>
        /// Arrange the children
        /// </summary>
        /// <param name="finalSize">Size available</param>
        /// <returns>Size used</returns>
        protected override Size ArrangeOverride(Size finalSize)
        {
            for (int i = 0; i < Children.Count; i++)
            {
                UIElement child = Children[i];

                // Map the child offset to an item offset
                int itemIndex = Generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));

                ArrangeChild(itemIndex, child, finalSize);
            }

            UpdateScrollInfo(finalSize);
            return finalSize;
        }

        /// <summary>
        /// When items are removed, remove the corresponding UI if necessary
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
        {
            switch (args.Action)
            {
                case NotifyCollectionChangedAction.Remove:
                case NotifyCollectionChangedAction.Replace:
                    RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                    break;
                case NotifyCollectionChangedAction.Move:
                    RemoveInternalChildRange(args.OldPosition.Index, args.ItemUICount);
                    break;
            }
        }

        #region Layout specific code
        // I've isolated the layout specific code to this region. If you want to do something other than tiling, this is
        // where you'll make your changes

        /// <summary>
        /// Calculate the extent of the view based on the available size
        /// </summary>
        /// <param name="availableSize">available size</param>
        /// <param name="itemCount">number of data items</param>
        /// <returns></returns>
        private Size CalculateExtent(Size availableSize, int itemCount)
        {
            int childrenPerRow = CalculateChildrenPerRow(availableSize);

            // See how big we are
            return new Size(
                childrenPerRow * this.ItemWidth,
                this.ItemWidth * Math.Ceiling(itemCount / (double)childrenPerRow));
        }

        /// <summary>
        /// Get the range of children that are visible
        /// </summary>
        /// <param name="firstVisibleItemIndex">The item index of the first visible item</param>
        /// <param name="lastVisibleItemIndex">The item index of the last visible item</param>
        private void GetVisibleRange(out int firstVisibleItemIndex, out int lastVisibleItemIndex)
        {
            int itemCount = _itemsControl.HasItems ? _itemsControl.Items.Count : 0;
            if (itemCount == 0)
            {
                firstVisibleItemIndex = -1;
                lastVisibleItemIndex = -1;
                return;
            }

            int childrenPerRow = CalculateChildrenPerRow(_extent);
            
            var rows = 0;
            double totalHeight = 0;
            double largestHeight = ItemWidth;

            while (true)
            {
                var height = GetLargestRowHeight(rows, childrenPerRow);
                if (height > largestHeight)
                {
                    largestHeight = height;
                }

                if (_offset.Y > totalHeight + height)
                {
                    totalHeight += height;
                    rows++;
                }
                else
                {
                    break;
                }
            }

            var avarageHeight = rows == 0 ? (ItemWidth * 1.5) : totalHeight / rows;
            firstVisibleItemIndex = (int)((rows == 0 ? rows : rows) * childrenPerRow);

            var newRows = (int)Math.Ceiling(_viewport.Height / avarageHeight) + 1;

            lastVisibleItemIndex = firstVisibleItemIndex + (newRows * childrenPerRow);

            if (lastVisibleItemIndex >= itemCount)
            {
                lastVisibleItemIndex = itemCount - 1;
            }
        }

        /// <summary>
        /// Get the size of the children. We assume they are all the same
        /// </summary>
        /// <returns>The size</returns>
        private Size GetInitialChildSize(UIElement child)
        {
            var size = new Size(ItemWidth, double.PositiveInfinity);
            return size;
        }

        private Size GetChildSizeFromIndex(int index)
        {
            var elem = (UIElement)GeneratorContainer.ContainerFromIndex(index);
            if (elem != null)
            {
                return elem.DesiredSize;
            }

            var size = GetStoredChildSize(GeneratorContainer.Items[index]);
            if (size == Size.Empty)
            {
                return new Size(ItemWidth, (1.5 * ItemWidth));
            }
            else
            {
                return size;
            }
        }

        private Size GetStoredChildSize(object child)
        {
            var storage = GetItemStorageProvider();
            if (child is UIElement)
            {
                var item = GeneratorContainer.ItemFromContainer((UIElement)child);
                object value = storage.ReadItemValue(item, ContainerSizeProperty);
                if (value == null)
                {
                    return Size.Empty;
                }
                else
                {
                    return (Size)value;
                }

            }
            else
            {
                object value = storage.ReadItemValue(child, ContainerSizeProperty);
                if (value == null)
                {
                    return Size.Empty;
                }
                else
                {
                    return (Size)value;
                }
            }
        }

        public IContainItemStorage GetItemStorageProvider()
        {
            return _itemsControl as IContainItemStorage;
        }
              

        private double GetLargestRowHeight(int row, int childrenPerRow)
        {
            if (row < 0)
            {
                return 0;
            }

            if (Generator == null)
            {
                return ItemWidth * 1.5;
            }

            var items = GeneratorContainer.Items;

            var firstIndex = (row == 0 ? 0 : row -1) * childrenPerRow;
            var lastIndex = firstIndex + childrenPerRow;
            if (items.Count < lastIndex)
            {
                lastIndex = items.Count;
            }

            var aa = firstIndex == 0 ? 0 : firstIndex - 1;
            var biggestSize = GetChildSizeFromIndex(aa);
            for (var i = firstIndex; i < lastIndex; i++)
            {
                var size = GetChildSizeFromIndex(i);
                if (size.Height > biggestSize.Height)
                {
                    biggestSize.Height = size.Height;
                }
            }

            return biggestSize.Height;
        }

        private int GetItemRow(int itemIndex, int itemPerRow)
        {
            int column = itemIndex % itemPerRow;
            return itemIndex < column ? 0 : (int)Math.Floor(itemIndex / (double)itemPerRow);
        }

        /// <summary>
        /// Position a child
        /// </summary>
        /// <param name="itemIndex">The data item index of the child</param>
        /// <param name="child">The element to position</param>
        /// <param name="finalSize">The size of the panel</param>
        private void ArrangeChild(int itemIndex, UIElement child, Size finalSize)
        {
            int childrenPerRow = CalculateChildrenPerRow(finalSize);
            int column = itemIndex % childrenPerRow;
            int row = GetItemRow(itemIndex, childrenPerRow);
            var targetRect = new Rect(
                column * ItemWidth,
                GetTotalHeightForRow(row, childrenPerRow),
                ItemWidth,
                child.DesiredSize.Height);

            child.Arrange(targetRect);
            
            var item = GeneratorContainer.ItemFromContainer(child);
            GetItemStorageProvider().StoreItemValue(item, ContainerSizeProperty, child.DesiredSize);
        }

        /// <summary>
        /// Helper function for tiling layout
        /// </summary>
        /// <param name="availableSize">Size available</param>
        /// <returns></returns>
        private int CalculateChildrenPerRow(Size availableSize)
        {
            // Figure out how many children fit on each row
            int childrenPerRow;
            if (availableSize.Width == Double.PositiveInfinity)
                childrenPerRow = this.Children.Count;
            else
                childrenPerRow = Math.Max(1, (int)Math.Floor(availableSize.Width / ItemWidth));
            return childrenPerRow;
        }

        #endregion

        #region IScrollInfo implementation
        // See Ben Constable's series of posts at http://blogs.msdn.com/bencon/

        private double GetTotalHeightForRow(int row, int itemsPerRow)
        {
            int itemCount = _itemsControl.HasItems ? _itemsControl.Items.Count : 0;
            double totalHeight = 0;
            for (var i = 0; i < row; i++)
            {
                totalHeight += GetLargestRowHeight(i, itemsPerRow);
            }

            return totalHeight;
        }

        private double GetTotalHeight(Size availableSize)
        {
            int itemCount = _itemsControl.HasItems ? _itemsControl.Items.Count : 0;
            var perRow = CalculateChildrenPerRow(availableSize);
            var rows = Math.Ceiling(itemCount / (double)perRow);

            double totalHeight = 0;
            for (var i = 0; i < rows; i++)
            {
                totalHeight += GetLargestRowHeight(i, perRow);
            }

            return totalHeight;
        }

        private void UpdateScrollInfo(Size availableSize)
        {
            if (_itemsControl == null)
            {
                return;
            }

            // See how many items there are
            int itemCount = _itemsControl.HasItems ? _itemsControl.Items.Count : 0;
            var perRow = CalculateChildrenPerRow(availableSize);
            var totalHeight = GetTotalHeight(availableSize);

            if (_offset.Y > totalHeight)
            {
                _offset.Y = 0;
                _trans.Y = 0;
            }

            Size extent = new Size(perRow * ItemWidth, totalHeight);

            // Update extent
            if (extent != _extent)
            {
                _extent = extent;
                if (_owner != null)
                    _owner.InvalidateScrollInfo();
            }

            // Update viewport
            if (availableSize != _viewport)
            {
                _viewport = availableSize;
                if (_owner != null)
                    _owner.InvalidateScrollInfo();
            }
        }

        public ScrollViewer ScrollOwner
        {
            get { return _owner; }
            set { _owner = value; }
        }

        public bool CanHorizontallyScroll
        {
            get { return _canHScroll; }
            set { _canHScroll = value; }
        }

        public bool CanVerticallyScroll
        {
            get { return _canVScroll; }
            set { _canVScroll = value; }
        }

        public double HorizontalOffset
        {
            get { return _offset.X; }
        }

        public double VerticalOffset
        {
            get { return _offset.Y; }
        }

        public double ExtentHeight
        {
            get { return _extent.Height; }
        }

        public double ExtentWidth
        {
            get { return _extent.Width; }
        }

        public double ViewportHeight
        {
            get { return _viewport.Height; }
        }

        public double ViewportWidth
        {
            get { return _viewport.Width; }
        }

        private const double ScrollLineAmount = 16;

        public void LineUp()
        {
            SetVerticalOffset(VerticalOffset - ScrollLineAmount);
        }

        public void LineDown()
        {
            SetVerticalOffset(VerticalOffset + ScrollLineAmount);
        }

        public void PageUp()
        {
            SetVerticalOffset(VerticalOffset - _viewport.Height);
        }

        public void PageDown()
        {
            SetVerticalOffset(VerticalOffset + _viewport.Height);
        }

        public void MouseWheelUp()
        {
            SetVerticalOffset(VerticalOffset - ItemWidth);
        }

        public void MouseWheelDown()
        {
            SetVerticalOffset(VerticalOffset + ItemWidth);
        }

        public void LineLeft()
        {

        }

        public void LineRight()
        {

        }

        public Rect MakeVisible(Visual visual, Rect rectangle)
        {
            var index = GeneratorContainer.IndexFromContainer(visual);
            var perRow = CalculateChildrenPerRow(_extent);
            var row = GetItemRow(index, perRow);
            var offset = GetTotalHeightForRow(row, perRow);
            var elem = visual as UIElement;
            
            var offsetSize = offset + elem.DesiredSize.Height;
            var offsetBottom = _offset.Y + _viewport.Height;
            if (offset > _offset.Y && offsetSize < offsetBottom)
            {
                return rectangle;
            }
            else if (offset > _offset.Y && (offsetBottom - offset < elem.DesiredSize.Height))
            {
                offset = _offset.Y + (elem.DesiredSize.Height - (offsetBottom - offset));
            }
            else if (Math.Floor((offsetBottom - offset)) == Math.Floor(elem.DesiredSize.Height))
            {
                return rectangle;
            }

            _offset.Y = offset;
            _trans.Y = -offset;
            InvalidateMeasure();
            return rectangle;
        }

        public void MouseWheelLeft()
        {

        }

        public void MouseWheelRight()
        {

        }

        public void PageLeft()
        {

        }

        public void PageRight()
        {

        }

        public void SetHorizontalOffset(double offset)
        {

        }

        public void SetVerticalOffset(double offset)
        {
            if (offset < 0 || _viewport.Height >= _extent.Height)
            {
                offset = 0;
            }
            else
            {
                if (offset + _viewport.Height >= _extent.Height)
                {
                    offset = _extent.Height - _viewport.Height;
                }
            }

            _offset.Y = offset;

            if (_owner != null)
                _owner.InvalidateScrollInfo();

            _trans.Y = -offset;

            InvalidateMeasure();
        }

        private TranslateTransform _trans = new TranslateTransform();
        private ScrollViewer _owner;
        private bool _canHScroll = false;
        private bool _canVScroll = false;
        private Size _extent = new Size(0, 0);
        private Size _viewport = new Size(0, 0);
        private Point _offset;

        #endregion

    }
}
