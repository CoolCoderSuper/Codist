﻿using System;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using AppHelpers;
using Microsoft.VisualStudio.Text.Operations;
using System.Threading.Tasks;

namespace Codist.NaviBar
{
	interface INaviBar
	{
		void ShowRootItemMenu(int parameter);
		void ShowActiveItemMenu();
	}

	/// <summary>
	/// Overrides default navigator to editor.
	/// </summary>
	[Export(typeof(IWpfTextViewCreationListener))]
	[ContentType(Constants.CodeTypes.Code)]
	[TextViewRole(PredefinedTextViewRoles.Document)]
	sealed class NaviBarFactory : IWpfTextViewCreationListener
	{
#pragma warning disable 649, 169

		/// <summary>
		/// Defines the adornment layer for syntax node range highlight.
		/// </summary>
		[Export(typeof(AdornmentLayerDefinition))]
		[Name(nameof(CSharpBar.SyntaxNodeRange))]
		[Order(After = PredefinedAdornmentLayers.CurrentLineHighlighter)]
		AdornmentLayerDefinition _SyntaxNodeRangeAdormentLayer;

		[Import(typeof(ITextSearchService2))]
		ITextSearchService2 _TextSearchService;

#pragma warning restore 649, 169

		public void TextViewCreated(IWpfTextView textView) {
			if (Config.Instance.Features.MatchFlags(Features.NaviBar)
				&& textView.Roles.Contains("DIFF") == false) {
				if (textView.TextBuffer.ContentType.IsOfType(Constants.CodeTypes.CSharp)
					|| textView.TextBuffer.LikeContentType(Constants.CodeTypes.Markdown)) {
					SemanticContext.GetOrCreateSingetonInstance(textView);
					new Overrider(textView, _TextSearchService);
				}
#if DEBUG
				else {
					AssociateFileCodeModelOverrider();
				}
#endif
			}
		}

#if DEBUG
		static void AssociateFileCodeModelOverrider() {
			Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
			var model = CodistPackage.DTE2.ActiveDocument?.ProjectItem?.FileCodeModel; // the active document can be null
			if (model == null) {
				return;
			}
			foreach (EnvDTE80.CodeElement2 item in model.CodeElements) {
				System.Diagnostics.Debug.WriteLine(item.Name + "," + item.Kind + "," + item.StartPoint.Line + "," + item.StartPoint.LineCharOffset);
				if (item.IsCodeType && item.Kind != EnvDTE.vsCMElement.vsCMElementDelegate) {
					var ct = (item as EnvDTE.CodeType).Members;
					for (int i = 1; i <= ct.Count; i++) {
						var member = ct.Item(i);
						System.Diagnostics.Debug.WriteLine(member.Name + "," + member.Kind + "," + member.StartPoint.Line + "," + member.StartPoint.LineCharOffset);
					}
				}
			}
		}
#endif

		sealed class Overrider
		{
			readonly IWpfTextView _View;
			readonly ITextSearchService2 _TextSearch;

			public Overrider(IWpfTextView view, ITextSearchService2 textSearch) {
				_View = view;
				_TextSearch = textSearch;
				view.VisualElement.Loaded += AddNaviBar;
			}

			void AddNaviBar(object sender, RoutedEventArgs e) {
				_View.VisualElement.Loaded -= AddNaviBar;

				var view = sender as IWpfTextView ?? _View;
				NaviBar naviBar;
				if ((naviBar = view.VisualElement?.GetParent<Grid>().GetFirstVisualChild<NaviBar>()) != null) {
					naviBar.BindView(view);
					return;
				}
				var naviBarHolder = view.VisualElement
					?.GetParent<Border>(b => b.Name == "PART_ContentPanel")
					?.GetFirstVisualChild<Border>(b => b.Name == "DropDownBarMargin");
				if (naviBarHolder == null) {
					var viewHost = view.VisualElement.GetParent<Panel>(b => b.GetType().Name == "WpfMultiViewHost");
					if (viewHost != null) {
						var b = new MarkdownBar(_View, _TextSearch);
						DockPanel.SetDock(b, Dock.Top);
						if (viewHost.Children.Count == 1) {
							viewHost.Children.Insert(0, b);
						}
						else {
							var c = viewHost.Children[0] as ContentControl;
							if (c != null && c.Content == null) {
								c.Content = b;
							}
						}
					}
					return;
				}
				var dropDown1 = naviBarHolder.GetFirstVisualChild<ComboBox>(c => c.Name == "DropDown1");
				var dropDown2 = naviBarHolder.GetFirstVisualChild<ComboBox>(c => c.Name == "DropDown2");
				if (dropDown1 == null || dropDown2 == null) {
					return;
				}
				var container = dropDown1.GetParent<Grid>();
				if (container == null) {
					return;
				}
				var bar = new CSharpBar(_View) {
					MinWidth = 200
				};
				bar.SetCurrentValue(Grid.ColumnProperty, 2);
				bar.SetCurrentValue(Grid.ColumnSpanProperty, 3);
				container.Children.Add(bar);
				dropDown1.Visibility = Visibility.Hidden;
				dropDown2.Visibility = Visibility.Hidden;
				naviBarHolder.Unloaded += ResurrectNaviBar_OnUnloaded;
			}

			// Fixes https://github.com/wmjordan/Codist/issues/131
			async void ResurrectNaviBar_OnUnloaded(object sender, RoutedEventArgs e) {
				var naviBar = sender as Border;
				if (naviBar != null) {
					naviBar.Unloaded -= ResurrectNaviBar_OnUnloaded;
				}
				if (_View.IsClosed) {
					return;
				}
				await Task.Delay(1000).ConfigureAwait(false);
				await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(default);
				if (_View.VisualElement.IsVisible && _View.Properties.ContainsProperty(nameof(NaviBar)) == false) {
					AddNaviBar(_View, e);
				}
			}
		}
	}
}
