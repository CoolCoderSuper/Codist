﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AppHelpers;
using Codist.SyntaxHighlight;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.Text;
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
			if (typeof(T) == typeof(IClassificationTag) && Config.Instance.Features.MatchFlags(Features.SyntaxHighlight)) {
				return textView.Properties.GetOrCreateSingletonProperty(typeof(CSharpTagger), () => new CSharpTagger(buffer) as ITagger<T>);
			}
			return null;
		}
	}

	/// <summary>A classifier for C# code syntax highlight.</summary>
	sealed class CSharpTagger : ITagger<IClassificationTag>, IDisposable
	{
		static readonly CSharpClassifications _Classifications = CSharpClassifications.Instance;
		static readonly GeneralClassifications _GeneralClassifications = GeneralClassifications.Instance;
		readonly ConditionalWeakTable<ITextSnapshot, ParserTask> _ParserTasks = new ConditionalWeakTable<ITextSnapshot, ParserTask>();
		readonly object _SyncRoot = new object();
		readonly ITextBuffer _Buffer;
		CancellationTokenSource _TaskBreaker;
		ParserTask _LastFinishedTask;

		public CSharpTagger(ITextBuffer buffer) {
			_Buffer = buffer;
			if (buffer is ITextBuffer2 b) {
				b.ChangedOnBackground += TextBuffer_ChangedOnBackground;
			}
		}

		/// <summary>This event is to notify the IDE that some tags are classified (from the parser thread).</summary>
		public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

		public IEnumerable<ITagSpan<IClassificationTag>> GetTags(NormalizedSnapshotSpanCollection spans) {
			if (spans.Count == 0) {
				return Array.Empty<ITagSpan<IClassificationTag>>();
			}
			var snapshot = spans[0].Snapshot;
			var textBuffer = snapshot.TextBuffer;
			if (_ParserTasks.TryGetValue(snapshot, out var task)) {
				if (task.Model != null) {
					return Parser.GetTags(spans, task.Workspace, task.Model, snapshot);
				}
				// the snapshot is still under parsing
				task.AddPendingSpans(spans);
			}
			var last = _LastFinishedTask;
			if (task == null && (last == null || snapshot != last.Snapshot)) {
				task = new ParserTask(snapshot, this, spans);
				task.StartParse(snapshot.Version.VersionNumber > 0 ? SyncHelper.CancelAndRetainToken(ref _TaskBreaker) : default);
				_ParserTasks.Add(snapshot, task);
			}
			if (last != null && last.Snapshot.TextBuffer == snapshot.TextBuffer) {
				return UseOldResult(spans, snapshot, last);
			}

			return Array.Empty<ITagSpan<IClassificationTag>>();
		}

		void RemoveParser(ParserTask task) {
			_ParserTasks.Remove(task.Snapshot);
		}

		void TextBuffer_ChangedOnBackground(object sender, TextContentChangedEventArgs e) {
			var changedSpan = new SnapshotSpan(e.After, e.Changes[0].NewPosition, new SnapshotPoint(e.After, e.Changes[e.Changes.Count - 1].NewSpan.End));
			Debug.WriteLine("Buffer changed: " + changedSpan);
			TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(changedSpan));
			//Reclassify(args.After);
		}

		static IEnumerable<ITagSpan<IClassificationTag>> UseOldResult(NormalizedSnapshotSpanCollection spans, ITextSnapshot snapshot, ParserTask last) {
			var old = last.Snapshot;
			return Parser.GetTags(MapToOldSpans(snapshot, spans, old), last.Workspace, last.Model, old);
		}

		static IEnumerable<SnapshotSpan> MapToOldSpans(ITextSnapshot current, NormalizedSnapshotSpanCollection spans, ITextSnapshot last) {
			foreach (var item in spans) {
				yield return current.CreateTrackingSpan(item.Span, SpanTrackingMode.EdgeInclusive).GetSpan(last);
			}
		}

		public void Dispose() {
			_TaskBreaker?.Dispose();
			if (_Buffer is ITextBuffer2 b) {
				b.ChangedOnBackground -= TextBuffer_ChangedOnBackground;
			}
		}

		sealed class ParserTask
		{
			// todo: hook WorkspaceRegistration.WorkspaceChanged, Workspace.WorkspaceChanged, Workspace.DocumentActiveContextChanged to refresh highlight
			readonly WorkspaceRegistration _WorkspaceRegistration;
			readonly CSharpTagger _Tagger;
			readonly ConcurrentQueue<SnapshotSpan> _PendingSpans;
			public readonly ITextSnapshot Snapshot;

			public Workspace Workspace;
			public Document Document;
			public SemanticModel Model;

			int _ParsingVersion;

			public ParserTask(ITextSnapshot snapshot, CSharpTagger tagger, NormalizedSnapshotSpanCollection spans) {
				var buffer = snapshot.TextBuffer;
				var wr = _WorkspaceRegistration = Workspace.GetWorkspaceRegistration(buffer.AsTextContainer());
				Workspace w;
				if ((w = wr.Workspace) != null) {
					Workspace = w;
					Document = w.GetDocument(buffer);
					//w.DocumentActiveContextChanged += Workspace_DocumentActiveContextChanged;
					//w.WorkspaceChanged += Workspace_WorkspaceChanged;
				}
				wr.WorkspaceChanged += WorkspaceRegistration_WorkspaceChanged;
				Snapshot = snapshot;
				_Tagger = tagger;
				_PendingSpans = new ConcurrentQueue<SnapshotSpan>(spans);
			}

			public void AddPendingSpans(IEnumerable<SnapshotSpan> spans) {
				foreach (var item in spans) {
					_PendingSpans.Enqueue(item);
				}
			}

			public void StartParse(CancellationToken cancellationToken) {
				if (Document != null && Interlocked.CompareExchange(ref _ParsingVersion, 1, 0) == 0) {
					Task.Run(() => {
						Debug.WriteLine("Start parsing " + Snapshot.Version);
						if (cancellationToken.CanBeCanceled) {
							var p = 0;
							do {
								Thread.Sleep(10);
								++p;
							}
							while (p < 30 && cancellationToken.IsCancellationRequested == false);
							if (cancellationToken.IsCancellationRequested) {
								Debug.WriteLine("Cancel parsing " + Snapshot.Version);
								_ParsingVersion = 0;
								_Tagger.RemoveParser(this);
								return Task.CompletedTask;
							}
						}
						return Document.GetSemanticModelAsync(cancellationToken).ContinueWith(t => {
							if (t.IsCompleted) {
								Debug.WriteLine("End parsing " + Snapshot.Version);
								Model = t.Result;
								var tagger = _Tagger;
								lock (tagger._SyncRoot) {
									ref var last = ref tagger._LastFinishedTask;
									if (last == null || last.Snapshot.Version.VersionNumber < Snapshot.Version.VersionNumber) {
										last = this;
									}
								}
								while (_PendingSpans.TryDequeue(out var span)) {
									tagger.TagsChanged?.Invoke(tagger, new SnapshotSpanEventArgs(span));
								}
							}
							_Tagger.RemoveParser(this);
							_ParsingVersion = 0;
						}, TaskScheduler.Default);
					}, cancellationToken);
				}
			}

			void WorkspaceRegistration_WorkspaceChanged(object sender, EventArgs e) {
				var w = _WorkspaceRegistration.Workspace;
				if (w != Workspace) {
					if (Workspace != null) {
						Workspace.DocumentActiveContextChanged -= Workspace_DocumentActiveContextChanged;
						Workspace.WorkspaceChanged -= Workspace_WorkspaceChanged;
					}
					Workspace = w;
					if (w != null) {
						Document = w.GetDocument(Snapshot.TextBuffer);
						w.DocumentActiveContextChanged += Workspace_DocumentActiveContextChanged;
						w.WorkspaceChanged += Workspace_WorkspaceChanged;
						Debug.WriteLine("Reclassify after WR.workspace change");
						Reclassify(Snapshot);
					}
				}
			}

			void Workspace_WorkspaceChanged(object sender, WorkspaceChangeEventArgs e) {
				if (e.NewSolution.Workspace != Workspace
					|| e.Kind != WorkspaceChangeKind.ProjectChanged
					|| Workspace.GetDocumentIdInCurrentContext(Snapshot.TextBuffer.AsTextContainer()).ProjectId != e.ProjectId
					|| Equals(e.NewSolution.GetProject(e.ProjectId).ParseOptions, e.OldSolution.GetProject(e.ProjectId).ParseOptions)) {
					return;
				}
				var doc = e.NewSolution.GetDocument(e.DocumentId);
				if (doc != null) {
					Document = doc;
					Debug.WriteLine("Reclassify after Workspace change");
					Reclassify(Snapshot);
				}
			}

			void Workspace_DocumentActiveContextChanged(object sender, DocumentActiveContextChangedEventArgs e) {
				Document = e.Solution.GetDocument(e.NewActiveContextDocumentId);
				_Tagger.TagsChanged?.Invoke(_Tagger, new SnapshotSpanEventArgs(new SnapshotSpan(Snapshot, 0, Snapshot.Length)));
			}

			void Reclassify(ITextSnapshot snapshot) {
				_Tagger.TagsChanged?.Invoke(_Tagger, new SnapshotSpanEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));
			}
		}

		static class Parser
		{
			public static IEnumerable<ITagSpan<IClassificationTag>> GetTags(IEnumerable<SnapshotSpan> spans, Workspace workspace, SemanticModel semanticModel, ITextSnapshot snapshot) {
				foreach (var span in spans) {
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
								if (node is MemberDeclarationSyntax || node is AccessorDeclarationSyntax) {
									switch (unitCompilation.FindToken(item.TextSpan.Start).Kind()) {
										case SyntaxKind.SealedKeyword:
										case SyntaxKind.OverrideKeyword:
										case SyntaxKind.AbstractKeyword:
										case SyntaxKind.VirtualKeyword:
										case SyntaxKind.ProtectedKeyword:
										case SyntaxKind.NewKeyword:
											yield return CreateClassificationSpan(snapshot, item.TextSpan, _Classifications.AbstractionKeyword);
											continue;
										case SyntaxKind.ThisKeyword:
											yield return CreateClassificationSpan(snapshot, item.TextSpan, _Classifications.Declaration);
											continue;
										case SyntaxKind.UnsafeKeyword:
										case SyntaxKind.FixedKeyword:
											yield return CreateClassificationSpan(snapshot, item.TextSpan, _Classifications.ResourceKeyword);
											continue;
										case SyntaxKind.ExplicitKeyword:
										case SyntaxKind.ImplicitKeyword:
											yield return CreateClassificationSpan(snapshot, item.TextSpan, _GeneralClassifications.TypeCastKeyword);
											yield return CreateClassificationSpan(snapshot, ((ConversionOperatorDeclarationSyntax)node).Type.Span, _Classifications.NestedDeclaration);
											continue;
										case SyntaxKind.ReadOnlyKeyword:
											yield return CreateClassificationSpan(snapshot, item.TextSpan, _GeneralClassifications.TypeCastKeyword);
											continue;
										case CodeAnalysisHelper.RecordKeyword: // workaround for missing classification type for record identifier
											yield return CreateClassificationSpan(snapshot, (node as TypeDeclarationSyntax).Identifier.Span, _Classifications.ClassName);
											yield return CreateClassificationSpan(snapshot, (node as TypeDeclarationSyntax).Identifier.Span, _Classifications.Declaration);
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
									case CodeAnalysisHelper.SwitchExpression:
									case SyntaxKind.CasePatternSwitchLabel:
									case SyntaxKind.WhenClause:
										yield return CreateClassificationSpan(snapshot, item.TextSpan, _GeneralClassifications.BranchingKeyword);
										continue;
									case SyntaxKind.ForStatement:
									case SyntaxKind.ForEachStatement:
									case SyntaxKind.ForEachVariableStatement:
									case SyntaxKind.WhileStatement:
									case SyntaxKind.DoStatement:
									case SyntaxKind.SelectClause:
									case SyntaxKind.FromClause:
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
									case CodeAnalysisHelper.ImplicitStackAllocArrayCreationExpression:
									case CodeAnalysisHelper.FunctionPointerCallingConvention:
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
									case SyntaxKind.CheckedExpression:
									case SyntaxKind.CheckedStatement:
									case SyntaxKind.UncheckedExpression:
									case SyntaxKind.UncheckedStatement:
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
									case SyntaxKind.IdentifierName:
										if (node.Parent.IsKind(SyntaxKind.TypeConstraint) && item.TextSpan.Length == 9 && node.ToString() == "unmanaged") {
											goto case SyntaxKind.UnsafeStatement;
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
									var p = ClassifyPunctuation(item.TextSpan, snapshot, semanticModel, unitCompilation);
									if (p != null) {
										yield return p;
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
					//case SyntaxKind.AttributeList:
					case SyntaxKind.AttributeArgumentList:
						return CreateClassificationSpan(snapshot, textSpan, _Classifications.AttributeNotation);
				}
				return null;
			}

			static ITagSpan<IClassificationTag> ClassifyPunctuation(TextSpan itemSpan, ITextSnapshot snapshot, SemanticModel semanticModel, CompilationUnitSyntax unitCompilation) {
				if (HighlightOptions.AllBraces == false) {
					return null;
				}
				var s = snapshot.GetText(itemSpan.Start, itemSpan.Length)[0];
				if (s == '{' || s == '}') {
					var node = unitCompilation.FindNode(itemSpan, true, true);
					if (node is BaseTypeDeclarationSyntax == false
						&& node is ExpressionSyntax == false
						&& node is NamespaceDeclarationSyntax == false
						&& node.Kind() != SyntaxKind.SwitchStatement && (node = node.Parent) == null) {
						return null;
					}
					var type = ClassifySyntaxNode(node, node is ExpressionSyntax ? HighlightOptions.MemberBraceTags : HighlightOptions.MemberDeclarationBraceTags, HighlightOptions.KeywordBraceTags);
					if (type != null) {
						return CreateClassificationSpan(snapshot, itemSpan, type);
					}
				}
				else if ((s == '(' || s == ')') && HighlightOptions.AllParentheses) {
					var node = unitCompilation.FindNode(itemSpan, true, true);
					switch (node.Kind()) {
						case SyntaxKind.CastExpression:
							return HighlightOptions.KeywordBraceTags.TypeCast != null
								&& semanticModel.GetSymbolInfo(((CastExpressionSyntax)node).Type).Symbol != null
								? CreateClassificationSpan(snapshot, itemSpan, HighlightOptions.KeywordBraceTags.TypeCast)
								: null;
						case SyntaxKind.ParenthesizedExpression:
							return (HighlightOptions.KeywordBraceTags.TypeCast != null
								&& node.ChildNodes().FirstOrDefault().IsKind(SyntaxKind.AsExpression)
								&& semanticModel.GetSymbolInfo(((BinaryExpressionSyntax)node.ChildNodes().First()).Right).Symbol != null)
								? CreateClassificationSpan(snapshot, itemSpan, HighlightOptions.KeywordBraceTags.TypeCast)
								: null;
						case SyntaxKind.SwitchStatement:
						case SyntaxKind.SwitchSection:
						case SyntaxKind.IfStatement:
						case SyntaxKind.ElseClause:
						case CodeAnalysisHelper.PositionalPatternClause:
							return CreateClassificationSpan(snapshot, itemSpan, HighlightOptions.KeywordBraceTags.Branching);
						case SyntaxKind.ForStatement:
						case SyntaxKind.ForEachStatement:
						case SyntaxKind.ForEachVariableStatement:
						case SyntaxKind.WhileStatement:
						case SyntaxKind.DoStatement:
							return CreateClassificationSpan(snapshot, itemSpan, HighlightOptions.KeywordBraceTags.Loop);
						case SyntaxKind.UsingStatement:
						case SyntaxKind.FixedStatement:
						case SyntaxKind.LockStatement:
						case SyntaxKind.UnsafeStatement:
						case SyntaxKind.TryStatement:
						case SyntaxKind.CatchDeclaration:
						case SyntaxKind.CatchClause:
						case SyntaxKind.CatchFilterClause:
						case SyntaxKind.FinallyClause:
							return CreateClassificationSpan(snapshot, itemSpan, HighlightOptions.KeywordBraceTags.Resource);
						case SyntaxKind.ParenthesizedVariableDesignation:
							return CreateClassificationSpan(snapshot, itemSpan, node.Parent.IsKind(CodeAnalysisHelper.VarPattern) ? HighlightOptions.KeywordBraceTags.Branching : HighlightOptions.MemberBraceTags.Constructor);
						case SyntaxKind.TupleExpression:
						case SyntaxKind.TupleType:
							return CreateClassificationSpan(snapshot, itemSpan, HighlightOptions.MemberBraceTags.Constructor);
						case SyntaxKind.CheckedExpression:
						case SyntaxKind.UncheckedExpression:
							return CreateClassificationSpan(snapshot, itemSpan, HighlightOptions.KeywordBraceTags.TypeCast);
					}
					if (HighlightOptions.MemberBraceTags.Constructor != null) {
						// SpecialHighlightOptions.ParameterBrace or SpecialHighlightOptions.SpecialPunctuation is ON
						node = (node as BaseArgumentListSyntax
						   ?? node as BaseParameterListSyntax
						   ?? (CSharpSyntaxNode)(node as CastExpressionSyntax)
						   )?.Parent;
						if (node != null) {
							var type = ClassifySyntaxNode(node, HighlightOptions.MemberBraceTags, HighlightOptions.KeywordBraceTags);
							if (type != null) {
								return CreateClassificationSpan(snapshot, itemSpan, type);
							}
						}
					}
				}
				else if (s == '[' || s == ']') {
					// highlight attribute annotation
					var node = unitCompilation.FindNode(itemSpan, true, false);
					switch (node.Kind()) {
						case SyntaxKind.AttributeList:
							return CreateClassificationSpan(snapshot, node.Span, _Classifications.AttributeNotation);
						case SyntaxKind.ArrayRankSpecifier:
							return node.Parent.Parent.IsKind(SyntaxKind.ArrayCreationExpression)
								? CreateClassificationSpan(snapshot, itemSpan, HighlightOptions.MemberBraceTags.Constructor)
								: null;
						case SyntaxKind.ImplicitStackAllocArrayCreationExpression:
							return CreateClassificationSpan(snapshot, itemSpan, HighlightOptions.MemberBraceTags.Constructor);
						case SyntaxKind.Argument:
							return ((ArgumentSyntax)node).Expression.IsKind(SyntaxKind.ImplicitStackAllocArrayCreationExpression) ? CreateClassificationSpan(snapshot, itemSpan, HighlightOptions.MemberBraceTags.Constructor) : null;
					}
				}
				return null;
			}

			static ClassificationTag ClassifySyntaxNode(SyntaxNode node, TransientMemberTagHolder tag, TransientKeywordTagHolder keyword) {
				switch (node.Kind()) {
					case SyntaxKind.MethodDeclaration:
					case SyntaxKind.AnonymousMethodExpression:
					case SyntaxKind.SimpleLambdaExpression:
					case SyntaxKind.ParenthesizedLambdaExpression:
					case SyntaxKind.LocalFunctionStatement:
					case SyntaxKind.ConversionOperatorDeclaration:
					case SyntaxKind.OperatorDeclaration:
						return tag.Method;
					case SyntaxKind.InvocationExpression:
						return ((((InvocationExpressionSyntax)node).Expression as IdentifierNameSyntax)?.Identifier.ValueText == "nameof") ? null : tag.Method;
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
					case CodeAnalysisHelper.ImplicitObjectCreationExpression:
					case CodeAnalysisHelper.WithInitializerExpression:
						return tag.Constructor;
					case SyntaxKind.IndexerDeclaration:
					case SyntaxKind.PropertyDeclaration: return tag.Property;
					case CodeAnalysisHelper.RecordDeclaration:
					case SyntaxKind.ClassDeclaration: return tag.Class;
					case SyntaxKind.InterfaceDeclaration: return tag.Interface;
					case SyntaxKind.EnumDeclaration: return tag.Enum;
					case SyntaxKind.StructDeclaration: return tag.Struct;
					case SyntaxKind.Attribute: return _Classifications.AttributeName;
					case SyntaxKind.EventDeclaration: return tag.Event;
					case SyntaxKind.DelegateDeclaration: return tag.Delegate;
					case SyntaxKind.NamespaceDeclaration:
						return tag.Namespace;
					case SyntaxKind.IfStatement:
					case SyntaxKind.ElseClause:
					case SyntaxKind.SwitchStatement:
					case SyntaxKind.SwitchSection:
					case CodeAnalysisHelper.SwitchExpression:
					case CodeAnalysisHelper.RecursivePattern:
						return keyword.Branching;
					case SyntaxKind.ForStatement:
					case SyntaxKind.ForEachStatement:
					case SyntaxKind.ForEachVariableStatement:
					case SyntaxKind.WhileStatement:
					case SyntaxKind.DoStatement:
						return keyword.Loop;
					case SyntaxKind.UsingStatement:
					case SyntaxKind.LockStatement:
					case SyntaxKind.FixedStatement:
					case SyntaxKind.UnsafeStatement:
					case SyntaxKind.TryStatement:
					case SyntaxKind.CatchClause:
					case SyntaxKind.CatchFilterClause:
					case SyntaxKind.FinallyClause:
						return keyword.Resource;
					case SyntaxKind.CheckedStatement:
					case SyntaxKind.UncheckedStatement:
						return keyword.TypeCast;
				}
				return null;
			}

			static IEnumerable<ClassificationTag> GetClassificationType(SyntaxNode node, SemanticModel semanticModel) {
				node = node.Kind() == SyntaxKind.Argument ? ((ArgumentSyntax)node).Expression : node;
				//System.Diagnostics.Debug.WriteLine(node.GetType().Name + node.Span.ToString());
				var symbol = semanticModel.GetSymbolInfo(node).Symbol;
				if (symbol is null) {
					symbol = semanticModel.GetDeclaredSymbol(node);
					if (symbol is null) {
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
						if (symbol is null) {
							yield break;
						}
					}
					else {
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
										if (symbol is null) {
											yield break;
										}
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
										: HighlightOptions.StyleConstructorAsType
										? (methodSymbol.ContainingType.TypeKind == TypeKind.Struct ? _Classifications.StructName : _Classifications.ClassName)
										: _Classifications.ConstructorMethod;
								break;
							case MethodKind.Destructor:
							case MethodKind.StaticConstructor:
								yield return HighlightOptions.StyleConstructorAsType
										? (methodSymbol.ContainingType.TypeKind == TypeKind.Struct ? _Classifications.StructName : _Classifications.ClassName)
										: _Classifications.ConstructorMethod;
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

				if (FormatStore.IdentifySymbolSource && symbol.IsMemberOrType() && symbol.ContainingAssembly != null) {
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
				return tag != null ? new TagSpan<IClassificationTag>(new SnapshotSpan(snapshotSpan, span.Start, span.Length), tag) : null;
			}
		}

		static class HighlightOptions
		{
			static bool _dummy = Init();
			public static TransientKeywordTagHolder KeywordBraceTags { get; private set; }
			public static TransientMemberTagHolder MemberDeclarationBraceTags { get; private set; }
			public static TransientMemberTagHolder MemberBraceTags { get; private set; }

			// use fields to cache option flags
			public static bool AllBraces, AllParentheses, LocalFunctionDeclaration, NonPrivateField, StyleConstructorAsType;

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
				var sp = o.MatchFlags(SpecialHighlightOptions.SpecialPunctuation);
				if (sp) {
					KeywordBraceTags = TransientKeywordTagHolder.BoldBraces.Clone();
					MemberBraceTags = TransientMemberTagHolder.BoldBraces.Clone();
					MemberDeclarationBraceTags = TransientMemberTagHolder.BoldDeclarationBraces.Clone();
				}
				else {
					KeywordBraceTags = TransientKeywordTagHolder.Default.Clone();
					MemberBraceTags = TransientMemberTagHolder.Default.Clone();
					MemberDeclarationBraceTags = TransientMemberTagHolder.DeclarationBraces.Clone();
				}
				if (o.MatchFlags(SpecialHighlightOptions.BranchBrace) == false) {
					KeywordBraceTags.Branching = sp ? ClassificationTagHelper.BoldBraceTag : null;
				}
				if (o.MatchFlags(SpecialHighlightOptions.CastBrace) == false) {
					KeywordBraceTags.TypeCast = sp ? ClassificationTagHelper.BoldBraceTag : null;
				}
				if (o.MatchFlags(SpecialHighlightOptions.LoopBrace) == false) {
					KeywordBraceTags.Loop = sp ? ClassificationTagHelper.BoldBraceTag : null;
				}
				if (o.MatchFlags(SpecialHighlightOptions.ResourceBrace) == false) {
					KeywordBraceTags.Resource = sp ? ClassificationTagHelper.BoldBraceTag : null;
				}
				if (o.MatchFlags(SpecialHighlightOptions.ParameterBrace) == false) {
					MemberBraceTags.Constructor = sp ? ClassificationTagHelper.BoldBraceTag : null;
					MemberBraceTags.Method = sp ? ClassificationTagHelper.BoldBraceTag : null;
				}
				if (o.MatchFlags(SpecialHighlightOptions.DeclarationBrace) == false) {
					MemberDeclarationBraceTags.Class
						= MemberDeclarationBraceTags.Constructor
						= MemberDeclarationBraceTags.Delegate
						= MemberDeclarationBraceTags.Enum
						= MemberDeclarationBraceTags.Event
						= MemberDeclarationBraceTags.Field
						= MemberDeclarationBraceTags.Interface
						= MemberDeclarationBraceTags.Method
						= MemberDeclarationBraceTags.Namespace
						= MemberDeclarationBraceTags.Property
						= MemberDeclarationBraceTags.Struct
						= sp ? ClassificationTagHelper.BoldDeclarationBraceTag : ClassificationTagHelper.DeclarationBraceTag;
				}
				LocalFunctionDeclaration = o.MatchFlags(SpecialHighlightOptions.LocalFunctionDeclaration);
				NonPrivateField = o.MatchFlags(SpecialHighlightOptions.NonPrivateField);
				StyleConstructorAsType = o.MatchFlags(SpecialHighlightOptions.UseTypeStyleOnConstructor);
			}
		}
	}
}