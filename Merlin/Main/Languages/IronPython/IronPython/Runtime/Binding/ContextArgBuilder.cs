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

using System.Collections.Generic;
using System.Linq.Expressions;
using System.Diagnostics;
using System.Reflection;
using System;
using System.Dynamic;
using Microsoft.Scripting.Actions.Calls;

namespace IronPython.Runtime.Binding {

    /// <summary>
    /// ArgBuilder which provides the CodeContext parameter to a method.
    /// </summary>
    public sealed class ContextArgBuilder : ArgBuilder {
        private static Func<object[], object> _readFunc = (Func<object[], object>)Delegate.CreateDelegate(typeof(Func<object[], object>), 0, typeof(ArgBuilder).GetMethod("ArgumentRead"));
        public ContextArgBuilder(ParameterInfo info) 
            : base(info){
        }

        public override int Priority {
            get { return -1; }
        }

        public override int ConsumedArgumentCount {
            get { return 0; }
        }

        protected override Expression ToExpression(OverloadResolver resolver, IList<Expression> parameters, bool[] hasBeenUsed) {
            return ((PythonOverloadResolver)resolver).ContextExpression;
        }

        protected override Func<object[], object> ToDelegate(OverloadResolver resolver, IList<DynamicMetaObject> knownTypes, bool[] hasBeenUsed) {
            return _readFunc;
        }
    }
}
