﻿// Copyright 2019 Maintainers of NUKE.
// Distributed under the MIT License.
// https://github.com/nuke-build/nuke/blob/master/LICENSE

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

// ReSharper disable CompareNonConstrainedGenericWithNull

namespace Nuke.Common
{
    
    [DebuggerNonUserCode]
    [DebuggerStepThrough]
    public static class ControlFlow
    {
        /// <summary>
        /// Logs a message as failure. Halts execution.
        /// </summary>
        
        
        public static void Fail(string format, params object[] args)
        {
            Fail(string.Format(format, args));
        }

        /// <summary>
        /// Logs a message as failure. Halts execution.
        /// </summary>
        
        public static void Fail(object value)
        {
            Fail(value.ToString());
        }

        /// <summary>
        /// Logs a message as failure. Halts execution.
        /// </summary>
        
        public static void Fail(string text)
        {
            throw new Exception(text);
        }
        

        /// <summary>
        /// Asserts a condition to be true, halts otherwise.
        /// </summary>
        public static void Assert(
            bool condition,
            string text)
        {
            if (!condition)
                Fail($"Assertion failed: {text}");
        }

        /// <summary>
        /// Asserts an object to be not null, halts otherwise.
        /// </summary>
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T NotNull<T>(
            this T obj,
            string text = null)
        {
            if (obj == null)
                Fail($"Assertion failed: {text ?? $"{typeof(T).FullName} != null"}");
            return obj;
        }

        /// <summary>
        /// Checks an object to be not null, calling <see cref="Logger.Warn(string)"/> otherwise.
        /// </summary>
        

        /// <summary>
        /// Asserts a collection to be not empty, halts otherwise.
        /// </summary>
        public static IReadOnlyCollection<T> NotEmpty<T>( this IEnumerable<T> enumerable, string message = null)
        {
            var collection = enumerable.NotNull("enumerable != null").ToList().AsReadOnly();
            Assert(collection.Count > 0, message ?? $"IEnumerable{typeof(T).FullName}.Count > 0");
            return collection;
        }

        /// <summary>
        /// Asserts a collection to contain only <em>non-null</em> elements, halts otherwise.
        /// </summary>
        public static IReadOnlyCollection<T> NoNullItems<T>( this IEnumerable<T> enumerable)
        {
            var collection = enumerable.NotNull("enumerable != null").ToList().AsReadOnly();
            Assert(collection.All(x => x != null), $"IEnumerable{typeof(T).FullName}.All(x => x != null)");
            return collection;
        }

    }
}
