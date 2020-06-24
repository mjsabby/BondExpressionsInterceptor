using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using Bond;
using Bond.Expressions;
using Bond.IO.Safe;
using Bond.Protocols;
using Microsoft.Search.Platform.Parallax;

namespace BondExpressionsInterceptor
{
    using Examples;

    class Program
    {
        static void Main(string[] args)
        {

            var asmName = new AssemblyName("Foo");
            var asmBuilder = AssemblyBuilder.DefineDynamicAssembly
                (asmName, AssemblyBuilderAccess.RunAndSave);
            var moduleBuilder = asmBuilder.DefineDynamicModule("Foo", "Foo.exe");

            var typeBuilder = moduleBuilder.DefineType("Program", TypeAttributes.Public);
            var methodBuilder = typeBuilder.DefineMethod("Main",
                MethodAttributes.Static, typeof(void), new[] { typeof(string) });

            var serializer = new Serializer<CompactBinaryWriter<OutputBuffer>>(typeof(AnswerConfig));
            var expression = serializer.GetExpressions().First();

            var visitor = new MyVisitor();
            LambdaExpression exp = (LambdaExpression)visitor.Visit(expression);

            exp.CompileToMethod(methodBuilder);

            typeBuilder.CreateType();
            asmBuilder.SetEntryPoint(methodBuilder);
            asmBuilder.Save("Foo.exe");

            var output = new OutputBuffer();
            var writer = new CompactBinaryWriter<OutputBuffer>(output);

            //BondExpressionsInterceptor.Serialize.To<CompactBinaryWriter<OutputBuffer>, Record>(writer, src);

            var input = new InputBuffer(output.Data);
            var reader = new CompactBinaryReader<InputBuffer>(input);

            var dst = Deserialize<Record>.From(reader);
        }
    }

    class MyVisitor : ExpressionVisitor
    {
        protected override Expression VisitConstant(ConstantExpression node)
        {
            if (node.Value.GetType() == typeof(Metadata))
            {
                return Expression.Constant(null, typeof(Metadata)); //typeof(Metadata).GetConstructors()[0]);
            }

            return base.VisitConstant(node);
        }
    }

}
