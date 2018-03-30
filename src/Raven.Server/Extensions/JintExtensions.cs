﻿using System.Linq;
using System.Reflection;
using Esprima.Ast;
using Jint.Native.Function;

namespace Raven.Server.Extensions
{
    public static class JintExtensions
    {
        public static string TryGetFieldFromSimpleLambdaExpression(this IFunction function)
        {
            if (!(function.Params.SingleOrDefault() is Identifier identifier))
                return null;
            if (!(function.Body.Body.SingleOrDefault() is ReturnStatement returnStatement))
                return null;
            if (!(returnStatement.Argument is MemberExpression me))
                return null;
            if (!(me.Property is Identifier property))
                return null;
            if ((!(me.Object is Identifier reference) || reference.Name != identifier.Name))
                return null;
            return property.Name;
        }

        private static readonly FieldInfo _funcDeclarationFieldInfo = typeof(ScriptFunctionInstance).GetField("_functionDeclaration", BindingFlags.Instance | BindingFlags.NonPublic);

        public static IFunction GetFunctionAst(this ScriptFunctionInstance scriptFunctionInstance)
        {
            //TODO: expose this in Jint directly instead of reflection            
            var theFuncAst = (IFunction)_funcDeclarationFieldInfo.GetValue(scriptFunctionInstance);
            return theFuncAst;
        }
    }
}
