using System;
using System.Linq;
using NMica.Utils;

namespace NMica.Tasks
{
    [Flags]
    public enum Layer
    {
        All          = 0b_1111,
        Package      = 0b_0001,
        EarlyPackage = 0b_0010,
        Project      = 0b_0100,
        App          = 0b_1000,
    }

    public static class KnownLayers
    {
        public static Layer[] DependencyLayers => Layer.All.ToValuesArray().Where(x => x != Layer.App).ToArray();
        public static Layer[] AllLayers => Layer.All.ToValuesArray();
    }
}