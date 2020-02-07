using System;
using System.Linq;

namespace NMica.Tasks
{
    public enum Layer
    {
        All = 0,
        Package = 1,
        EarlyPackage,
        Project,
        App
    }

    public static class KnownLayers
    {
        public static Layer[] DependencyLayers => ((Layer[])Enum.GetValues(typeof(Layer))).Where(x => x != Layer.App && x != Layer.All).ToArray();
        public static Layer[] AllLayers => ((Layer[]) Enum.GetValues(typeof(Layer))).Where(x => x != Layer.All).ToArray();
    }
}