/* ****************************************************************************
 *
 * Copyright (c) Microsoft Corporation. 
 *
 * This source code is subject to terms and conditions of the Microsoft Public License. A 
 * copy of the license can be found in the License.html file at the root of this distribution. If 
 * you cannot locate the  Microsoft Public License, please send an email to 
 * dlr@microsoft.com. By using this source code in any fashion, you are agreeing to be bound 
 * by the terms of the Microsoft Public License.
 *
 * You must not remove this notice, or any other, from this software.
 *
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Dynamic;
using System.Text;
using Microsoft.Contracts;
using Microsoft.Scripting.Actions;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Generation;
using System.Collections;
using Microsoft.Scripting.Utils;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Linq.Expressions;
using AstUtils = Microsoft.Scripting.Ast.Utils;

namespace Microsoft.Scripting.Actions.Calls {
    using Ast = System.Linq.Expressions.Expression;

    /// <summary>
    /// MethodCandidate represents the different possible ways of calling a method or a set of method overloads.
    /// A single method can result in multiple MethodCandidates. Some reasons include:
    /// - Every optional parameter or parameter with a default value will result in a candidate
    /// - The presence of ref and out parameters will add a candidate for languages which want to return the updated values as return values.
    /// - ArgumentKind.List and ArgumentKind.Dictionary can result in a new candidate per invocation since the list might be different every time.
    ///
    /// Each MethodCandidate represents the parameter type for the candidate using ParameterWrapper.
    /// </summary>
    public sealed class MethodCandidate {
        private readonly OverloadResolver _resolver;
        private readonly MethodBase _method;

        private readonly List<ParameterWrapper> _parameters;
        private readonly ParameterWrapper _paramsDict;
        private readonly int _paramsArrayIndex;

        private readonly IList<ArgBuilder> _argBuilders;
        private readonly ArgBuilder _instanceBuilder;
        private readonly ReturnBuilder _returnBuilder;

        internal MethodCandidate(OverloadResolver resolver, MethodBase method, List<ParameterWrapper> parameters, ParameterWrapper paramsDict,
            ReturnBuilder returnBuilder, ArgBuilder instanceBuilder, IList<ArgBuilder> argBuilders) {

            Assert.NotNull(resolver, method, instanceBuilder, returnBuilder);
            Assert.NotNullItems(parameters);
            Assert.NotNullItems(argBuilders);

            _resolver = resolver;
            _method = method;
            _instanceBuilder = instanceBuilder;
            _argBuilders = argBuilders;
            _returnBuilder = returnBuilder;
            _parameters = parameters;
            _paramsDict = paramsDict;

            _paramsArrayIndex = ParameterWrapper.IndexOfParamsArray(parameters);

            parameters.TrimExcess();
        }

        internal ReturnBuilder ReturnBuilder {
            get { return _returnBuilder; }
        }

        internal IList<ArgBuilder> ArgBuilders {
            get { return _argBuilders; }
        }

        public OverloadResolver Resolver {
            get { return _resolver; }
        }

        public MethodBase Method {
            get { return _method; }
        }

        public Type ReturnType {
            get { return _returnBuilder.ReturnType; }
        }

        public int ParamsArrayIndex {
            get { return _paramsArrayIndex; }
        }

        public bool HasParamsArray {
            get { return _paramsArrayIndex != -1; }
        }

        public bool HasParamsDictionary {
            get { return _paramsDict != null; }
        }

        public ActionBinder Binder {
            get { return _resolver.Binder; }
        }

        internal ParameterWrapper GetParameter(int argumentIndex, ArgumentBinding namesBinding) {
            return _parameters[namesBinding.ArgumentToParameter(argumentIndex)];
        }

        internal ParameterWrapper GetParameter(int parameterIndex) {
            return _parameters[parameterIndex];
        }

        internal int ParameterCount {
            get { return _parameters.Count; }
        }

        internal int IndexOfParameter(string name) {
            for (int i = 0; i < _parameters.Count; i++) {
                if (_parameters[i].Name == name) {
                    return i;
                }
            }
            return -1;
        }

        public int GetVisibleParameterCount() {
            int result = 0;
            foreach (var parameter in _parameters) {
                if (!parameter.IsHidden) {
                    result++;
                }
            }
            return result;
        }

        /// <summary>
        /// Builds a new MethodCandidate which takes count arguments and the provided list of keyword arguments.
        /// 
        /// The basic idea here is to figure out which parameters map to params or a dictionary params and
        /// fill in those spots w/ extra ParameterWrapper's.  
        /// </summary>
        internal MethodCandidate MakeParamsExtended(int count, IList<string> names) {
            Debug.Assert(BinderHelpers.IsParamsMethod(_method));

            List<ParameterWrapper> newParameters = new List<ParameterWrapper>(count);
            
            // keep track of which named args map to a real argument, and which ones
            // map to the params dictionary.
            List<string> unusedNames = new List<string>(names);
            List<int> unusedNameIndexes = new List<int>();
            for (int i = 0; i < unusedNames.Count; i++) {
                unusedNameIndexes.Add(i);
            }

            // if we don't have a param array we'll have a param dict which is type object
            ParameterWrapper paramsArrayParameter = null;
            int paramsArrayIndex = -1;

            for (int i = 0; i < _parameters.Count; i++) {
                ParameterWrapper parameter = _parameters[i];

                if (parameter.IsParamsArray) {
                    paramsArrayParameter = parameter;
                    paramsArrayIndex = i;
                } else {
                    int j = unusedNames.IndexOf(parameter.Name);
                    if (j != -1) {
                        unusedNames.RemoveAt(j);
                        unusedNameIndexes.RemoveAt(j);
                    }
                    newParameters.Add(parameter);
                }
            }

            if (paramsArrayIndex != -1) {
                ParameterWrapper expanded = paramsArrayParameter.Expand();
                while (newParameters.Count < (count - unusedNames.Count)) {
                    newParameters.Insert(System.Math.Min(paramsArrayIndex, newParameters.Count), expanded);
                }
            }

            if (_paramsDict != null) {
                bool nonNullItems = CompilerHelpers.ProhibitsNullItems(_paramsDict.ParameterInfo);

                foreach (string name in unusedNames) {
                    newParameters.Add(new ParameterWrapper(_paramsDict.ParameterInfo, typeof(object), name, nonNullItems, false, false, _paramsDict.IsHidden));
                }
            } else if (unusedNames.Count != 0) {
                // unbound kw args and no where to put them, can't call...
                // TODO: We could do better here because this results in an incorrect arg # error message.
                return null;
            }

            // if we have too many or too few args we also can't call
            if (count != newParameters.Count) {
                return null;
            }

            return MakeParamsExtended(unusedNames.ToArray(), unusedNameIndexes.ToArray(), newParameters);
        }

        private MethodCandidate MakeParamsExtended(string[] names, int[] nameIndices, List<ParameterWrapper> parameters) {
            Debug.Assert(BinderHelpers.IsParamsMethod(Method));

            List<ArgBuilder> newArgBuilders = new List<ArgBuilder>(_argBuilders.Count);

            // current argument that we consume, initially skip this if we have it.
            int curArg = CompilerHelpers.IsStatic(_method) ? 0 : 1;
            int kwIndex = -1;
            ArgBuilder paramsDictBuilder = null;

            foreach (ArgBuilder ab in _argBuilders) {
                // TODO: define a virtual method on ArgBuilder implementing this functionality:

                SimpleArgBuilder sab = ab as SimpleArgBuilder;
                if (sab != null) {
                    // we consume one or more incoming argument(s)
                    if (sab.IsParamsArray) {
                        // consume all the extra arguments
                        int paramsUsed = parameters.Count -
                            GetConsumedArguments() -
                            names.Length +
                            (CompilerHelpers.IsStatic(_method) ? 1 : 0);

                        newArgBuilders.Add(new ParamsArgBuilder(
                            sab.ParameterInfo,
                            sab.Type.GetElementType(),
                            curArg,
                            paramsUsed
                        ));

                        curArg += paramsUsed;
                    } else if (sab.IsParamsDict) {
                        // consume all the kw arguments
                        kwIndex = newArgBuilders.Count;
                        paramsDictBuilder = sab;
                    } else {
                        // consume the argument, adjust its position:
                        newArgBuilders.Add(sab.MakeCopy(curArg++));
                    }
                } else if (ab is KeywordArgBuilder) {
                    newArgBuilders.Add(ab);
                    curArg++;
                } else {
                    // CodeContext, null, default, etc...  we don't consume an 
                    // actual incoming argument.
                    newArgBuilders.Add(ab);
                }
            }

            if (kwIndex != -1) {
                newArgBuilders.Insert(kwIndex, new ParamsDictArgBuilder(paramsDictBuilder.ParameterInfo, curArg, names, nameIndices));
            }

            return new MethodCandidate(_resolver, _method, parameters, null, _returnBuilder, _instanceBuilder, newArgBuilders);
        }

        private int GetConsumedArguments() {
            int consuming = 0;
            foreach (ArgBuilder argb in _argBuilders) {
                SimpleArgBuilder sab = argb as SimpleArgBuilder;
                if (sab != null && !sab.IsParamsDict || argb is KeywordArgBuilder) {
                    consuming++;
                }
            }
            return consuming;
        }

        public Type[] GetParameterTypes() {
            List<Type> res = new List<Type>(_argBuilders.Count);
            for (int i = 0; i < _argBuilders.Count; i++) {
                Type t = _argBuilders[i].Type;
                if (t != null) {
                    res.Add(t);
                }
            }

            return res.ToArray();
        }

        #region MakeDelegate

        internal OptimizingCallDelegate MakeDelegate(RestrictionInfo restrictionInfo) {
            MethodInfo mi = Method as MethodInfo;
            if (mi == null) {
                return null;
            }

            Type declType = mi.GetBaseDefinition().DeclaringType;
            if (declType != null &&
                declType.Assembly == typeof(string).Assembly &&
                declType.IsSubclassOf(typeof(MemberInfo))) {
                // members of reflection are off limits via reflection in partial trust
                return null;
            }

            if (_returnBuilder.CountOutParams > 0) {
                return null;
            }

            // if we have a non-visible method see if we can find a better method which
            // will call the same thing but is visible.  If this fails we still bind anyway - it's
            // the callers responsibility to filter out non-visible methods.
            mi = CompilerHelpers.TryGetCallableMethod(mi);

            Func<object[], object>[] builders = new Func<object[], object>[_argBuilders.Count];
            bool[] hasBeenUsed = new bool[restrictionInfo.Objects.Length];

            for (int i = 0; i < _argBuilders.Count; i++) {
                var builder = _argBuilders[i].ToDelegate(_resolver, restrictionInfo.Objects, hasBeenUsed);
                if (builder == null) {
                    return null;
                }

                builders[i] = builder;
            }

            if (_instanceBuilder != null && !(_instanceBuilder is NullArgBuilder)) {
                return new Caller(mi, builders, _instanceBuilder.ToDelegate(_resolver, restrictionInfo.Objects, hasBeenUsed)).CallWithInstance;
            } else {
                return new Caller(mi, builders, null).Call;
            }
        }

        private sealed class Caller {
            private readonly Func<object[], object>[] _argBuilders;
            private readonly Func<object[], object> _instanceBuilder;
            private readonly MethodInfo _mi;
            private ReflectedCaller _caller;
            private int _hitCount;

            public Caller(MethodInfo mi, Func<object[], object>[] argBuilders, Func<object[], object> instanceBuilder) {
                _mi = mi;
                _argBuilders = argBuilders;
                _instanceBuilder = instanceBuilder;
            }

            public object Call(object[] args, out bool shouldOptimize) {
                shouldOptimize = TrackUsage(args);

                try {
                    if (_caller != null) {
                        return _caller.Invoke(GetArguments(args));
                    }
                    return _mi.Invoke(null, GetArguments(args));
                } catch (TargetInvocationException tie) {
                    ExceptionHelpers.UpdateForRethrow(tie.InnerException);
                    throw tie.InnerException;
                }
            }

            public object CallWithInstance(object[] args, out bool shouldOptimize) {
                shouldOptimize = TrackUsage(args);

                try {
                    if (_caller != null) {
                        return _caller.InvokeInstance(_instanceBuilder(args), GetArguments(args));
                    }

                    return _mi.Invoke(_instanceBuilder(args), GetArguments(args));
                } catch (TargetInvocationException tie) {
                    ExceptionHelpers.UpdateForRethrow(tie.InnerException);
                    throw tie.InnerException;
                }
            }

            private object[] GetArguments(object[] args) {
                object[] finalArgs = new object[_argBuilders.Length];
                for (int i = 0; i < finalArgs.Length; i++) {
                    finalArgs[i] = _argBuilders[i](args);
                }
                return finalArgs;
            }

            private bool TrackUsage(object[] args) {
                bool shouldOptimize;
                _hitCount++;
                shouldOptimize = false;

                bool forceCaller = false;
                if (_hitCount <= 100 && _caller == null) {
                    foreach (object o in args) {
                        // can't pass Missing.Value via reflection, use a ReflectedCaller
                        if (o == Missing.Value) {
                            forceCaller = true;
                        }
                    }
                }

                if (_hitCount > 100) {
                    shouldOptimize = true;
                } else if ((_hitCount > 5 || forceCaller) && _caller == null) {
                    _caller = ReflectedCaller.Create(_mi);
                }
                return shouldOptimize;
            }
        }

        #endregion

        #region MakeExpression

        internal Expression MakeExpression(IList<Expression> args) {
            bool[] usageMarkers;
            Expression[] spilledArgs;
            Expression[] callArgs = GetArgumentExpressions(args, out usageMarkers, out spilledArgs);

            MethodBase mb = Method;
            MethodInfo mi = mb as MethodInfo;
            Expression ret, call;
            if (mi != null) {
                // if we have a non-visible method see if we can find a better method which
                // will call the same thing but is visible.  If this fails we still bind anyway - it's
                // the callers responsibility to filter out non-visible methods.
                mb = CompilerHelpers.TryGetCallableMethod(mi);
            }

            ConstructorInfo ci = mb as ConstructorInfo;
            Debug.Assert(mi != null || ci != null);
            if (CompilerHelpers.IsVisible(mb)) {
                // public method
                if (mi != null) {
                    Expression instance = mi.IsStatic ? null : _instanceBuilder.ToExpression(_resolver, args, usageMarkers);
                    call = AstUtils.SimpleCallHelper(instance, mi, callArgs);
                } else {
                    call = AstUtils.SimpleNewHelper(ci, callArgs);
                }
            } else {
                // Private binding, invoke via reflection
                if (mi != null) {
                    Expression instance = mi.IsStatic ? AstUtils.Constant(null) : _instanceBuilder.ToExpression(_resolver, args, usageMarkers);
                    Debug.Assert(instance != null, "Can't skip instance expression");

                    call = Ast.Call(
                        typeof(BinderOps).GetMethod("InvokeMethod"),
                        AstUtils.Constant(mi),
                        AstUtils.Convert(instance, typeof(object)),
                        AstUtils.NewArrayHelper(typeof(object), callArgs)
                    );
                } else {
                    call = Ast.Call(
                        typeof(BinderOps).GetMethod("InvokeConstructor"),
                        AstUtils.Constant(ci),
                        AstUtils.NewArrayHelper(typeof(object), callArgs)
                    );
                }
            }

            if (spilledArgs != null) {
                call = Expression.Block(spilledArgs.AddLast(call));
            }

            ret = _returnBuilder.ToExpression(_resolver, _argBuilders, args, call);

            List<Expression> updates = null;
            for (int i = 0; i < _argBuilders.Count; i++) {
                Expression next = _argBuilders[i].UpdateFromReturn(_resolver, args);
                if (next != null) {
                    if (updates == null) {
                        updates = new List<Expression>();
                    }
                    updates.Add(next);
                }
            }

            if (updates != null) {
                if (ret.Type != typeof(void)) {
                    ParameterExpression temp = Ast.Variable(ret.Type, "$ret");
                    updates.Insert(0, Ast.Assign(temp, ret));
                    updates.Add(temp);
                    ret = Ast.Block(new[] { temp }, updates.ToArray());
                } else {
                    updates.Insert(0, ret);
                    ret = Ast.Block(typeof(void), updates.ToArray());
                }
            }

            if (_resolver.Temps != null) {
                ret = Ast.Block(_resolver.Temps, ret);
            }

            return ret;
        }

        private Expression[] GetArgumentExpressions(IList<Expression> parameters, out bool[] usageMarkers, out Expression[] spilledArgs) {
            int minPriority = Int32.MaxValue;
            int maxPriority = Int32.MinValue;
            foreach (ArgBuilder ab in _argBuilders) {
                minPriority = System.Math.Min(minPriority, ab.Priority);
                maxPriority = System.Math.Max(maxPriority, ab.Priority);
            }

            var args = new Expression[_argBuilders.Count];
            Expression[] actualArgs = null;
            usageMarkers = new bool[parameters.Count];
            for (int priority = minPriority; priority <= maxPriority; priority++) {
                for (int i = 0; i < _argBuilders.Count; i++) {
                    if (_argBuilders[i].Priority == priority) {
                        args[i] = _argBuilders[i].ToExpression(_resolver, parameters, usageMarkers);

                        // see if this has a temp that needs to be passed as the actual argument
                        Expression byref = _argBuilders[i].ByRefArgument;
                        if (byref != null) {
                            if (actualArgs == null) {
                                actualArgs = new Expression[_argBuilders.Count];
                            }
                            actualArgs[i] = byref;
                        }
                    }
                }
            }

            if (actualArgs != null) {
                for (int i = 0; i < args.Length; i++) {
                    if (args[i] != null && actualArgs[i] == null) {
                        actualArgs[i] = _resolver.GetTemporary(args[i].Type, null);
                        args[i] = Expression.Assign(actualArgs[i], args[i]);
                    }
                }

                spilledArgs = RemoveNulls(args);
                return RemoveNulls(actualArgs);
            }

            spilledArgs = null;
            return RemoveNulls(args);
        }

        private static Expression[] RemoveNulls(Expression[] args) {
            int newLength = args.Length;
            for (int i = 0; i < args.Length; i++) {
                if (args[i] == null) {
                    newLength--;
                }
            }

            var result = new Expression[newLength];
            for (int i = 0, j = 0; i < args.Length; i++) {
                if (args[i] != null) {
                    result[j++] = args[i];
                }
            }
            return result;
        }

        #endregion

        [Confined]
        public override string ToString() {
            return string.Format("MethodCandidate({0} on {1})", Method, Method.DeclaringType.FullName);
        }
    }
}
