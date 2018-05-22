﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;
using Microsoft.CodeAnalysis;

namespace Codist.QuickInfo
{
	static class SymbolInfoRenderer
	{
		public static TextBlock ShowSymbolDeclaration(ISymbol symbol, SymbolFormatter formatter) {
			var info = new TextBlock()
				.AddText(symbol.GetAccessibility(), formatter.Keyword);
			if (symbol.Kind == SymbolKind.Field) {
				ShowFieldDeclaration(symbol as IFieldSymbol, formatter, info);
			}
			else {
				ShowSymbolDeclaration(symbol, formatter, info);
			}
			info.AddText(symbol.GetTypeName(), symbol.Kind == SymbolKind.NamedType ? formatter.Keyword : null)
				.AddText(" ");
			return info;
		}

		static void ShowFieldDeclaration(IFieldSymbol field, SymbolFormatter formatter, TextBlock info) {
			if (field.IsConst) {
				info.AddText("const ", formatter.Keyword);
			}
			else {
				if (field.IsStatic) {
					info.AddText("static ", formatter.Keyword);
				}
				if (field.IsReadOnly) {
					info.AddText("readonly ", formatter.Keyword);
				}
				else if (field.IsVolatile) {
					info.AddText("volatile ", formatter.Keyword);
				}
			}
		}

		static void ShowSymbolDeclaration(ISymbol symbol, SymbolFormatter formatter, TextBlock info) {
			if (symbol.IsAbstract) {
				info.AddText("abstract ", formatter.Keyword);
			}
			else if (symbol.IsStatic) {
				info.AddText("static ", formatter.Keyword);
			}
			else if (symbol.IsVirtual) {
				info.AddText("virtual ", formatter.Keyword);
			}
			else if (symbol.IsOverride) {
				info.AddText(symbol.IsSealed ? "sealed override " : "override ", formatter.Keyword);
				INamedTypeSymbol t = null;
				switch (symbol.Kind) {
					case SymbolKind.Method: t = ((IMethodSymbol)symbol).OverriddenMethod?.ContainingType; break;
					case SymbolKind.Property: t = ((IPropertySymbol)symbol).OverriddenProperty?.ContainingType; break;
					case SymbolKind.Event: t = ((IEventSymbol)symbol).OverriddenEvent?.ContainingType; break;
				}
				if (t != null && t.IsCommonClass() == false) {
					info.AddSymbol(t, null, formatter).AddText(" ");
				}
			}
			else if (symbol.IsSealed && (symbol.Kind == SymbolKind.NamedType && (symbol as INamedTypeSymbol).TypeKind == TypeKind.Class || symbol.Kind == SymbolKind.Method)) {
				info.AddText("sealed ", formatter.Keyword);
			}
			if (symbol.IsExtern) {
				info.AddText("extern ", formatter.Keyword);
			}
		}
	}
}
