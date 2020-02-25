// Copyright 2019 Maintainers of NUKE.
// Distributed under the MIT License.
// https://github.com/nuke-build/nuke/blob/master/LICENSE

using System;
using System.Linq;
using static Nuke.Common.IO.PathConstruction;

namespace Nuke.Common.IO
{
    [Serializable]
    public class UnixRelativePath : RelativePath
    {
        protected UnixRelativePath(string path, char? separator)
            : base(path, separator)
        {
        }

        public static explicit operator UnixRelativePath(string path)
        {
            return new UnixRelativePath(NormalizePath(path, UnixSeparator), UnixSeparator);
        }
    }
}
