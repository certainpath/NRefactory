// 
// CSharpCompletionEngine.cs
//  
// Author:
//       Mike Krüger <mkrueger@xamarin.com>
// 
// Copyright (c) 2011 Xamarin Inc. (http://xamarin.com)
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using System.Threading;
using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text;
using Microsoft.CodeAnalysis.Recommendations;
using System.Threading.Tasks;

namespace ICSharpCode.NRefactory6.CSharp.Completion
{
	public partial class CompletionEngine 
	{
		static CompletionContextHandler[] handlers = {
			new RoslynRecommendationsCompletionContextHandler (),
			new KeywordContextHandler(),
			new OverrideContextHandler(),
			new PartialContextHandler(),
			new EnumMemberContextHandler(),
			new XmlDocCommentContextHandler(),
			new ExplicitInterfaceContextHandler(),
			new AttributeNamedParameterContextHandler(),
			new NamedParameterContextHandler(),
			new SpeculativeTContextHandler(),
			new SnippetContextHandler(),
			new ObjectInitializerContextHandler(),
			new FormatItemContextHandler(),
			new SpeculativeNameContextHandler(),
			new DelegateCreationContextHandler(),
			new ObjectCreationContextHandler(),
			new SenderCompletionContextHandler(),
			new CastCompletionContextHandler(),
			new PreProcessorExpressionContextHandler()
		};

		static readonly ICompletionKeyHandler DefaultKeyHandler = new RoslynRecommendationsCompletionContextHandler ();

		readonly ICompletionDataFactory factory;
		readonly Workspace workspace;

		public ICompletionDataFactory Factory {
			get {
				return factory;
			}
		}

		public Workspace Workspace {
			get {
				return workspace;
			}
		}

		public CompletionEngine(Workspace workspace, ICompletionDataFactory factory)
		{
			if (workspace == null)
				throw new ArgumentNullException("workspace");
			if (factory == null)
				throw new ArgumentNullException("factory");
			this.workspace = workspace;
			this.factory = factory;
		}
		
		public void AddImportCompletionData (CompletionResult result, Document document, SemanticModel semanticModel, int position, CancellationToken cancellationToken = default(CancellationToken))
		{
			var ns = new Stack<INamespaceOrTypeSymbol>();
			ns.Push(semanticModel.Compilation.GlobalNamespace);
			
			semanticModel.LookupNamespacesAndTypes(position);
		}

		public async Task<CompletionResult> GetCompletionDataAsync(CompletionContext completionContext, CompletionTriggerInfo info, CancellationToken cancellationToken = default(CancellationToken))
		{
			if (completionContext == null)
				throw new ArgumentNullException ("completionContext");

			var document = completionContext.Document;
			var semanticModel = await completionContext.GetSemanticModelAsync (cancellationToken).ConfigureAwait(false);
			var position = completionContext.Position;

			var text = await document.GetTextAsync (cancellationToken).ConfigureAwait (false);
			var ctx = await completionContext.GetSyntaxContextAsync (workspace, cancellationToken).ConfigureAwait (false);

			// case lambda parameter (n1, $
			if (ctx.TargetToken.IsKind (SyntaxKind.CommaToken) &&
				ctx.TargetToken.Parent != null && ctx.TargetToken.Parent.Parent != null &&
				ctx.TargetToken.Parent.Parent.IsKind(SyntaxKind.ParenthesizedLambdaExpression)) 
				return CompletionResult.Empty;

			var result = new CompletionResult();

			if (position > 0) {
				var nonExclusiveHandlers = new List<CompletionContextHandler> ();
				var exclusiveHandlers = new List<CompletionContextHandler> ();
				IEnumerable<CompletionContextHandler> handlerList;
				if (completionContext.UseDefaultContextHandlers) {
					handlerList = handlers.Concat (completionContext.AdditionalContextHandlers);
				} else {
					handlerList = completionContext.AdditionalContextHandlers;
				}
				foreach (var handler in handlerList) {
					if (info.CompletionTriggerReason == CompletionTriggerReason.CompletionCommand || handler.IsTriggerCharacter (text, position - 1)) {
						if (await handler.IsExclusiveAsync (document, position, info, cancellationToken)) {
							exclusiveHandlers.Add (handler);
						} else {
							nonExclusiveHandlers.Add (handler);
						}
					}
				}

				foreach (var handler in exclusiveHandlers) {
					var handlerResult = handler.GetCompletionDataAsync (result, this, completionContext, info, cancellationToken).Result;
					if (handlerResult != null)
						result.AddRange (handlerResult);
				}

				if (result.Count == 0) {
					foreach (var handler in nonExclusiveHandlers) {
						var handlerResult = handler.GetCompletionDataAsync (result, this, completionContext, info, cancellationToken).Result;
						if (handlerResult != null)
							result.AddRange (handlerResult);
					}
				}
			}

			// prevent auto selection for "<number>." case
			if (ctx.TargetToken.IsKind(SyntaxKind.DotToken)) {
				var accessExpr = ctx.TargetToken.Parent as MemberAccessExpressionSyntax;
				if (accessExpr != null &&
					accessExpr.Expression != null &&
					accessExpr.Expression.IsKind(SyntaxKind.NumericLiteralExpression))  {
					result.AutoSelect = false;
				}
			}

			if (ctx.LeftToken.Parent != null &&
				ctx.LeftToken.Parent.Parent != null &&
				ctx.TargetToken.Parent != null && !ctx.TargetToken.Parent.IsKind(SyntaxKind.NameEquals) &&
				ctx.LeftToken.Parent.Parent.IsKind(SyntaxKind.AnonymousObjectMemberDeclarator))
				result.AutoSelect = false;

			if (ctx.TargetToken.IsKind (SyntaxKind.OpenParenToken) && ctx.TargetToken.GetPreviousToken ().IsKind (SyntaxKind.OpenParenToken)) {
				var validTypes = TypeGuessing.GetValidTypes (semanticModel, ctx.TargetToken.Parent, cancellationToken);
				result.AutoSelect = !validTypes.Any (t => t.IsDelegateType ());
			}

			foreach (var type in ctx.InferredTypes) {
				if (type.TypeKind == TypeKind.Delegate) {
					result.AutoSelect = false;
					break;
				}
			}
			
			return result;
		}

		IEnumerable<ISymbol> GetAllMembers (ITypeSymbol type)
		{
			if (type == null)
				yield break;
			foreach (var member in type.GetMembers()) {
				yield return member;
			}
			foreach (var baseMember in GetAllMembers(type.BaseType))
				yield return baseMember;
		}

		public static Func<CancellationToken, Task<IEnumerable<ICompletionData>>> SnippetCallback;
	}
}