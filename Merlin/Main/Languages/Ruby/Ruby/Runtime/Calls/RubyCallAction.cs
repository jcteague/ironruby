﻿/* ****************************************************************************
 *
 * Copyright (c) Microsoft Corporation. 
 *
 * This source code is subject to terms and conditions of the Microsoft Public License. A 
 * copy of the license can be found in the License.html file at the root of this distribution. If 
 * you cannot locate the  Microsoft Public License, please send an email to 
 * ironruby@microsoft.com. By using this source code in any fashion, you are agreeing to be bound 
 * by the terms of the Microsoft Public License.
 *
 * You must not remove this notice, or any other, from this software.
 *
 *
 * ***************************************************************************/

using System;
using System.Linq.Expressions;
using System.Dynamic;

using Microsoft.Scripting.Utils;
using Microsoft.Scripting.Runtime;

using IronRuby.Builtins;
using IronRuby.Compiler;

using Ast = System.Linq.Expressions.Expression;
using AstUtils = Microsoft.Scripting.Ast.Utils;
using IronRuby.Compiler.Generation;
using System.Collections.Generic;
using System.Reflection;

namespace IronRuby.Runtime.Calls {

    public class RubyCallAction : RubyMetaBinder, IExpressionSerializable {
        private readonly RubyCallSignature _signature;
        private readonly string/*!*/ _methodName;

        public override RubyCallSignature Signature {
            get { return _signature; }
        }

        public string/*!*/ MethodName {
            get { return _methodName; }
        }

        public override Type/*!*/ ResultType {
            get { return typeof(object); }
        }

        internal protected RubyCallAction(RubyContext context, string/*!*/ methodName, RubyCallSignature signature) 
            : base(context) {
            Assert.NotNull(methodName);
            _methodName = methodName;
            _signature = signature;
        }

        /// <summary>
        /// Creates a runtime-bound call site binder.
        /// </summary>
        public static RubyCallAction/*!*/ Make(RubyContext/*!*/ context, string/*!*/ methodName, int argumentCount) {
            return Make(context, methodName, RubyCallSignature.Simple(argumentCount));
        }

        /// <summary>
        /// Creates a runtime-bound call site binder.
        /// </summary>
        public static RubyCallAction/*!*/ Make(RubyContext/*!*/ context, string/*!*/ methodName, RubyCallSignature signature) {
            ContractUtils.RequiresNotNull(context, "context");
            ContractUtils.RequiresNotNull(methodName, "methodName");
            return context.MetaBinderFactory.Call(methodName, signature);
        }

        /// <summary>
        /// Creates a call site binder that can be used from multiple runtimes. The site it binds for can be called from multiple runtimes.
        /// </summary>
        [Emitted]
        public static RubyCallAction/*!*/ MakeShared(string/*!*/ methodName, RubyCallSignature signature) {
            // TODO: reduce usage of these sites to minimum
            return RubyMetaBinderFactory.Shared.Call(methodName, signature);
        }

        public override string/*!*/ ToString() {
            return _methodName + _signature.ToString() + (Context != null ? " @" + Context.RuntimeId.ToString() : null);
        }

        #region IExpressionSerializable Members

        Expression/*!*/ IExpressionSerializable.CreateExpression() {
            return Expression.Call(
                Methods.GetMethod(typeof(RubyCallAction), "MakeShared", typeof(string), typeof(RubyCallSignature)),
                AstUtils.Constant(_methodName),
                _signature.CreateExpression()
            );
        }

        #endregion

        protected override bool Build(MetaObjectBuilder/*!*/ metaBuilder, CallArguments/*!*/ args, bool defaultFallback) {
            return BuildCall(metaBuilder, _methodName, args, defaultFallback);
        }

        // Returns true if the call was bound (with success or failure), false if fallback should be performed.
        internal static bool BuildCall(MetaObjectBuilder/*!*/ metaBuilder, string/*!*/ methodName, CallArguments/*!*/ args, bool defaultFallback) {
            RubyMemberInfo methodMissing;
            var method = Resolve(metaBuilder, methodName, args, out methodMissing);

            if (method.Found) {
                method.Info.BuildCall(metaBuilder, args, methodName);
                return true;
            } else {
                return BindToMethodMissing(metaBuilder, args, methodName, methodMissing, method.IncompatibleVisibility, false, defaultFallback);
            }
        }

        internal static MethodResolutionResult Resolve(MetaObjectBuilder/*!*/ metaBuilder, string/*!*/ methodName, CallArguments/*!*/ args,
            out RubyMemberInfo methodMissing) {

            MethodResolutionResult method;
            RubyClass targetClass = args.RubyContext.GetImmediateClassOf(args.Target);
            using (targetClass.Context.ClassHierarchyLocker()) {
                metaBuilder.AddTargetTypeTest(args.Target, targetClass, args.TargetExpression, args.MetaContext);

                // TODO: All sites should have either implicit-self or has-scope flag set?
                var visibilityContext = args.Signature.HasImplicitSelf || !args.Signature.HasScope ? RubyClass.IgnoreVisibility : args.Scope.SelfImmediateClass;
                method = targetClass.ResolveMethodForSiteNoLock(methodName, visibilityContext);
                if (!method.Found) {
                    if (args.Signature.IsTryCall) {
                        // TODO: this shouldn't throw. We need to fix caching of non-existing methods.
                        throw new MissingMethodException();
                        // metaBuilder.Result = AstUtils.Constant(Fields.RubyOps_MethodNotFound);
                    } else {
                        methodMissing = targetClass.ResolveMethodMissingForSite(methodName, method.IncompatibleVisibility);
                    }
                } else {
                    methodMissing = null;
                }
            }

            // Whenever the current self's class changes we need to invalidate the rule, if a protected method is being called.
            if (method.Info != null && method.Info.IsProtected && !args.Signature.HasImplicitSelf) {
                // We don't need to compare versions, just the class objects (super-class relationship cannot be changed).
                // Since we don't want to hold on a class object (to make it collectible) we compare references to the version boxes.
                metaBuilder.AddCondition(Ast.Equal(
                    Methods.GetSelfClassVersionHandle.OpCall(AstUtils.Convert(args.MetaScope.Expression, typeof(RubyScope))),
                    Ast.Constant(args.Scope.SelfImmediateClass.Version)
                ));
            }

            return method;
        }

        internal static bool BindToMethodMissing(MetaObjectBuilder/*!*/ metaBuilder, CallArguments/*!*/ args, string/*!*/ methodName,
            RubyMemberInfo methodMissing, RubyMethodVisibility incompatibleVisibility, bool isSuperCall, bool defaultFallback) {
            // Assumption: args already contain method name.
            
            // TODO: better check for builtin method
            if (methodMissing == null ||
                methodMissing.DeclaringModule == methodMissing.Context.KernelModule && methodMissing is RubyLibraryMethodInfo) {

                if (isSuperCall) {
                    metaBuilder.SetError(Methods.MakeMissingSuperException.OpCall(AstUtils.Constant(methodName)));
                } else if (incompatibleVisibility == RubyMethodVisibility.Private) {
                    metaBuilder.SetError(Methods.MakePrivateMethodCalledError.OpCall(
                        AstUtils.Convert(args.MetaContext.Expression, typeof(RubyContext)), args.TargetExpression, AstUtils.Constant(methodName))
                    );
                } else if (incompatibleVisibility == RubyMethodVisibility.Protected) {
                    metaBuilder.SetError(Methods.MakeProtectedMethodCalledError.OpCall(
                        AstUtils.Convert(args.MetaContext.Expression, typeof(RubyContext)), args.TargetExpression, AstUtils.Constant(methodName))
                    );
                } else if (defaultFallback) {
                    args.InsertMethodName(methodName);
                    methodMissing.BuildCall(metaBuilder, args, methodName);
                } else {
                    return false;
                }
            } else {
                args.InsertMethodName(methodName);
                methodMissing.BuildCall(metaBuilder, args, methodName);
            }

            return true;
        }

        protected override DynamicMetaObjectBinder GetInteropBinder(RubyContext/*!*/ context, IList<DynamicMetaObject/*!*/>/*!*/ args,
            out MethodInfo postConverter) {

            switch (_methodName) {
                case "new":
                    postConverter = null;
                    return new InteropBinder.CreateInstance(context, new CallInfo(args.Count));

                case "call":
                    postConverter = null; 
                    return new InteropBinder.Invoke(context, "call", new CallInfo(args.Count));

                case "to_s":
                    postConverter = Methods.ObjectToMutableString;
                    return new InteropBinder.InvokeMember(context, "ToString", new CallInfo(args.Count));

                case "to_str":
                    postConverter = Methods.StringToMutableString;
                    return new InteropBinder.Convert(context, typeof(string), false);

                case "[]":
                    // TODO: or invoke?
                    postConverter = null;
                    return new InteropBinder.GetIndex(context, new CallInfo(args.Count));

                case "[]=":
                    postConverter = null;
                    return new InteropBinder.SetIndex(context, new CallInfo(args.Count));

                // BinaryOps:
                case "+": // ExpressionType.Add
                case "-": // ExpressionType.Subtract
                case "/": // ExpressionType.Divide
                case "*": // ExpressionType.Multiply
                case "%": // ExpressionType.Modulo
                case "==": // ExpressionType.Equal
                case "!=": // ExpressionType.NotEqual
                case ">": // ExpressionType.GreaterThan
                case ">=": // ExpressionType.GreaterThanOrEqual
                case "<":  // ExpressionType.LessThan
                case "<=": // ExpressionType.LessThanOrEqual

                case "**": // ExpressionType.Power
                case "<<": // ExpressionType.LeftShift
                case ">>": // ExpressionType.RightShift
                case "&": // ExpressionType.And
                case "|": // ExpressionType.Or
                case "^": // ExpressionType.ExclusiveOr;

                // UnaryOp:
                case "-@":
                case "+@":
                case "~":
                    postConverter = null;
                    return null;

                default:
                    postConverter = null;
                    if (_methodName.EndsWith("=")) {
                        return new InteropBinder.SetMember(context, _methodName.Substring(0, _methodName.Length - 1));
                    } else {
                        return new InteropBinder.InvokeMember(context, _methodName, new CallInfo(args.Count));
                    }
            }
        }
    }
}
