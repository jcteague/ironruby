﻿/* ****************************************************************************
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

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Dynamic.Utils;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace System.Linq.Expressions {
    /// <summary>
    /// Represents indexing a property or array.
    /// </summary>
#if !SILVERLIGHT
    [DebuggerTypeProxy(typeof(Expression.IndexExpressionProxy))]
#endif
    public sealed class IndexExpression : Expression, IArgumentProvider {
        private readonly Expression _instance;
        private readonly PropertyInfo _indexer;
        private IList<Expression> _arguments;

        internal IndexExpression(
            Expression instance,
            PropertyInfo indexer,
            IList<Expression> arguments) {

            if (indexer == null) {
                Debug.Assert(instance != null && instance.Type.IsArray);
                Debug.Assert(instance.Type.GetArrayRank() == arguments.Count);
            }

            _instance = instance;
            _indexer = indexer;
            _arguments = arguments;
        }

        /// <summary>
        /// Returns the node type of this <see cref="Expression" />. (Inherited from <see cref="Expression" />.)
        /// </summary>
        /// <returns>The <see cref="ExpressionType"/> that represents this expression.</returns>
        protected override ExpressionType NodeTypeImpl() {
            return ExpressionType.Index;
        }

        /// <summary>
        /// Gets the static type of the expression that this <see cref="Expression" /> represents. (Inherited from <see cref="Expression"/>.)
        /// </summary>
        /// <returns>The <see cref="Type"/> that represents the static type of the expression.</returns>
        protected override Type TypeImpl() {
            if (_indexer != null) {
                return _indexer.PropertyType;
            }
            return _instance.Type.GetElementType();
        }

        /// <summary>
        /// An object to index.
        /// </summary>
        public Expression Object {
            get { return _instance; }
        }

        /// <summary>
        /// Gets the <see cref="PropertyInfo"/> for the property if the expression represents an indexed property, returns null otherwise.
        /// </summary>
        public PropertyInfo Indexer {
            get { return _indexer; }
        }

        /// <summary>
        /// Gets the arguments to be used to index the property or array.
        /// </summary>
        public ReadOnlyCollection<Expression> Arguments {
            get { return ReturnReadOnly(ref _arguments); }
        }

        Expression IArgumentProvider.GetArgument(int index) {
            return _arguments[index];
        }

        int IArgumentProvider.ArgumentCount {
            get {
                return _arguments.Count;
            }
        }

        internal override Expression Accept(ExpressionVisitor visitor) {
            return visitor.VisitIndex(this);
        }

        internal Expression Rewrite(Expression instance, Expression[] arguments) {
            Debug.Assert(instance != null);
            Debug.Assert(arguments == null || arguments.Length == _arguments.Count);

            return Expression.MakeIndex(instance, _indexer, arguments ?? _arguments);
        }
    }

    public partial class Expression {

        /// <summary>
        /// Creates an <see cref="IndexExpression"/> that represents accessing an indexed property in an object.
        /// </summary>
        /// <param name="instance">The object to which the property belongs. Should be null if the property is static(shared).</param>
        /// <param name="indexer">An <see cref="Expression"/> representing the property to index.</param>
        /// <param name="arguments">An IEnumerable{Expression} contaning the arguments to be used to index the property.</param>
        /// <returns>The created <see cref="IndexExpression"/>.</returns>
        public static IndexExpression MakeIndex(Expression instance, PropertyInfo indexer, IEnumerable<Expression> arguments) {
            if (indexer != null) {
                return Property(instance, indexer, arguments);
            } else {
                return ArrayAccess(instance, arguments);
            }
        }

        #region ArrayAccess

        /// <summary>
        /// Creates an <see cref="IndexExpression"></see> to access an array.
        /// </summary>
        /// <param name="array">An expression representing the array to index.</param>
        /// <param name="indexes">An array containing expressions used to index the array.</param>
        /// <remarks>The expression representing the array can be obtained by using the MakeMemberAccess method, 
        /// or through NewArrayBounds or NewArrayInit.</remarks>
        /// <returns>The created <see cref="IndexExpression"/>.</returns>
        public static IndexExpression ArrayAccess(Expression array, params Expression[] indexes) {
            return ArrayAccess(array, (IEnumerable<Expression>)indexes);
        }

        /// <summary>
        /// Creates an <see cref="IndexExpression"></see> to access an array.
        /// </summary>
        /// <param name="array">An expression representing the array to index.</param>
        /// <param name="indexes">An <see cref="IEnumerable{Expression}"/> containing expressions used to index the array.</param>
        /// <remarks>The expression representing the array can be obtained by using the MakeMemberAccess method, 
        /// or through NewArrayBounds or NewArrayInit.</remarks>
        /// <returns>The created <see cref="IndexExpression"/>.</returns>
        public static IndexExpression ArrayAccess(Expression array, IEnumerable<Expression> indexes) {
            RequiresCanRead(array, "array");

            Type arrayType = array.Type;
            if (!arrayType.IsArray) {
                throw Error.ArgumentMustBeArray();
            }

            var indexList = indexes.ToReadOnly();
            if (arrayType.GetArrayRank() != indexList.Count) {
                throw Error.IncorrectNumberOfIndexes();
            }

            foreach (Expression e in indexList) {
                RequiresCanRead(e, "indexes");
                if (e.Type != typeof(int)) {
                    throw Error.ArgumentMustBeArrayIndexType();
                }
            }

            return new IndexExpression(array, null, indexList);
        }

        #endregion

        #region Property
        /// <summary>
        /// Creates an <see cref="IndexExpression"/> representing the access to an indexed property.
        /// </summary>
        /// <param name="instance">The object to which the property belongs. If the property is static/shared, it must be null.</param>
        /// <param name="propertyName">The name of the indexer.</param>
        /// <param name="arguments">An array of <see cref="Expression"/> objects that are used to index the property.</param>
        /// <returns>The created <see cref="IndexExpression"/>.</returns>
        public static IndexExpression Property(Expression instance, string propertyName, params Expression[] arguments) {
            RequiresCanRead(instance, "instance");
            ContractUtils.RequiresNotNull(propertyName, "indexerName");
            PropertyInfo pi = FindInstanceProperty(instance.Type, propertyName, arguments);
            return Property(instance, pi, arguments);
        }

        #region methods for finding a PropertyInfo by its name
        /// <summary>
        /// The method finds the instance property with the specified name in a type. The property's type signature needs to be compatible with
        /// the arguments if it is a indexer. If the arguments is null or empty, we get a normal property.
        /// </summary>
        private static PropertyInfo FindInstanceProperty(Type type, string propertyName, Expression[] arguments) {
            // bind to public names first
            BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase | BindingFlags.FlattenHierarchy;
            PropertyInfo pi = FindProperty(type, propertyName, arguments, flags);
            if (pi == null) {
                flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.IgnoreCase | BindingFlags.FlattenHierarchy;
                pi = FindProperty(type, propertyName, arguments, flags);
            }
            if (pi == null) {
                if (arguments == null || arguments.Length == 0) {
                    throw Error.InstancePropertyWithoutParameterNotDefinedForType(propertyName, type);
                } else {
                    throw Error.InstancePropertyWithSpecifiedParametersNotDefinedForType(propertyName, GetArgTypesString(arguments), type);
                }
            }
            return pi;
        }

        private static string GetArgTypesString(Expression[] arguments) {
            StringBuilder argTypesStr = new StringBuilder();
            var isFirst = true;
            argTypesStr.Append("(");
            foreach (var t in arguments.Select(arg => arg.Type)) {
                if (!isFirst) {
                    argTypesStr.Append(", ");
                }
                argTypesStr.Append(t.Name);
                isFirst = false;
            }
            argTypesStr.Append(")");
            return argTypesStr.ToString();
        }

        private static PropertyInfo FindProperty(Type type, string propertyName, Expression[] arguments, BindingFlags flags) {
            MemberInfo[] members = type.FindMembers(MemberTypes.Property, flags, Type.FilterNameIgnoreCase, propertyName);
            if (members == null || members.Length == 0)
                return null;

            PropertyInfo pi;
            var propertyInfos = members.Map(t => (PropertyInfo)t);
            int count = FindBestProperty(propertyInfos, arguments, out pi);

            if (count == 0)
                return null;
            if (count > 1)
                throw Error.PropertyWithMoreThanOneMatch(propertyName, type);
            return pi;
        }

        private static int FindBestProperty(IEnumerable<PropertyInfo> properties, Expression[] args, out PropertyInfo property) {
            int count = 0;
            property = null;
            foreach (PropertyInfo pi in properties) {
                if (pi != null && IsCompatible(pi, args)) {
                    if (property == null) {
                        property = pi;
                        count = 1;
                    }
                    else {
                        count++;
                    }
                }
            }
            return count;
        }

        private static bool IsCompatible(PropertyInfo pi, Expression[] args) {
            MethodInfo mi;

            mi = pi.GetGetMethod(true);
            ParameterInfo[] parms;
            if (mi != null) {
                parms = mi.GetParametersCached();
            } else {
                mi = pi.GetSetMethod(true);
                //The setter has an additional parameter for the value to set,
                //need to remove the last type to match the arguments.
                parms = mi.GetParametersCached().RemoveLast();
            }
            
            if (mi == null) {
                return false;
            }
            if (args == null) {
                return parms.Length == 0;
            }
            
            if (parms.Length != args.Length)
                return false;
            for (int i = 0; i < args.Length; i++) {
                if (args[i] == null) return false;
                if (!TypeUtils.AreReferenceAssignable(parms[i].ParameterType, args[i].Type)) {
                    return false;
                }
            }
            return true;
        }
        #endregion

        /// <summary>
        /// Creates an <see cref="IndexExpression"/> representing the access to an indexed property.
        /// </summary>
        /// <param name="instance">The object to which the property belongs. If the property is static/shared, it must be null.</param>
        /// <param name="indexer">The <see cref="PropertyInfo"/> that represents the property to index.</param>
        /// <param name="arguments">An array of <see cref="Expression"/> objects that are used to index the property.</param>
        /// <returns>The created <see cref="IndexExpression"/>.</returns>
        public static IndexExpression Property(Expression instance, PropertyInfo indexer, params Expression[] arguments) {
            return Property(instance, indexer, (IEnumerable<Expression>)arguments);
        }

        /// <summary>
        /// Creates an <see cref="IndexExpression"/> representing the access to an indexed property.
        /// </summary>
        /// <param name="instance">The object to which the property belongs. If the property is static/shared, it must be null.</param>
        /// <param name="indexer">The <see cref="PropertyInfo"/> that represents the property to index.</param>
        /// <param name="arguments">An <see cref="IEnumerable{T}"/> of <see cref="Expression"/> objects that are used to index the property.</param>
        /// <returns>The created <see cref="IndexExpression"/>.</returns>
        public static IndexExpression Property(Expression instance, PropertyInfo indexer, IEnumerable<Expression> arguments) {
            var argList = arguments.ToReadOnly();
            ValidateIndexedProperty(instance, indexer, ref argList);
            return new IndexExpression(instance, indexer, argList);
        }

        // CTS places no restrictions on properties (see ECMA-335 8.11.3),
        // so we validate that the property conforms to CLS rules here.
        //
        // Does reflection help us out at all? Expression.Property skips all of
        // these checks, so either it needs more checks or we need less here.
        private static void ValidateIndexedProperty(Expression instance, PropertyInfo property, ref ReadOnlyCollection<Expression> argList) {

            // If both getter and setter specified, all their parameter types
            // should match, with exception of the last setter parameter which
            // should match the type returned by the get method.
            // Accessor parameters cannot be ByRef.

            ContractUtils.RequiresNotNull(property, "property");
            ContractUtils.Requires(!property.PropertyType.IsByRef, "property", Strings.PropertyCannotHaveRefType);
            ContractUtils.Requires(property.PropertyType != typeof(void), "property", Strings.PropertyTypeCannotBeVoid);

            ParameterInfo[] getParameters = null;
            MethodInfo getter = property.GetGetMethod(true);
            if (getter != null) {
                getParameters = getter.GetParametersCached();
                ValidateAccessor(instance, getter, getParameters, ref argList);
            }

            MethodInfo setter = property.GetSetMethod(true);
            if (setter != null) {
                ParameterInfo[] setParameters = setter.GetParametersCached();
                ContractUtils.Requires(setParameters.Length > 0, "property", Strings.SetterHasNoParams);

                // valueType is the type of the value passed to the setter (last parameter)
                Type valueType = setParameters[setParameters.Length - 1].ParameterType;
                ContractUtils.Requires(!valueType.IsByRef, "property", Strings.PropertyCannotHaveRefType);
                ContractUtils.Requires(setter.ReturnType == typeof(void), "property", Strings.SetterMustBeVoid);
                ContractUtils.Requires(property.PropertyType == valueType, "property", Strings.PropertyTyepMustMatchSetter);

                if (getter != null) {
                    ContractUtils.Requires(!(getter.IsStatic ^ setter.IsStatic), "property", Strings.BothAccessorsMustBeStatic);
                    ContractUtils.Requires(getParameters.Length == setParameters.Length - 1, "property", Strings.IndexesOfSetGetMustMatch);

                    for (int i = 0; i < getParameters.Length; i++) {
                        ContractUtils.Requires(getParameters[i].ParameterType == setParameters[i].ParameterType, "property", Strings.IndexesOfSetGetMustMatch);
                    }
                } else {
                    ValidateAccessor(instance, setter, setParameters.RemoveLast(), ref argList);
                }
            }

            if (getter == null && setter == null) {
                throw Error.PropertyDoesNotHaveAccessor(property);
            }
        }

        private static void ValidateAccessor(Expression instance, MethodInfo method, ParameterInfo[] indexes, ref ReadOnlyCollection<Expression> arguments) {
            ContractUtils.RequiresNotNull(arguments, "arguments");

            ValidateMethodInfo(method);
            ContractUtils.Requires((method.CallingConvention & CallingConventions.VarArgs) == 0, "method", Strings.AccessorsCannotHaveVarArgs);
            if (method.IsStatic) {
                ContractUtils.Requires(instance == null, "instance", Strings.OnlyStaticMethodsHaveNullInstance);
            } else {
                ContractUtils.Requires(instance != null, "method", Strings.OnlyStaticMethodsHaveNullInstance);
                RequiresCanRead(instance, "instance");
                ValidateCallInstanceType(instance.Type, method);
            }

            ValidateAccessorArgumentTypes(method, indexes, ref arguments);
        }

        private static void ValidateAccessorArgumentTypes(MethodInfo method, ParameterInfo[] indexes, ref ReadOnlyCollection<Expression> arguments) {
            if (indexes.Length > 0) {
                if (indexes.Length != arguments.Count) {
                    throw Error.IncorrectNumberOfMethodCallArguments(method);
                }
                Expression[] newArgs = null;
                for (int i = 0, n = indexes.Length; i < n; i++) {
                    Expression arg = arguments[i];
                    ParameterInfo pi = indexes[i];
                    RequiresCanRead(arg, "arguments");

                    Type pType = pi.ParameterType;
                    ContractUtils.Requires(!pType.IsByRef, "indexes", Strings.AccessorsCannotHaveByRefArgs);
                    TypeUtils.ValidateType(pType);

                    if (!TypeUtils.AreReferenceAssignable(pType, arg.Type)) {
                        if (TypeUtils.IsSameOrSubclass(typeof(LambdaExpression), pType) && pType.IsAssignableFrom(arg.GetType())) {
                            arg = Expression.Quote(arg);
                        } else {
                            throw Error.ExpressionTypeDoesNotMatchMethodParameter(arg.Type, pType, method);
                        }
                    }
                    if (newArgs == null && arg != arguments[i]) {
                        newArgs = new Expression[arguments.Count];
                        for (int j = 0; j < i; j++) {
                            newArgs[j] = arguments[j];
                        }
                    }
                    if (newArgs != null) {
                        newArgs[i] = arg;
                    }
                }
                if (newArgs != null) {
                    arguments = new TrueReadOnlyCollection<Expression>(newArgs);
                }

            } else if (arguments.Count > 0) {
                throw Error.IncorrectNumberOfMethodCallArguments(method);
            }
        }

        #endregion
    }
}
