using System;
using System.Collections.Generic;

namespace NMica.Utils
{
    public static class Extensions
    {
        public static T[] ToValuesArray<T>(this T e) where T : Enum
        {
            int v = (int)(object)e;
            var result = new List<T>();
        
            for(var bit = 0; v != 0; bit++)
            {
                if((v & 1) != 0)
                {
                    var enumValue = 1 << bit;
                    var name = Enum.GetName(e.GetType(), enumValue);
                    if(name != null)
                        result.Add((T)Enum.ToObject(e.GetType(), enumValue));
                
                }
                v = v >> 1;
            }
       
            return result.ToArray();
        
        }
    }
}