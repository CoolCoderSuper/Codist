﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using AppHelpers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using GDI = System.Drawing;
using R = Codist.Properties.Resources;
using Task = System.Threading.Tasks.Task;
using WPF = System.Windows.Media;

namespace Codist.Controls
{
	class SymbolList : VirtualList, ISymbolFilterable, INotifyCollectionChanged, IDisposable
	{
		Predicate<object> _Filter;
		readonly ToolTip _SymbolTip;
		readonly List<SymbolItem> _Symbols;

		public SymbolList(SemanticContext semanticContext) {
			_Symbols = new List<SymbolItem>();
			FilteredItems = new ListCollectionView(_Symbols);
			SemanticContext = semanticContext;
			_SymbolTip = new ToolTip {
				Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
				PlacementTarget = this
			};
			Resources = SharedDictionaryManager.SymbolList;
		}

		public SemanticContext SemanticContext { get; private set; }
		public IReadOnlyList<SymbolItem> Symbols => _Symbols;
		public SymbolListType ContainerType { get; set; }
		public Func<SymbolItem, UIElement> IconProvider { get; set; }
		public Func<SymbolItem, UIElement> ExtIconProvider { get; set; }
		public SymbolItem SelectedSymbolItem => SelectedItem as SymbolItem;
		public event NotifyCollectionChangedEventHandler CollectionChanged;

		public bool IsPinned { get; set; }

		public SymbolItem Add(SyntaxNode node) {
			var item = new SymbolItem(node, this);
			_Symbols.Add(item);
			return item;
		}
		public SymbolItem Add(ISymbol symbol, bool includeContainerType) {
			var item = new SymbolItem(symbol, this, includeContainerType);
			_Symbols.Add(item);
			return item;
		}
		public SymbolItem Add(ISymbol symbol, ISymbol containerType) {
			var item = new SymbolItem(symbol, this, containerType);
			_Symbols.Add(item);
			return item;
		}
		public SymbolItem Add(Location location) {
			var item = new SymbolItem(location, this);
			_Symbols.Add(item);
			return item;
		}
		public SymbolItem Add(SymbolItem item) {
			_Symbols.Add(item);
			return item;
		}
		public void AddRange(IEnumerable<SymbolItem> items) {
			_Symbols.AddRange(items);
		}

		public void ClearSymbols() {
			foreach (var item in _Symbols) {
				item.Release();
			}
			_Symbols.Clear();
			CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
		}
		public void RefreshItemsSource(bool force = false) {
			if (force) {
				ItemsSource = null;
			}
			if (_Filter != null) {
				FilteredItems.Filter = _Filter;
				ItemsSource = FilteredItems;
			}
			else {
				ItemsSource = _Symbols;
			}
			if (SelectedIndex == -1 && HasItems) {
				SelectedIndex = 0;
			}
		}

		protected override void OnPreviewKeyDown(KeyEventArgs e) {
			base.OnPreviewKeyDown(e);
			if (e.OriginalSource is TextBox == false || e.Handled) {
				return;
			}
			if (e.Key == Key.Enter) {
				var item = SelectedIndex == -1 && HasItems
					? ItemContainerGenerator.Items[0] as SymbolItem
					: SelectedItem as SymbolItem;
				item?.GoToSource();
				e.Handled = true;
			}
		}

		#region Analysis commands
		internal void AddNamespaceItems(ISymbol[] symbols, ISymbol highlight) {
			var c = highlight != null ? CodeAnalysisHelper.GetSpecificSymbolComparer(highlight) : null;
			foreach (var item in symbols) {
				var s = Add(new SymbolItem(item, this, false));
				if (c != null && c(item)) {
					SelectedItem = s;
					c = null;
				}
			}
		}

		internal void SetupForSpecialTypes(ITypeSymbol type) {
			INamespaceSymbol typeNamespace;
			if (type == null) {
				return;
			}
			switch (type.TypeKind) {
				case TypeKind.Dynamic:
					return;
				case TypeKind.Enum:
					if (type.GetAttributes().Any(a => a.AttributeClass.MatchTypeName(nameof(FlagsAttribute), "System"))) {
						ContainerType = SymbolListType.EnumFlags;
						return;
					}
					break;
			}
			typeNamespace = type.ContainingNamespace;
			if (typeNamespace == null || typeNamespace.IsGlobalNamespace) {
				return;
			}
			string typeName = type.Name;
			switch (typeNamespace.ToString()) {
				case "System.Drawing":
					switch (typeName) {
						case nameof(GDI.SystemBrushes):
						case nameof(GDI.SystemPens):
						case nameof(GDI.SystemColors):
							SetupListForSystemColors(); return;
						case nameof(GDI.Color):
						case nameof(GDI.Brushes):
						case nameof(GDI.Pens):
							SetupListForColors(); return;
						case nameof(GDI.KnownColor): SetupListForKnownColors(); return;
					}
					return;
				case "System.Windows":
					if (typeName == nameof(SystemColors)) {
						SetupListForSystemColors();
					}
					return;
				case "System.Windows.Media":
					switch (typeName) {
						case nameof(WPF.Colors):
						case nameof(WPF.Brushes):
							SetupListForColors(); return;
					}
					return;
				case "Microsoft.VisualStudio.PlatformUI":
					switch (typeName) {
						case nameof(EnvironmentColors): SetupListForVsUIColors(typeof(EnvironmentColors)); return;
						case nameof(CommonControlsColors): SetupListForVsUIColors(typeof(CommonControlsColors)); return;
						case nameof(CommonDocumentColors): SetupListForVsUIColors(typeof(CommonDocumentColors)); return;
						case nameof(HeaderColors): SetupListForVsUIColors(typeof(HeaderColors)); return;
						case nameof(InfoBarColors): SetupListForVsUIColors(typeof(InfoBarColors)); return;
						case nameof(ProgressBarColors): SetupListForVsUIColors(typeof(ProgressBarColors)); return;
						case nameof(SearchControlColors): SetupListForVsUIColors(typeof(SearchControlColors)); return;
						case nameof(StartPageColors): SetupListForVsUIColors(typeof(StartPageColors)); return;
						case nameof(ThemedDialogColors): SetupListForVsUIColors(typeof(ThemedDialogColors)); return;
						case nameof(TreeViewColors): SetupListForVsUIColors(typeof(TreeViewColors)); return;
					}
					return;
				case "Microsoft.VisualStudio.Shell":
					switch (typeName) {
						case nameof(VsColors): SetupListForVsResourceColors(typeof(VsColors)); return;
						case nameof(VsBrushes): SetupListForVsResourceBrushes(typeof(VsBrushes)); return;
					}
					return;
				case "Microsoft.VisualStudio.Imaging":
					switch (typeName) {
						case nameof(KnownImageIds):
							SetupListForKnownImageIds();
							break;
						case nameof(KnownMonikers):
							SetupListForKnownMonikers();
							break;
					}
					return;
			}
		}

		void SetupListForVsUIColors(Type type) {
			ContainerType = SymbolListType.PredefinedColors;
			IconProvider = s => ((s.Symbol as IPropertySymbol)?.IsStatic == true) ? GetColorPreviewIcon(ColorHelper.GetVsThemeBrush(type, s.Symbol.Name)) : null;
		}
		void SetupListForVsResourceColors(Type type) {
			ContainerType = SymbolListType.PredefinedColors;
			IconProvider = s => ((s.Symbol as IPropertySymbol)?.IsStatic == true) ? GetColorPreviewIcon(ColorHelper.GetVsResourceColor(type, s.Symbol.Name)) : null;
		}
		void SetupListForVsResourceBrushes(Type type) {
			ContainerType = SymbolListType.PredefinedColors;
			IconProvider = s => ((s.Symbol as IPropertySymbol)?.IsStatic == true) ? GetColorPreviewIcon(ColorHelper.GetVsResourceBrush(type, s.Symbol.Name)) : null;
		}
		void SetupListForSystemColors() {
			ContainerType = SymbolListType.PredefinedColors;
			IconProvider = s => ((s.Symbol as IPropertySymbol)?.IsStatic == true) ? GetColorPreviewIcon(ColorHelper.GetSystemBrush(s.Symbol.Name)) : null;
		}
		void SetupListForColors() {
			ContainerType = SymbolListType.PredefinedColors;
			IconProvider = s => ((s.Symbol as IPropertySymbol)?.IsStatic == true) ? GetColorPreviewIcon(ColorHelper.GetBrush(s.Symbol.Name)) : null;
		}
		void SetupListForKnownColors() {
			ContainerType = SymbolListType.PredefinedColors;
			IconProvider = s => ((s.Symbol as IFieldSymbol)?.IsStatic == true) ? GetColorPreviewIcon(ColorHelper.GetBrush(s.Symbol.Name) ?? ColorHelper.GetSystemBrush(s.Symbol.Name)) : null;
		}
		void SetupListForKnownImageIds() {
			ContainerType = SymbolListType.VsKnownImage;
			IconProvider = s => {
				var f = s.Symbol as IFieldSymbol;
				return f == null || f.HasConstantValue == false || f.Type.SpecialType != SpecialType.System_Int32
					? null
					: ThemeHelper.GetImage((int)f.ConstantValue);
			};
		}
		void SetupListForKnownMonikers() {
			ContainerType = SymbolListType.VsKnownImage;
			IconProvider = s => {
				var p = s.Symbol as IPropertySymbol;
				return p == null || p.IsStatic == false
					? null
					: ThemeHelper.GetImage(p.Name);
			};
		}
		static Border GetColorPreviewIcon(WPF.Brush brush) {
			return brush == null ? null : new Border {
				BorderThickness = WpfHelper.TinyMargin,
				BorderBrush = ThemeHelper.MenuTextBrush,
				SnapsToDevicePixels = true,
				Background = brush,
				Height = ThemeHelper.DefaultIconSize,
				Width = ThemeHelper.DefaultIconSize,
			};
		}
		#endregion

		#region Context menu
		protected override void OnContextMenuOpening(ContextMenuEventArgs e) {
			base.OnContextMenuOpening(e);
			ShowContextMenu(e);
		}

		internal void ShowContextMenu(RoutedEventArgs e) {
			var item = SelectedSymbolItem;
			if (item == null
				|| (item.Symbol == null && item.SyntaxNode == null)
				|| (e.OriginalSource as DependencyObject).GetParentOrSelf<ListBoxItem>() == null) {
				e.Handled = true;
				return;
			}
			if (ContextMenu is CSharpSymbolContextMenu m) {
				m.Dispose();
			}
			ContextMenu = m = new CSharpSymbolContextMenu(item.Symbol, item.SyntaxNode, SemanticContext) {
				Resources = SharedDictionaryManager.ContextMenu,
				Foreground = ThemeHelper.ToolWindowTextBrush,
				IsEnabled = true,
			};
			SetupContextMenu(m, item);
			m.AddTitleItem(item.SyntaxNode?.GetDeclarationSignature() ?? item.Symbol.GetOriginalName());
			m.IsOpen = true;
		}

		void SetupContextMenu(CSharpSymbolContextMenu menu, SymbolItem item) {
			if (item.Symbol != null) {
				menu.AddAnalysisCommands();
				menu.Items.Add(new Separator());
				if (item.SyntaxNode == null && item.Symbol.HasSource()) {
					menu.AddSymbolNodeCommands();
				}
				else {
					menu.AddCopyAndSearchSymbolCommands();
				}
			}
			if (item.SyntaxNode != null) {
				SetupMenuCommand(item, IconIds.SelectCode, R.CMD_SelectCode, s => {
					if (s.IsExternal) {
						s.SyntaxNode.SelectNode(true);
					}
					else {
						s.Container.SemanticContext.View.SelectNode(s.SyntaxNode, true);
					}
				});
				//SetupMenuCommand(item, KnownImageIds.Copy, "Copy Code", s => Clipboard.SetText(s.SyntaxNode.ToFullString()));
				item.SetSymbolToSyntaxNode();
			}
		}

		void SetupMenuCommand(SymbolItem item, int imageId, string title, Action<SymbolItem> action) {
			var mi = new ThemedMenuItem(imageId, title, (s, args) => {
				var i = (ValueTuple<SymbolItem, Action<SymbolItem>>)((MenuItem)s).Tag;
				i.Item2(i.Item1);
			}) {
				Tag = (item, action)
			};
			ContextMenu.Items.Add(mi);
		}
		#endregion

		#region Tool Tip
		protected override void OnMouseEnter(MouseEventArgs e) {
			base.OnMouseEnter(e);
			if (_SymbolTip.Tag == null) {
				_SymbolTip.Tag = DateTime.Now;
				SizeChanged -= SizeChanged_RelocateToolTip;
				MouseMove -= MouseMove_ChangeToolTip;
				MouseLeave -= MouseLeave_HideToolTip;
				SizeChanged += SizeChanged_RelocateToolTip;
				MouseMove += MouseMove_ChangeToolTip;
				MouseLeave += MouseLeave_HideToolTip;
			}
		}

		void MouseLeave_HideToolTip(object sender, MouseEventArgs e) {
			UnhookMouseEventAndHideToolTip();
		}

		void UnhookMouseEventAndHideToolTip() {
			SizeChanged -= SizeChanged_RelocateToolTip;
			MouseMove -= MouseMove_ChangeToolTip;
			MouseLeave -= MouseLeave_HideToolTip;
			HideToolTip();
		}

		internal void HideToolTip() {
			_SymbolTip.IsOpen = false;
			_SymbolTip.Content = null;
			_SymbolTip.Tag = null;
		}

		[System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100:Avoid async void methods", Justification = "Event handler")]
		async void MouseMove_ChangeToolTip(object sender, MouseEventArgs e) {
			var li = GetMouseEventTarget(e);
			if (li != null && _SymbolTip.Tag != li) {
				await ShowToolTipForItemAsync(li);
			}
		}

		void SizeChanged_RelocateToolTip(object sender, SizeChangedEventArgs e) {
			if (_SymbolTip.IsOpen) {
				_SymbolTip.IsOpen = false;
				_SymbolTip.IsOpen = true;
			}
		}

		async Task ShowToolTipForItemAsync(ListBoxItem li) {
			_SymbolTip.Tag = li;
			_SymbolTip.Content = await CreateItemToolTipAsync(li);
			_SymbolTip.IsOpen = true;
		}

		async Task<object> CreateItemToolTipAsync(ListBoxItem li) {
			SymbolItem item;
			var sc = SemanticContext;
			if ((item = li.Content as SymbolItem) == null
				|| sc == null
				|| await sc.UpdateAsync(default).ConfigureAwait(true) == false) {
				return null;
			}

			if (item.SyntaxNode != null) {
				if (item.Symbol != null) {
					item.RefreshSymbol();
				}
				else {
					item.SetSymbolToSyntaxNode();
				}
				if (item.Symbol != null) {
					var tip = ToolTipHelper.CreateToolTip(item.Symbol, ContainerType == SymbolListType.NodeList, sc);
					if (Config.Instance.NaviBarOptions.MatchFlags(NaviBarOptions.LineOfCode)) {
						tip.AddTextBlock()
							.Append(R.T_LineOfCode + (item.SyntaxNode.GetLineSpan().Length + 1).ToString());
					}
					return tip;
				}
				return ((Microsoft.CodeAnalysis.CSharp.SyntaxKind)item.SyntaxNode.RawKind).GetSyntaxBrief();
			}
			if (item.Symbol != null) {
				item.RefreshSymbol();
				var tip = ToolTipHelper.CreateToolTip(item.Symbol, false, sc);
				if (ContainerType == SymbolListType.SymbolReferrers && item.Location.IsInSource) {
					// append location info to tip
					ShowSourceReference(tip.AddTextBlock().Append(R.T_SourceReference).AppendLine(), item.Location);
				}
				return tip;
			}
			if (item.Location != null) {
				if (item.Location.IsInSource) {
					var f = item.Location.SourceTree.FilePath;
					return new ThemedToolTip(Path.GetFileName(f), String.Join(Environment.NewLine,
						R.T_Folder + Path.GetDirectoryName(f),
						R.T_Line + (item.Location.GetLineSpan().StartLinePosition.Line + 1).ToString(),
						R.T_Project + sc.GetDocument(item.Location.SourceTree)?.Project.Name
					));
				}
				else {
					return new ThemedToolTip(item.Location.MetadataModule.Name, R.T_ContainingAssembly + item.Location.MetadataModule.ContainingAssembly);
				}
			}
			return null;
		}

		static void ShowSourceReference(TextBlock text, Location location) {
			var sourceTree = location.SourceTree;
			var sourceSpan = location.SourceSpan;
			var sourceText = sourceTree.GetText();
			var t = sourceText.ToString(new TextSpan(Math.Max(sourceSpan.Start - 100, 0), Math.Min(sourceSpan.Start, 100)));
			int i = t.LastIndexOfAny(new[] { '\r', '\n' });
			text.Append(i != -1 ? t.Substring(i).TrimStart() : t.TrimStart())
				.Append(sourceText.ToString(sourceSpan), true);
			t = sourceText.ToString(new TextSpan(sourceSpan.End, Math.Min(sourceTree.Length - sourceSpan.End, 100)));
			i = t.IndexOfAny(new[] { '\r', '\n' });
			text.Append(i != -1 ? t.Substring(0, i).TrimEnd() : t.TrimEnd());
		}
		#endregion

		#region ISymbolFilterable
		SymbolFilterKind ISymbolFilterable.SymbolFilterKind {
			get => ContainerType == SymbolListType.TypeList ? SymbolFilterKind.Type
				: ContainerType == SymbolListType.SymbolReferrers ? SymbolFilterKind.Usage
				: ContainerType == SymbolListType.NodeList ? SymbolFilterKind.Node
				: SymbolFilterKind.Member;
		}

		void ISymbolFilterable.Filter(string[] keywords, int filterFlags) {
			switch (ContainerType) {
				case SymbolListType.TypeList:
					_Filter = FilterByTypeKinds(keywords, (MemberFilterTypes)filterFlags);
					break;
				case SymbolListType.Locations:
					_Filter = FilterByLocations(keywords);
					break;
				case SymbolListType.SymbolReferrers:
					_Filter = ((MemberFilterTypes)filterFlags).MatchFlags(MemberFilterTypes.AllUsages)
						? FilterByMemberTypes(keywords, (MemberFilterTypes)filterFlags)
						: FilterByUsages(keywords, (MemberFilterTypes)filterFlags);
					break;
				case SymbolListType.NodeList:
					_Filter = FilterByNodeTypes(keywords, (MemberFilterTypes)filterFlags);
					break;
				default:
					_Filter = FilterByMemberTypes(keywords, (MemberFilterTypes)filterFlags);
					break;
			}
			RefreshItemsSource();

			Predicate<object> FilterByNodeTypes(string[] k, MemberFilterTypes memberFilter) {
				var noKeyword = k.Length == 0;
				if (noKeyword && memberFilter == MemberFilterTypes.All) {
					return null;
				}
				if (noKeyword) {
					return o => SymbolFilterBox.FilterByImageId(memberFilter, ((SymbolItem)o).ImageId);
				}
				return o => SymbolFilterBox.FilterByImageId(memberFilter, ((SymbolItem)o).ImageId)
						&& MatchKeywords(((SymbolItem)o).Content.GetText(), k);
			}
			Predicate<object> FilterByMemberTypes(string[] k, MemberFilterTypes memberFilter) {
				var noKeyword = k.Length == 0;
				if (noKeyword && memberFilter == MemberFilterTypes.All) {
					return null;
				}
				if (noKeyword) {
					return o => SymbolFilterBox.FilterBySymbol(memberFilter, ((SymbolItem)o).Symbol);
				}
				return o => {
					var i = (SymbolItem)o;
					return SymbolFilterBox.FilterBySymbol(memberFilter, i.Symbol)
						&& MatchKeywords(i.Content.GetText(), k);
				};
			}
			Predicate<object> FilterByTypeKinds(string[] k, MemberFilterTypes typeFilter) {
				var noKeyword = k.Length == 0;
				if (noKeyword && typeFilter == MemberFilterTypes.All) {
					return null;
				}
				if (noKeyword) {
					return o => {
						var i = (SymbolItem)o;
						return i.Symbol != null && SymbolFilterBox.FilterBySymbolType(typeFilter, i.Symbol);
					};
				}
				return o => {
					var i = (SymbolItem)o;
					return i.Symbol != null
						&& SymbolFilterBox.FilterBySymbolType(typeFilter, i.Symbol)
						&& MatchKeywords(i.Content.GetText(), k);
				};
			}
			Predicate<object> FilterByLocations(string[] k) {
				if (k.Length == 0) {
					return null;
				}
				return o => {
					var i = (SymbolItem)o;
					return i.Location != null
						&& (MatchKeywords(((System.Windows.Documents.Run)i.Content.Inlines.FirstInline).Text, k)
								|| MatchKeywords(i.Hint, k));
				};
			}
			Predicate<object> FilterByUsages(string[] k, MemberFilterTypes filter) {
				var noKeyword = k.Length == 0;
				if (noKeyword && filter == MemberFilterTypes.All) {
					return null;
				}
				if (noKeyword) {
					return o => {
						var i = (SymbolItem)o;
						return SymbolFilterBox.FilterByUsage(filter, i)
							&& (i.Symbol != null ? SymbolFilterBox.FilterBySymbol(filter, i.Symbol) : SymbolFilterBox.FilterByImageId(filter, i.ImageId));
					};
				}
				return o => {
					var i = (SymbolItem)o;
					return SymbolFilterBox.FilterByUsage(filter, i)
						&& (i.Symbol != null
							? SymbolFilterBox.FilterBySymbol(filter, i.Symbol)
							: SymbolFilterBox.FilterByImageId(filter, i.ImageId))
						&& MatchKeywords(i.Content.GetText(), k);
				};
			}
			bool MatchKeywords(string text, string[] k) {
				var c = Char.IsUpper(k[0][0]) ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
				var m = 0;
				foreach (var item in k) {
					if ((m = text.IndexOf(item, m, c)) == -1) {
						return false;
					}
				}
				return true;
			}
		}
		#endregion

		#region Drag and drop
		protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e) {
			base.OnPreviewMouseLeftButtonDown(e);
			if (ContainerType != SymbolListType.NodeList) {
				return;
			}
			var item = GetMouseEventData(e);
			if (item != null && SemanticContext != null && item.SyntaxNode != null) {
				MouseMove -= BeginDragHandler;
				MouseMove += BeginDragHandler;
			}
		}

		SymbolItem GetMouseEventData(MouseEventArgs e) {
			return GetMouseEventTarget(e)?.Content as SymbolItem;
		}

		ListBoxItem GetItemFromPoint(Point point) {
			return (InputHitTest(point) as DependencyObject).GetParentOrSelf<ListBoxItem>();
		}

		ListBoxItem GetMouseEventTarget(MouseEventArgs e) {
			return GetItemFromPoint(e.GetPosition(this));
		}

		ListBoxItem GetDragEventTarget(DragEventArgs e) {
			return GetItemFromPoint(e.GetPosition(this));
		}

		static SymbolItem GetDragData(DragEventArgs e) {
			return e.Data.GetData(typeof(SymbolItem)) as SymbolItem;
		}

		void BeginDragHandler(object sender, MouseEventArgs e) {
			SymbolItem item;
			if (e.LeftButton == MouseButtonState.Pressed
				&& (item = GetMouseEventData(e)) != null
				&& item.SyntaxNode != null) {
				Handler(item, e);
			}

			async void Handler(SymbolItem i, MouseEventArgs args) {
				if (await SemanticContext.UpdateAsync(default).ConfigureAwait(true)) {
					i.RefreshSyntaxNode();
					var s = args.Source as FrameworkElement;
					MouseMove -= BeginDragHandler;
					DragOver += DragOverHandler;
					Drop += DropHandler;
					DragEnter += DragOverHandler;
					DragLeave += DragLeaveHandler;
					QueryContinueDrag += QueryContinueDragHandler;
					var r = DragDrop.DoDragDrop(s, i, DragDropEffects.Copy | DragDropEffects.Move);
					var t = Footer as TextBlock;
					if (t != null) {
						t.Text = null;
					}
					DragOver -= DragOverHandler;
					Drop -= DropHandler;
					DragEnter -= DragOverHandler;
					DragLeave -= DragLeaveHandler;
					QueryContinueDrag -= QueryContinueDragHandler;
				}
			}
		}

		void DragOverHandler(object sender, DragEventArgs e) {
			var li = GetDragEventTarget(e);
			SymbolItem target, source;
			// todo Enable dragging child before parent node
			if (li != null && (target = li.Content as SymbolItem)?.SyntaxNode != null
				&& (source = GetDragData(e)) != null && source != target
				&& (source.SyntaxNode.SyntaxTree.FilePath != target.SyntaxNode.SyntaxTree.FilePath
					|| source.SyntaxNode.Span.IntersectsWith(target.SyntaxNode.Span) == false)) {
				var copy = e.KeyStates.MatchFlags(DragDropKeyStates.ControlKey);
				e.Effects = copy ? DragDropEffects.Copy : DragDropEffects.Move;
				var t = Footer as TextBlock;
				if (t != null) {
					t.Text = (e.GetPosition(li).Y < li.ActualHeight / 2
							? (copy ? R.T_CopyBefore : R.T_MoveBefore)
							: (copy ? R.T_CopyAfter : R.T_MoveAfter)
							).Replace("<NAME>", target.SyntaxNode.GetDeclarationSignature());
				}
			}
			else {
				e.Effects = DragDropEffects.None;
			}
			e.Handled = true;
		}

		void DragLeaveHandler(object sender, DragEventArgs e) {
			var t = Footer as TextBlock;
			if (t != null) {
				t.Text = null;
			}
			e.Handled = true;
		}

		void DropHandler(object sender, DragEventArgs e) {
			var li = GetDragEventTarget(e);
			SymbolItem source, target;
			if (li != null && (target = li.Content as SymbolItem)?.SyntaxNode != null
				&& (source = GetDragData(e)) != null) {
				target.RefreshSyntaxNode();
				var copy = e.KeyStates.MatchFlags(DragDropKeyStates.ControlKey);
				var before = e.GetPosition(li).Y < li.ActualHeight / 2;
				SemanticContext.View.CopyOrMoveSyntaxNode(source.SyntaxNode, target.SyntaxNode, copy, before);
				e.Effects = copy ? DragDropEffects.Copy : DragDropEffects.Move;
			}
			else {
				e.Effects = DragDropEffects.None;
			}
			e.Handled = true;
		}

		void QueryContinueDragHandler(object sender, QueryContinueDragEventArgs e) {
			if (e.EscapePressed) {
				e.Action = DragAction.Cancel;
				e.Handled = true;
			}
		}

		#endregion

		public override void Dispose() {
			base.Dispose();
			if (SemanticContext != null) {
				UnhookMouseEventAndHideToolTip();
				_SymbolTip.PlacementTarget = null;
				ClearSymbols();
				if (ContextMenu is IDisposable d) {
					d.Dispose();
				}
				SelectedItem = null;
				ItemsSource = null;
				SemanticContext = null;
				IconProvider = null;
				ExtIconProvider = null;
			}
		}

	}
}
