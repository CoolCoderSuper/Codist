﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.CodeAnalysis;
using AppHelpers;

namespace Codist.Controls
{
	// HACK: put the symbol list, smart bar, etc. on top of the WpfTextView
	// don't use AdornmentLayer to do so, otherwise contained objects will go up and down when scrolling code window
	sealed class ExternalAdornment : Canvas
	{
		readonly Microsoft.VisualStudio.Text.Editor.IWpfTextView _View;
		int _LayerZIndex;

		public ExternalAdornment(Microsoft.VisualStudio.Text.Editor.IWpfTextView view) {
			UseLayoutRounding = true;
			SnapsToDevicePixels = true;
			Grid.SetColumn(this, 1);
			Grid.SetRow(this, 1);
			Grid.SetIsSharedSizeScope(this, true);
			var grid = view.VisualElement.GetParent<Grid>();
			if (grid != null) {
				grid.Children.Add(this);
			}
			else {
				view.VisualElement.Loaded += VisualElement_Loaded;
			}
			_View = view;
		}

		public void FocusOnTextView() {
			_View.VisualElement.Focus();
		}

		public void Clear() {
			//foreach (UIElement item in Children) {
			//	item.MouseLeave -= ReleaseQuickInfo;
			//	item.MouseEnter -= SuppressQuickInfo;
			//}
			Children.Clear();
			_View.Properties.RemoveProperty(nameof(ExternalAdornment));
			//_View.VisualElement.Focus();
		}

		protected override void OnVisualChildrenChanged(DependencyObject visualAdded, DependencyObject visualRemoved) {
			base.OnVisualChildrenChanged(visualAdded, visualRemoved);
			var element = visualAdded as UIElement;
			if (element != null) {
				element.MouseLeave -= ReleaseQuickInfo;
				element.MouseLeave += ReleaseQuickInfo;
				element.MouseEnter -= SuppressQuickInfo;
				element.MouseEnter += SuppressQuickInfo;
				element.MouseLeftButtonDown -= BringToFront;
				element.MouseLeftButtonDown += BringToFront;
				SetZIndex(element, ++_LayerZIndex);
			}
			element = visualRemoved as UIElement;
			if (element != null) {
				element.MouseLeave -= ReleaseQuickInfo;
				element.MouseEnter -= SuppressQuickInfo;
				element.MouseLeftButtonDown -= BringToFront;
			}
			if (Children.Count == 0 || Children.Count == 1 && Children[0] is null) {
				_View.Properties.RemoveProperty(nameof(ExternalAdornment));
				FocusOnTextView();
			}
		}

		void VisualElement_Loaded(object sender, RoutedEventArgs e) {
			_View.VisualElement.Loaded -= VisualElement_Loaded;
			_View.VisualElement.GetParent<Grid>().Children.Add(this);
		}

		void BringToFront(object sender, MouseButtonEventArgs e) {
			SetZIndex(e.Source as UIElement, ++_LayerZIndex);
		}

		void ReleaseQuickInfo(object sender, MouseEventArgs e) {
			_View.Properties.RemoveProperty(nameof(ExternalAdornment));
		}

		void SuppressQuickInfo(object sender, MouseEventArgs e) {
			_View.Properties[nameof(ExternalAdornment)] = true;
		}
	}
}
