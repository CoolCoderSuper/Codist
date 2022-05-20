﻿using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;

namespace Codist.QuickInfo
{
	sealed class QuickInfoBackgroundController : IAsyncQuickInfoSource
	{
		ITextBuffer _TextBuffer;
		Brush _Background;

		public QuickInfoBackgroundController(ITextBuffer textBuffer) {
			_TextBuffer = textBuffer;
			UpdateBackgroundBrush();
			Config.RegisterUpdateHandler(ConfigUpdated);
		}

		void ConfigUpdated(ConfigUpdatedEventArgs obj) {
			UpdateBackgroundBrush();
		}

		void UpdateBackgroundBrush() {
			var bc = Config.Instance.QuickInfo.BackgroundColor;
			if (String.IsNullOrEmpty(bc) == false && bc != Constants.EmptyColor) {
				UIHelper.ParseColor(bc, out var c, out var o);
				_Background = new SolidColorBrush(c);
				if (o > 0) {
					_Background.Opacity = o / 255d;
				}
			}
			else {
				_Background = null;
			}
		}

		public async Task<QuickInfoItem> GetQuickInfoItemAsync(IAsyncQuickInfoSession session, CancellationToken cancellationToken) {
			await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
			return Keyboard.Modifiers != ModifierKeys.Control && _Background != null
				? new QuickInfoItem(null, new BackgroundController(_Background))
				: null;
		}

		void IDisposable.Dispose() {
			if (_TextBuffer != null) {
				_TextBuffer.Properties.RemoveProperty(typeof(QuickInfoBackgroundController));
				_TextBuffer = null;
			}
			Config.UnregisterUpdateHandler(ConfigUpdated);
		}

		sealed class BackgroundController : UserControl
		{
			readonly Brush _Brush;

			public BackgroundController(Brush brush) {
				_Brush = brush;
			}

			protected override void OnVisualParentChanged(DependencyObject oldParent) {
				base.OnVisualParentChanged(oldParent);
				var p = this.GetParent<UserControl>(n => n.GetType().Name == "WpfToolTipControl");
				if (p != null && _Brush != null) {
					p.Background = _Brush;
				}
			}
		}
	}
}