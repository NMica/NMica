// Copyright 2019 Maintainers of NUKE.
// Distributed under the MIT License.
// https://github.com/nuke-build/nuke/blob/master/LICENSE

using System;
using System.Linq;
using static Nuke.Common.IO.PathConstruction;

namespace Nuke.Common.IO
{
    [Serializable]
    public class WinRelativePath : RelativePath
    {
        protected WinRelativePath(string path, char? separator)
            : base(path, separator)
        {
        }

        public static explicit operator WinRelativePath(string path)
        {
            return new WinRelativePath(NormalizePath(path, WinSeparator), WinSeparator);
        }
    }
}
