using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace BondExpressionsInterceptor
{
    internal static class Audit
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void FailArgNull(string paramName)
        {
            throw new ArgumentNullException(paramName);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void FailArgRule(string message)
        {
            throw new ArgumentException(message);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ArgNotNull(object value, string paramName)
        {
            if (value == null)
            {
                FailArgNull(paramName);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ArgRule(bool invariant, string message)
        {
            if (!invariant)
            {
                FailArgRule(message);
            }
        }
    }
}
