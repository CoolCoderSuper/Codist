﻿using System;
using System.Collections.Generic;

namespace Codist.Refactorings
{
	static class All
	{
		internal static readonly IRefactoring[] Refactorings = new IRefactoring[] {
			ReplaceToken.InvertOperator,
			ReplaceNode.MergeToConditional,
			ReplaceNode.WrapInElse,
			ReplaceNode.MultiLineExpression,
			ReplaceNode.MultiLineList,
			ReplaceNode.MultiLineMemberAccess,
			ReplaceNode.ConditionalToIf,
			ReplaceNode.IfToConditional,
			ReplaceNode.MergeCondition,
			ReplaceNode.While,
			ReplaceNode.AsToCast,
			ReplaceNode.SwapOperands,
			ReplaceNode.NestCondition,
			ReplaceNode.AddBraces,
			ReplaceNode.WrapInUsing,
			ReplaceNode.WrapInIf,
			ReplaceNode.WrapInTryCatch,
			ReplaceNode.WrapInRegion,
			ReplaceToken.UseStaticDefault,
			ReplaceToken.UseExplicitType,
			ReplaceNode.DeleteCondition,
			ReplaceNode.RemoveContainingStatement
		};
	}
}