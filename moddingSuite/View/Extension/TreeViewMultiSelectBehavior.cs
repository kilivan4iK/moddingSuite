using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using moddingSuite.ViewModel.Edata;

namespace moddingSuite.View.Extension
{
    public static class TreeViewMultiSelectBehavior
    {
        private sealed class SelectionState
        {
            public VirtualNodeViewModel AnchorNode { get; set; }
        }

        private static readonly Dictionary<TreeView, SelectionState> StateByTree = new Dictionary<TreeView, SelectionState>();

        public static readonly DependencyProperty EnableMultiSelectProperty = DependencyProperty.RegisterAttached(
            "EnableMultiSelect",
            typeof(bool),
            typeof(TreeViewMultiSelectBehavior),
            new PropertyMetadata(false, OnEnableMultiSelectChanged));

        public static readonly DependencyProperty SelectedItemsProperty = DependencyProperty.RegisterAttached(
            "SelectedItems",
            typeof(IList),
            typeof(TreeViewMultiSelectBehavior),
            new PropertyMetadata(null));

        public static bool GetEnableMultiSelect(DependencyObject obj)
        {
            return (bool)obj.GetValue(EnableMultiSelectProperty);
        }

        public static void SetEnableMultiSelect(DependencyObject obj, bool value)
        {
            obj.SetValue(EnableMultiSelectProperty, value);
        }

        public static IList GetSelectedItems(DependencyObject obj)
        {
            return (IList)obj.GetValue(SelectedItemsProperty);
        }

        public static void SetSelectedItems(DependencyObject obj, IList value)
        {
            obj.SetValue(SelectedItemsProperty, value);
        }

        private static void OnEnableMultiSelectChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is TreeView treeView))
                return;

            bool enabled = (bool)e.NewValue;
            if (enabled)
            {
                treeView.PreviewMouseLeftButtonDown += TreeViewOnPreviewMouseLeftButtonDown;
                EnsureState(treeView);
            }
            else
            {
                treeView.PreviewMouseLeftButtonDown -= TreeViewOnPreviewMouseLeftButtonDown;
                StateByTree.Remove(treeView);
            }
        }

        private static void TreeViewOnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is TreeView treeView))
                return;

            TreeViewItem treeViewItem = TreeViewExtension.VisualUpwardSearch(e.OriginalSource as DependencyObject);
            if (treeViewItem == null)
                return;

            if (!(treeViewItem.DataContext is VirtualNodeViewModel clickedNode))
                return;

            SelectionState state = EnsureState(treeView);
            IList selectedItems = GetSelectedItems(treeView);
            List<VirtualNodeViewModel> currentSelection = selectedItems == null
                ? new List<VirtualNodeViewModel>()
                : selectedItems.OfType<VirtualNodeViewModel>().Distinct().ToList();

            bool isCtrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            bool isShift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

            List<VirtualNodeViewModel> newSelection;

            if (isShift && state.AnchorNode != null)
            {
                List<VirtualNodeViewModel> visibleNodes = EnumerateVisibleNodes(treeView).ToList();
                int anchorIndex = visibleNodes.IndexOf(state.AnchorNode);
                int clickedIndex = visibleNodes.IndexOf(clickedNode);

                if (anchorIndex >= 0 && clickedIndex >= 0)
                {
                    int start = Math.Min(anchorIndex, clickedIndex);
                    int length = Math.Abs(anchorIndex - clickedIndex) + 1;
                    List<VirtualNodeViewModel> rangeSelection = visibleNodes.Skip(start).Take(length).ToList();

                    if (isCtrl)
                        newSelection = currentSelection.Union(rangeSelection).Distinct().ToList();
                    else
                        newSelection = rangeSelection;
                }
                else
                {
                    newSelection = new List<VirtualNodeViewModel> { clickedNode };
                    state.AnchorNode = clickedNode;
                }
            }
            else if (isCtrl)
            {
                newSelection = new List<VirtualNodeViewModel>(currentSelection);
                if (newSelection.Contains(clickedNode))
                    newSelection.Remove(clickedNode);
                else
                    newSelection.Add(clickedNode);

                state.AnchorNode = clickedNode;
            }
            else
            {
                newSelection = new List<VirtualNodeViewModel> { clickedNode };
                state.AnchorNode = clickedNode;
            }

            ApplySelection(treeView, newSelection);

            treeViewItem.IsSelected = true;
            treeViewItem.Focus();
        }

        private static SelectionState EnsureState(TreeView treeView)
        {
            if (!StateByTree.TryGetValue(treeView, out SelectionState state))
            {
                state = new SelectionState();
                StateByTree[treeView] = state;
            }

            return state;
        }

        private static void ApplySelection(TreeView treeView, IEnumerable<VirtualNodeViewModel> selection)
        {
            List<VirtualNodeViewModel> normalized = (selection ?? Enumerable.Empty<VirtualNodeViewModel>())
                .Where(node => node != null)
                .Distinct()
                .ToList();

            if (treeView.DataContext is EdataManagerViewModel vm)
            {
                vm.ApplyUnifiedZzSelection(normalized);
                return;
            }

            IList selectedItems = GetSelectedItems(treeView);
            if (selectedItems == null)
                return;

            foreach (VirtualNodeViewModel node in EnumerateAllNodes(treeView))
                node.IsMultiSelected = false;

            selectedItems.Clear();
            foreach (VirtualNodeViewModel node in normalized)
            {
                node.IsMultiSelected = true;
                selectedItems.Add(node);
            }
        }

        private static IEnumerable<VirtualNodeViewModel> EnumerateVisibleNodes(TreeView treeView)
        {
            foreach (VirtualNodeViewModel rootNode in treeView.Items.OfType<VirtualNodeViewModel>())
            {
                foreach (VirtualNodeViewModel node in EnumerateVisibleNodes(rootNode))
                    yield return node;
            }
        }

        private static IEnumerable<VirtualNodeViewModel> EnumerateVisibleNodes(VirtualNodeViewModel rootNode)
        {
            yield return rootNode;
            if (!rootNode.IsExpanded)
                yield break;

            foreach (VirtualNodeViewModel childNode in rootNode.Children)
            {
                foreach (VirtualNodeViewModel node in EnumerateVisibleNodes(childNode))
                    yield return node;
            }
        }

        private static IEnumerable<VirtualNodeViewModel> EnumerateAllNodes(TreeView treeView)
        {
            foreach (VirtualNodeViewModel rootNode in treeView.Items.OfType<VirtualNodeViewModel>())
            {
                foreach (VirtualNodeViewModel node in EnumerateAllNodes(rootNode))
                    yield return node;
            }
        }

        private static IEnumerable<VirtualNodeViewModel> EnumerateAllNodes(VirtualNodeViewModel rootNode)
        {
            yield return rootNode;
            foreach (VirtualNodeViewModel childNode in rootNode.Children)
            {
                foreach (VirtualNodeViewModel node in EnumerateAllNodes(childNode))
                    yield return node;
            }
        }
    }
}
