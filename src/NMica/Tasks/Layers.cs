using System;
using System.Collections.Generic;
using System.Linq;

namespace NMica.Tasks;

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

internal static class FlagsEnumExtensions
{
    /// <summary>Enumerate the single-bit members of a <c>[Flags]</c> enum value.</summary>
    public static T[] ToValuesArray<T>(this T value) where T : Enum
    {
        var bits = (int)(object)value;
        var result = new List<T>();
        for (var b = 0; bits != 0; b++, bits >>= 1)
        {
            if ((bits & 1) == 0) continue;
            var single = 1 << b;
            if (Enum.GetName(typeof(T), single) is not null)
                result.Add((T)Enum.ToObject(typeof(T), single));
        }
        return result.ToArray();
    }
}
