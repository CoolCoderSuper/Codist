﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Reflection;
using AppHelpers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;

namespace Codist.Taggers
{
	[Export(typeof(IViewTaggerProvider))]
	[ContentType(Constants.CodeTypes.CSharp)]
	[TagType(typeof(IClassificationTag))]
	sealed class CSharpTaggerProvider : IViewTaggerProvider
	{
		public ITagger<T> CreateTagger<T>(ITextView textView, ITextBuffer buffer) where T : ITag {
			return Config.Instance.Features.MatchFlags(Features.SyntaxHighlight)
				? textView.Properties.GetOrCreateSingletonProperty(() => new CSharpTagger() as ITagger<T>)
				: null;
		}
	}

	/// <summary>A classifier for C# code syntax highlight.</summary>
	sealed class CSharpTagger : ITagger<IClassificationTag>
	{
		static readonly CSharpClassifications _Classifications = new CSharpClassifications(ServicesHelper.Instance.ClassificationTypeRegistry);
		static readonly GeneralClassifications _GeneralClassifications = new GeneralClassifications(ServicesHelper.Instance.ClassificationTypeRegistry);

		public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

		public IEnumerable<ITagSpan<IClassificationTag>> GetTags(NormalizedSnapshotSpanCollection spans) {
			ITextSnapshot snapshot = null;
			Workspace workspace = null;
			SemanticModel semanticModel = null;
			foreach (var span in spans) {
				if (snapshot != span.Snapshot) {
					snapshot = span.Snapshot;
					if (workspace == null) {
						workspace = snapshot.TextBuffer.GetWorkspace();
					}
					if (workspace == null) {
						yield break;
					}
					if (semanticModel == null) {
						semanticModel = SyncHelper.RunSync(() => workspace.GetDocument(span).GetSemanticModelAsync());
					}
				}

				var textSpan = new TextSpan(span.Start.Position, span.Length);
				var unitCompilation = semanticModel.SyntaxTree.GetCompilationUnitRoot();
				var classifiedSpans = Classifier.GetClassifiedSpans(semanticModel, textSpan, workspace);
				var lastTriviaSpan = default(TextSpan);
				SyntaxNode node;
				var r = GetAttributeNotationSpan(snapshot, textSpan, unitCompilation);
				if (r != null) {
					yield return r;
				}

				foreach (var item in classifiedSpans) {
					var ct = item.ClassificationType;
					switch (ct) {
						case "keyword":
						case Constants.CodeKeywordControl: {
							node = unitCompilation.FindNode(item.TextSpan, true, true);
							if (node is MemberDeclarationSyntax) {
								switch (unitCompilation.FindToken(item.TextSpan.Start).Kind()) {
									case SyntaxKind.SealedKeyword:
									case SyntaxKind.OverrideKeyword:
									case SyntaxKind.AbstractKeyword:
									case SyntaxKind.VirtualKeyword:
									case SyntaxKind.NewKeyword:
										yield return CreateClassificationSpan(snapshot, item.TextSpan, _Classifications.AbstractionKeyword);
										continue;
									case SyntaxKind.ThisKeyword:
										yield return CreateClassificationSpan(snapshot, item.TextSpan, _Classifications.Declaration);
										continue;
									case SyntaxKind.UnsafeKeyword:
										yield return CreateClassificationSpan(snapshot, item.TextSpan, _Classifications.ResourceKeyword);
										continue;
									case SyntaxKind.ExplicitKeyword:
									case SyntaxKind.ImplicitKeyword:
										yield return CreateClassificationSpan(snapshot, item.TextSpan, _GeneralClassifications.TypeCastKeyword);
										yield return CreateClassificationSpan(snapshot, (node as ConversionOperatorDeclarationSyntax).Type.Span, _Classifications.NestedDeclaration);
										continue;
								}
								continue;
							}
							switch (node.Kind()) {
								case SyntaxKind.BreakStatement:
									if (node.Parent is SwitchSectionSyntax) {
										continue;
									}
									goto case SyntaxKind.ReturnStatement;
								// highlights: return, yield return, yield break, throw and continue
								case SyntaxKind.ReturnKeyword:
								case SyntaxKind.GotoCaseStatement:
								case SyntaxKind.GotoDefaultStatement:
								case SyntaxKind.GotoStatement:
								case SyntaxKind.ContinueStatement:
								case SyntaxKind.ReturnStatement:
								case SyntaxKind.YieldReturnStatement:
								case SyntaxKind.YieldBreakStatement:
								case SyntaxKind.ThrowStatement:
								case SyntaxKind.ThrowExpression:
									yield return CreateClassificationSpan(snapshot, item.TextSpan, _GeneralClassifications.ControlFlowKeyword);
									continue;
								case SyntaxKind.IfStatement:
								case SyntaxKind.ElseClause:
								case SyntaxKind.SwitchStatement:
								case SyntaxKind.CaseSwitchLabel:
								case SyntaxKind.DefaultSwitchLabel:
									yield return CreateClassificationSpan(snapshot, item.TextSpan, _GeneralClassifications.BranchingKeyword);
									continue;
								case SyntaxKind.ForStatement:
								case SyntaxKind.ForEachStatement:
								case SyntaxKind.ForEachVariableStatement:
								case SyntaxKind.WhileStatement:
								case SyntaxKind.DoStatement:
								case SyntaxKind.SelectClause:
									yield return CreateClassificationSpan(snapshot, item.TextSpan, _GeneralClassifications.LoopKeyword);
									continue;
								case SyntaxKind.UsingStatement:
								case SyntaxKind.FixedStatement:
								case SyntaxKind.LockStatement:
								case SyntaxKind.UnsafeStatement:
								case SyntaxKind.TryStatement:
								case SyntaxKind.CatchClause:
								case SyntaxKind.CatchFilterClause:
								case SyntaxKind.FinallyClause:
								case SyntaxKind.StackAllocArrayCreationExpression:
								case (SyntaxKind)9053: // implicit stackalloc
									yield return CreateClassificationSpan(snapshot, item.TextSpan, _Classifications.ResourceKeyword);
									continue;
								case SyntaxKind.LocalDeclarationStatement:
									if (unitCompilation.FindToken(item.TextSpan.Start, true).Kind() == SyntaxKind.UsingKeyword) {
										goto case SyntaxKind.UsingStatement;
									}
									continue;
								case SyntaxKind.IsExpression:
								case SyntaxKind.IsPatternExpression:
								case SyntaxKind.AsExpression:
								case SyntaxKind.RefExpression:
								case SyntaxKind.RefType:
									yield return CreateClassificationSpan(snapshot, item.TextSpan, _GeneralClassifications.TypeCastKeyword);
									break;
								case SyntaxKind.Argument:
								case SyntaxKind.Parameter:
								case SyntaxKind.CrefParameter:
									switch (unitCompilation.FindToken(item.TextSpan.Start, true).Kind()) {
										case SyntaxKind.InKeyword:
										case SyntaxKind.OutKeyword:
										case SyntaxKind.RefKeyword:
											yield return CreateClassificationSpan(snapshot, item.TextSpan, _GeneralClassifications.TypeCastKeyword);
											continue;
									}
									break;
							}
							continue;
						}
						case "operator":
						case Constants.CodeOverloadedOperator: {
							node = unitCompilation.FindNode(item.TextSpan);
							if (node.RawKind == (int)SyntaxKind.DestructorDeclaration) {
								yield return CreateClassificationSpan(snapshot, item.TextSpan, _Classifications.Declaration);
								yield return CreateClassificationSpan(snapshot, item.TextSpan, _Classifications.ResourceKeyword);
								continue;
							}
							var opMethod = semanticModel.GetSymbol(node.IsKind(SyntaxKind.Argument) ? ((ArgumentSyntax)node).Expression : node) as IMethodSymbol;
							if (opMethod?.MethodKind == MethodKind.UserDefinedOperator) {
								if (node.RawKind == (int)SyntaxKind.OperatorDeclaration) {
									yield return CreateClassificationSpan(snapshot, item.TextSpan, _Classifications.Declaration);
								}
								yield return CreateClassificationSpan(snapshot, item.TextSpan, _Classifications.OverrideMember);
							}
							continue;
						}
						case Constants.CodePunctuation:
							if (item.TextSpan.Length == 1) {
								foreach (var t in ClassifyPunctuation(item.TextSpan, snapshot, semanticModel, unitCompilation)) {
									yield return t;
								}
							}
							continue;
						default:
							if (ct == Constants.XmlDocDelimiter) {
								if (lastTriviaSpan.Contains(item.TextSpan)) {
									continue;
								}
								var trivia = unitCompilation.FindTrivia(item.TextSpan.Start);
								switch (trivia.Kind()) {
									case SyntaxKind.SingleLineDocumentationCommentTrivia:
									case SyntaxKind.MultiLineDocumentationCommentTrivia:
									case SyntaxKind.DocumentationCommentExteriorTrivia:
										lastTriviaSpan = trivia.FullSpan;
										yield return CreateClassificationSpan(snapshot, lastTriviaSpan, _Classifications.XmlDoc);
										continue;
								}
							}
							else if (ct == Constants.CodeIdentifier
								//|| ct == Constants.CodeStaticSymbol
								|| ct.EndsWith("name", StringComparison.Ordinal)) {
								var itemSpan = item.TextSpan;
								node = unitCompilation.FindNode(itemSpan, true);
								foreach (var type in GetClassificationType(node, semanticModel)) {
									yield return CreateClassificationSpan(snapshot, itemSpan, type);
								}
							}
							break;
					}
				}
			}
		}

		static ITagSpan<IClassificationTag> GetAttributeNotationSpan(ITextSnapshot snapshot, TextSpan textSpan, CompilationUnitSyntax unitCompilation) {
			var spanNode = unitCompilation.FindNode(textSpan, true, false);
			if (spanNode.HasLeadingTrivia && spanNode.GetLeadingTrivia().FullSpan.Contains(textSpan)) {
				return null;
			}
			switch (spanNode.Kind()) {
				case SyntaxKind.AttributeArgument:
				case SyntaxKind.AttributeList:
				case SyntaxKind.AttributeArgumentList:
					return CreateClassificationSpan(snapshot, textSpan, _Classifications.AttributeNotation);
			}
			return null;
		}

		static IEnumerable<ITagSpan<IClassificationTag>> ClassifyPunctuation(TextSpan itemSpan, ITextSnapshot snapshot, SemanticModel semanticModel, CompilationUnitSyntax unitCompilation) {
			if (HighlightOptions.AllBraces == false) {
				yield break;
			}
			var s = snapshot.GetText(itemSpan.Start, itemSpan.Length)[0];
			if (s == '{' || s == '}') {
				var node = unitCompilation.FindNode(itemSpan, true, true);
				if (node is BaseTypeDeclarationSyntax == false
					&& node is ExpressionSyntax == false
					&& node is NamespaceDeclarationSyntax == false
					&& node.Kind() != SyntaxKind.SwitchStatement && (node = node.Parent) == null) {
					yield break;
				}
				var type = ClassifySyntaxNode(node);
				if (type != null) {
					if (HighlightOptions.SpecialPunctuation) {
						yield return CreateClassificationSpan(snapshot, itemSpan, _GeneralClassifications.SpecialPunctuation);
					}
					if (type == _GeneralClassifications.BranchingKeyword) {
						if (HighlightOptions.BranchBrace) {
							yield return CreateClassificationSpan(snapshot, itemSpan, type);
						}
						yield break;
					}
					if (type == _GeneralClassifications.LoopKeyword) {
						if (HighlightOptions.LoopBrace) {
							yield return CreateClassificationSpan(snapshot, itemSpan, type);
						}
						yield break;
					}
					if (type == _Classifications.ResourceKeyword) {
						if (HighlightOptions.ResourceBrace) {
							yield return CreateClassificationSpan(snapshot, itemSpan, type);
						}
						yield break;
					}
					if (node is ExpressionSyntax == false) {
						yield return CreateClassificationSpan(snapshot, itemSpan, _Classifications.DeclarationBrace);
					}
					if (HighlightOptions.DeclarationBrace) {
						yield return CreateClassificationSpan(snapshot, itemSpan, type);
					}
				}
			}
			else if ((s == '(' || s == ')') && HighlightOptions.AllParentheses) {
				var node = unitCompilation.FindNode(itemSpan, true, true);
				ISymbol symbol;
				ClassificationTag tag;
				switch (node.Kind()) {
					case SyntaxKind.CastExpression:
						if (HighlightOptions.CastBrace == false) {
							yield break;
						}
						symbol = semanticModel.GetSymbolInfo(((CastExpressionSyntax)node).Type).Symbol;
						if (symbol == null) {
							yield break;
						}
						tag = GetClassificationType(symbol);
						if (tag != null) {
							if (HighlightOptions.SpecialPunctuation) {
								yield return CreateClassificationSpan(snapshot, itemSpan, _GeneralClassifications.SpecialPunctuation);
							}
							yield return CreateClassificationSpan(snapshot, itemSpan, tag);
						}
						break;
					case SyntaxKind.ParenthesizedExpression:
						if (HighlightOptions.CastBrace == false) {
							yield break;
						}
						if (node.ChildNodes().FirstOrDefault().IsKind(SyntaxKind.AsExpression)) {
							symbol = semanticModel.GetSymbolInfo(((BinaryExpressionSyntax)node.ChildNodes().First()).Right).Symbol;
							if (symbol == null) {
								yield break;
							}
							tag = GetClassificationType(symbol);
							if (tag != null) {
								if (HighlightOptions.SpecialPunctuation) {
									yield return CreateClassificationSpan(snapshot, itemSpan, _GeneralClassifications.SpecialPunctuation);
								}
								yield return CreateClassificationSpan(snapshot, itemSpan, tag);
								yield break;
							}
						}
						break;
					case SyntaxKind.SwitchStatement:
					case SyntaxKind.SwitchSection:
					case SyntaxKind.IfStatement:
					case SyntaxKind.ElseClause:
						foreach(var i in MarkClassificationTypeForBrace(itemSpan, snapshot, _GeneralClassifications.BranchingKeyword, HighlightOptions.BranchBrace)) {
							yield return i;
						}
						yield break;
					case SyntaxKind.ForStatement:
					case SyntaxKind.ForEachStatement:
					case SyntaxKind.ForEachVariableStatement:
					case SyntaxKind.WhileStatement:
					case SyntaxKind.DoStatement:
						foreach (var i in MarkClassificationTypeForBrace(itemSpan, snapshot, _GeneralClassifications.LoopKeyword, HighlightOptions.LoopBrace)) {
							yield return i;
						}
						yield break;
					case SyntaxKind.UsingStatement:
					case SyntaxKind.FixedStatement:
					case SyntaxKind.LockStatement:
					case SyntaxKind.UnsafeStatement:
					case SyntaxKind.TryStatement:
					case SyntaxKind.CatchDeclaration:
					case SyntaxKind.CatchClause:
					case SyntaxKind.CatchFilterClause:
					case SyntaxKind.FinallyClause:
						foreach (var i in MarkClassificationTypeForBrace(itemSpan, snapshot, _Classifications.ResourceKeyword, HighlightOptions.ResourceBrace)) {
							yield return i;
						}
						yield break;
					case SyntaxKind.TupleExpression:
						foreach (var i in MarkClassificationTypeForBrace(itemSpan, snapshot, _Classifications.ConstructorMethod, HighlightOptions.ParameterBrace)) {
							yield return i;
						}
						yield break;
				}
				if (HighlightOptions.SpecialPunctuation | HighlightOptions.ParameterBrace) {
					node = (node as BaseArgumentListSyntax
					   ?? node as BaseParameterListSyntax
					   ?? (CSharpSyntaxNode)(node as CastExpressionSyntax)
					   )?.Parent;
					if (node != null) {
						var type = ClassifySyntaxNode(node);
						if (type != null) {
							foreach (var i in MarkClassificationTypeForBrace(itemSpan, snapshot, type, HighlightOptions.ParameterBrace)) {
								yield return i;
							}
						}
					}
				}
			}
			else if (s == '[' || s == ']') {
				// highlight attribute annotation
				var node = unitCompilation.FindNode(itemSpan, true, false);
				if (node.IsKind(SyntaxKind.AttributeList)) {
					yield return CreateClassificationSpan(snapshot, node.Span, _Classifications.AttributeNotation);
				}
			}

			ClassificationTag GetClassificationType(ISymbol symbol) {
				switch (symbol.Kind) {
					case SymbolKind.NamedType:
						switch (((INamedTypeSymbol)symbol).TypeKind) {
							case TypeKind.Class: return _Classifications.ClassName;
							case TypeKind.Interface: return _Classifications.InterfaceName;
							case TypeKind.Struct: return _Classifications.StructName;
							case TypeKind.Delegate: return _Classifications.DelegateName;
							case TypeKind.Enum: return _Classifications.EnumName;
						}
						break;
					case SymbolKind.ArrayType: return _Classifications.ClassName;
					case SymbolKind.Event: return _Classifications.Event;
					case SymbolKind.PointerType: return _Classifications.StructName;
					case SymbolKind.TypeParameter:
						var p = (ITypeParameterSymbol)symbol;
						foreach (var c in p.ConstraintTypes) {
							switch (c.SpecialType) {
								case SpecialType.System_Enum: return _Classifications.EnumName;
								case SpecialType.System_Delegate: return _Classifications.DelegateName;
							}
							return _Classifications.ClassName;
						}
						if (p.HasReferenceTypeConstraint) {
							return _Classifications.ClassName;
						}
						if (p.HasValueTypeConstraint || p.HasUnmanagedTypeConstraint) {
							return _Classifications.StructName;
						}
						return _Classifications.TypeParameter;
				}
				return null;
			}
		}

		static IEnumerable<ITagSpan<IClassificationTag>> MarkClassificationTypeForBrace(TextSpan itemSpan, ITextSnapshot snapshot, ClassificationTag tag, bool option) {
			if (HighlightOptions.SpecialPunctuation) {
				yield return CreateClassificationSpan(snapshot, itemSpan, _GeneralClassifications.SpecialPunctuation);
			}
			if (option) {
				yield return CreateClassificationSpan(snapshot, itemSpan, tag);
			}
		}

		static ClassificationTag ClassifySyntaxNode(SyntaxNode node) {
			const SyntaxKind SwitchExpression = (SyntaxKind)9025;

			switch (node.Kind()) {
				case SyntaxKind.MethodDeclaration:
				case SyntaxKind.AnonymousMethodExpression:
				case SyntaxKind.SimpleLambdaExpression:
				case SyntaxKind.ParenthesizedLambdaExpression:
				case SyntaxKind.LocalFunctionStatement:
				case SyntaxKind.ConversionOperatorDeclaration:
				case SyntaxKind.OperatorDeclaration:
					return _Classifications.Method;
				case SyntaxKind.InvocationExpression:
					return ((((InvocationExpressionSyntax)node).Expression as IdentifierNameSyntax)?.Identifier.ValueText == "nameof") ? null : _Classifications.Method;
				case SyntaxKind.ConstructorDeclaration:
				case SyntaxKind.BaseConstructorInitializer:
				case SyntaxKind.AnonymousObjectCreationExpression:
				case SyntaxKind.ObjectInitializerExpression:
				case SyntaxKind.ObjectCreationExpression:
				case SyntaxKind.ComplexElementInitializerExpression:
				case SyntaxKind.CollectionInitializerExpression:
				case SyntaxKind.ArrayInitializerExpression:
				case SyntaxKind.ThisConstructorInitializer:
				case SyntaxKind.DestructorDeclaration:
					return _Classifications.ConstructorMethod;
				case SyntaxKind.IndexerDeclaration:
				case SyntaxKind.PropertyDeclaration: return _Classifications.Property;
				case SyntaxKind.ClassDeclaration: return _Classifications.ClassName;
				case SyntaxKind.InterfaceDeclaration: return _Classifications.InterfaceName;
				case SyntaxKind.EnumDeclaration: return _Classifications.EnumName;
				case SyntaxKind.StructDeclaration: return _Classifications.StructName;
				case SyntaxKind.Attribute: return _Classifications.AttributeName;
				case SyntaxKind.EventDeclaration: return _Classifications.Event;
				case SyntaxKind.DelegateDeclaration: return _Classifications.DelegateName;
				case SyntaxKind.NamespaceDeclaration:
					return _Classifications.Namespace;
				case SyntaxKind.IfStatement:
				case SyntaxKind.ElseClause:
				case SyntaxKind.SwitchStatement:
				case SyntaxKind.SwitchSection:
				case SwitchExpression:
					return _GeneralClassifications.BranchingKeyword;
				case SyntaxKind.ForStatement:
				case SyntaxKind.ForEachStatement:
				case SyntaxKind.ForEachVariableStatement:
				case SyntaxKind.WhileStatement:
				case SyntaxKind.DoStatement:
					return _GeneralClassifications.LoopKeyword;
				case SyntaxKind.UsingStatement:
				case SyntaxKind.LockStatement:
				case SyntaxKind.FixedStatement:
				case SyntaxKind.UnsafeStatement:
				case SyntaxKind.TryStatement:
				case SyntaxKind.CatchClause:
				case SyntaxKind.CatchFilterClause:
				case SyntaxKind.FinallyClause:
					return _Classifications.ResourceKeyword;
			}
			return null;
		}

		static IEnumerable<ClassificationTag> GetClassificationType(SyntaxNode node, SemanticModel semanticModel) {
			node = node.Kind() == SyntaxKind.Argument ? ((ArgumentSyntax)node).Expression : node;
			//System.Diagnostics.Debug.WriteLine(node.GetType().Name + node.Span.ToString());
			var symbol = semanticModel.GetSymbolInfo(node).Symbol;
			if (symbol == null) {
				symbol = semanticModel.GetDeclaredSymbol(node);
				if (symbol != null) {
					switch (symbol.Kind) {
						case SymbolKind.NamedType:
							yield return symbol.ContainingType != null ? _Classifications.NestedDeclaration : _Classifications.Declaration;
							break;
						case SymbolKind.Event:
							yield return _Classifications.NestedDeclaration;
							break;
						case SymbolKind.Method:
							if (HighlightOptions.LocalFunctionDeclaration
								|| ((IMethodSymbol)symbol).MethodKind != MethodKind.LocalFunction) {
								yield return _Classifications.NestedDeclaration;
							}
							break;
						case SymbolKind.Property:
							if (symbol.ContainingType.IsAnonymousType == false) {
								yield return _Classifications.NestedDeclaration;
							}
							break;
						case SymbolKind.Field:
							if (node.IsKind(SyntaxKind.TupleElement)) {
								if (((TupleElementSyntax)node).Identifier.IsKind(SyntaxKind.None)) {
									symbol = semanticModel.GetTypeInfo(((TupleElementSyntax)node).Type).Type;
								}
							}
							else if (HighlightOptions.NonPrivateField
								&& symbol.DeclaredAccessibility >= Accessibility.ProtectedAndInternal
								&& symbol.ContainingType.TypeKind != TypeKind.Enum) {
								yield return _Classifications.NestedDeclaration;
							}
							break;
						case SymbolKind.Local:
							yield return _Classifications.LocalDeclaration;
							break;
					}
				}
				else {
					// NOTE: handle alias in using directive
					if ((node.Parent as NameEqualsSyntax)?.Parent is UsingDirectiveSyntax) {
						yield return _Classifications.AliasNamespace;
					}
					else if (node is AttributeArgumentSyntax) {
						symbol = semanticModel.GetSymbolInfo(((AttributeArgumentSyntax)node).Expression).Symbol;
						if (symbol != null && symbol.Kind == SymbolKind.Field && (symbol as IFieldSymbol)?.IsConst == true) {
							yield return _Classifications.ConstField;
							yield return _Classifications.StaticMember;
						}
					}
					symbol = node.Parent is MemberAccessExpressionSyntax ? semanticModel.GetSymbolInfo(node.Parent).CandidateSymbols.FirstOrDefault()
						: node.Parent.IsKind(SyntaxKind.Argument) ? semanticModel.GetSymbolInfo(((ArgumentSyntax)node.Parent).Expression).CandidateSymbols.FirstOrDefault()
						: node.IsKind(SyntaxKind.SimpleBaseType) ? semanticModel.GetTypeInfo(((SimpleBaseTypeSyntax)node).Type).Type
						: node.IsKind(SyntaxKind.TypeConstraint) ? semanticModel.GetTypeInfo(((TypeConstraintSyntax)node).Type).Type
						: null;
					if (symbol == null) {
						yield break;
					}
				}
			}
			switch (symbol.Kind) {
				case SymbolKind.Alias:
				case SymbolKind.ArrayType:
				case SymbolKind.Assembly:
				case SymbolKind.DynamicType:
				case SymbolKind.ErrorType:
				case SymbolKind.NetModule:
				case SymbolKind.PointerType:
				case SymbolKind.RangeVariable:
				case SymbolKind.Preprocessing:
					//case SymbolKind.Discard:
					yield break;

				case SymbolKind.Label:
					yield return _Classifications.Label;
					yield break;

				case SymbolKind.TypeParameter:
					yield return _Classifications.TypeParameter;
					yield break;

				case SymbolKind.Field:
					var f = symbol as IFieldSymbol;
					yield return f.IsConst ?
							f.ContainingType.TypeKind == TypeKind.Enum ? _Classifications.EnumField
							: _Classifications.ConstField
						: f.IsReadOnly ? _Classifications.ReadonlyField
						: f.IsVolatile ? _Classifications.VolatileField
						: _Classifications.Field;
					break;

				case SymbolKind.Property:
					yield return _Classifications.Property;
					break;

				case SymbolKind.Event:
					yield return _Classifications.Event;
					break;

				case SymbolKind.Local:
					var localSymbol = symbol as ILocalSymbol;
					yield return localSymbol.IsConst ? _Classifications.ConstField : _Classifications.LocalVariable;
					break;

				case SymbolKind.Namespace:
					yield return _Classifications.Namespace;
					yield break;

				case SymbolKind.Parameter:
					yield return _Classifications.Parameter;
					break;

				case SymbolKind.Method:
					var methodSymbol = symbol as IMethodSymbol;
					switch (methodSymbol.MethodKind) {
						case MethodKind.Constructor:
							yield return
								node is AttributeSyntax || node.Parent is AttributeSyntax || node.Parent?.Parent is AttributeSyntax
									? _Classifications.AttributeName
									: _Classifications.ConstructorMethod;
							break;
						case MethodKind.Destructor:
						case MethodKind.StaticConstructor:
							yield return _Classifications.ConstructorMethod;
							break;
						default:
							yield return methodSymbol.IsExtensionMethod ? _Classifications.ExtensionMethod
								: methodSymbol.IsExtern ? _Classifications.ExternMethod
								: _Classifications.Method;
							break;
					}
					break;

				case SymbolKind.NamedType:
					break;

				default:
					yield break;
			}

			if (SymbolMarkManager.HasBookmark) {
				var markerStyle = SymbolMarkManager.GetSymbolMarkerStyle(symbol);
				if (markerStyle != null) {
					yield return markerStyle;
				}
			}

			if (SyntaxHighlight.FormatStore.IdentifySymbolSource && symbol.IsMemberOrType() && symbol.ContainingAssembly != null) {
				yield return symbol.ContainingAssembly.GetSourceType() == AssemblySource.Metadata
					? _Classifications.MetadataSymbol
					: _Classifications.UserSymbol;
			}

			if (symbol.IsStatic) {
				if (symbol.Kind != SymbolKind.Namespace) {
					yield return _Classifications.StaticMember;
				}
			}
			else if (symbol.IsSealed) {
				if (symbol.Kind == SymbolKind.NamedType && ((ITypeSymbol)symbol).TypeKind != TypeKind.Class) {
					yield break;
				}
				yield return _Classifications.SealedMember;
			}
			else if (symbol.IsOverride) {
				yield return _Classifications.OverrideMember;
			}
			else if (symbol.IsVirtual) {
				yield return _Classifications.VirtualMember;
			}
			else if (symbol.IsAbstract) {
				yield return _Classifications.AbstractMember;
			}
		}

		static ITagSpan<IClassificationTag> CreateClassificationSpan(ITextSnapshot snapshotSpan, TextSpan span, ClassificationTag tag) {
			return new TagSpan<IClassificationTag>(new SnapshotSpan(snapshotSpan, span.Start, span.Length), tag);
		}

		static class HighlightOptions
		{
			static bool _dummy = Init();
			// use fields to cache option flags
			public static bool AllBraces, AllParentheses, SpecialPunctuation, BranchBrace, CastBrace, LoopBrace, ParameterBrace, ResourceBrace, DeclarationBrace, LocalFunctionDeclaration, NonPrivateField;

			static bool Init() {
				Config.Updated += Update;
				Update(Config.Instance, new ConfigUpdatedEventArgs(Features.SyntaxHighlight));
				return true;
			}

			static void Update(object sender, ConfigUpdatedEventArgs e) {
				if (e.UpdatedFeature.MatchFlags(Features.SyntaxHighlight) == false) {
					return;
				}
				var o = (sender as Config).SpecialHighlightOptions;
				AllBraces = o.HasAnyFlag(SpecialHighlightOptions.AllBraces);
				AllParentheses = o.HasAnyFlag(SpecialHighlightOptions.AllParentheses);
				SpecialPunctuation = o.MatchFlags(SpecialHighlightOptions.SpecialPunctuation);
				BranchBrace = o.MatchFlags(SpecialHighlightOptions.BranchBrace);
				CastBrace = o.MatchFlags(SpecialHighlightOptions.CastBrace);
				LoopBrace = o.MatchFlags(SpecialHighlightOptions.LoopBrace);
				ResourceBrace = o.MatchFlags(SpecialHighlightOptions.ResourceBrace);
				ParameterBrace = o.MatchFlags(SpecialHighlightOptions.ParameterBrace);
				DeclarationBrace = o.MatchFlags(SpecialHighlightOptions.DeclarationBrace);
				LocalFunctionDeclaration = o.MatchFlags(SpecialHighlightOptions.LocalFunctionDeclaration);
				NonPrivateField = o.MatchFlags(SpecialHighlightOptions.NonPrivateField);
			}
		}
	}
}