// Copyright 2019 Maintainers of NUKE.
// Distributed under the MIT License.
// https://github.com/nuke-build/nuke/blob/master/LICENSE

using System;
using System.Collections.Generic;
using System.Linq;

namespace Nuke.Common.Utilities
{
    public static partial class StringExtensions
    {
        
        public static string Join(this IEnumerable<string> enumerable, string separator)
        {
            return string.Join(separator, enumerable);
        }

        
        public static string Join(this IEnumerable<string> enumerable, char separator)
        {
            return enumerable.Join(separator.ToString());
        }

        
        public static string JoinSpace(this IEnumerable<string> values)
        {
            return values.Join(" ");
        }

        
        public static string JoinComma(this IEnumerable<string> values)
        {
            return values.Join(", ");
        }

        
        public static string JoinCommaOr(this IEnumerable<string> values)
        {
            var valuesList = values.ToArray();
            return valuesList.Length >= 2
                ? valuesList.Reverse().Skip(1).Reverse().JoinComma() + ", or " + valuesList.Last()
                : valuesList.JoinComma();
        }

        
        public static string JoinCommaAnd(this IEnumerable<string> values)
        {
            var valuesList = values.ToArray();
            return valuesList.Length >= 2
                ? valuesList.Reverse().Skip(1).Reverse().JoinComma() + ", and " + valuesList.Last()
                : valuesList.JoinComma();
        }

        
    }
}
