using System;
using RT.Util.ExtensionMethods;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace RT.DocGen
{
    static class Extensions
    {
        public static PropertyInfo GetBaseDefinition(this PropertyInfo property)
        {
            var getMethod = property.GetGetMethod();
            if (getMethod != null)
            {
                var baseMethod = getMethod.GetBaseDefinition();
                return baseMethod.DeclaringType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).FirstOrDefault(p => p.GetGetMethod() == baseMethod, property);
            }

            var setMethod = property.GetSetMethod();
            if (setMethod != null)
            {
                var baseMethod = setMethod.GetBaseDefinition();
                return baseMethod.DeclaringType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).FirstOrDefault(p => p.GetSetMethod() == baseMethod, property);
            }

            return property;
        }

        public static EventInfo GetBaseDefinition(this EventInfo eventt)
        {
            var addMethod = eventt.GetAddMethod();
            if (addMethod != null)
            {
                var baseMethod = addMethod.GetBaseDefinition();
                return baseMethod.DeclaringType.GetEvents(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).FirstOrDefault(e => e.GetAddMethod() == baseMethod, eventt);
            }

            return eventt;
        }
    }
}
