namespace BondExpressionsInterceptor
{
    using System.Linq.Expressions;
    using System.Reflection;

    internal static class DataExpression
    {
        // Similar to Expression.PropertyOrField but considers only members declared
        // in the type, ignoring inherited members.
        public static MemberExpression PropertyOrField(Expression expression, string name)
        {
            var property = expression.Type.GetTypeInfo().GetDeclaredProperty(name);
            return (property != null) ?
                Expression.Property(expression, property) :
                Expression.Field(expression, expression.Type.GetTypeInfo().GetDeclaredField(name));
        }
    }
}
