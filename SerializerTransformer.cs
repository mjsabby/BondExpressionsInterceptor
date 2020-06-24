﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using Bond;
using Bond.Expressions;
using Bond.Protocols;

namespace BondExpressionsInterceptor
{
    internal static class TypeDefExtensions
    {
        // We intentionally don't implement GetHashCode override for Bond generated classes like
        // TypeDef because they are mutable and we can't guarantee value semantics in general case.
        // CalculateHashCode is used internally by parsers to implement their own GetHashCode
        // override which some transforms expect.
        internal static int CalculateHashCode(this TypeDef typeDef)
        {
            return (typeDef == null) ? 0 : (typeDef.struct_def | ((int)typeDef.id << 16))
                                           ^ typeDef.element.CalculateHashCode() ^ typeDef.key.CalculateHashCode();
        }
    }

    internal class TypeDefComparer : IEqualityComparer<RuntimeSchema>
    {
        public bool Equals(RuntimeSchema x, RuntimeSchema y)
        {
            return Comparer.Equal(x.TypeDef, y.TypeDef);
        }

        public int GetHashCode(RuntimeSchema x)
        {
            return x.TypeDef.CalculateHashCode();
        }
    }

    internal class SerializerTransform<R, W> : SerializerGenerator<R, W>
    {
        delegate Expression SerializeWithSchema(IParser parser, RuntimeSchema schema);

        static readonly Expression noMetadata = Expression.Constant(null, typeof(Metadata));
        readonly RuntimeSchema runtimeSchema;
        readonly ProtocolWriter<W> writer = new ProtocolWriter<W>();
        readonly Dictionary<RuntimeSchema, Serialize> serializeDelegates =
            new Dictionary<RuntimeSchema, Serialize>(new TypeDefComparer());
        readonly bool inlineNested;
        static readonly bool untaggedWriter = false;
            //typeof(IUntaggedProtocolReader).IsAssignableFrom(typeof(W).GetAttribute<ReaderAttribute>().ReaderType);
        static readonly bool binaryWriter = true;
            //|| typeof(ITaggedProtocolReader).IsAssignableFrom(typeof(W).GetAttribute<ReaderAttribute>().ReaderType);

        public SerializerTransform(Expression<Action<R, W, int>> deferredSerialize, RuntimeSchema schema, bool inlineNested = true)
            : base(deferredSerialize)
        {
            runtimeSchema = schema;
            this.inlineNested = inlineNested;
        }

        public SerializerTransform(Expression<Action<R, W, int>> deferredSerialize, Type type, bool inlineNested = true)
            : this(deferredSerialize, Schema.GetRuntimeSchema(type), inlineNested)
        { }

        public override IEnumerable<Expression<Action<R, W>>> Generate(IParser parser)
        {
            if (runtimeSchema.HasValue)
            {
                GenerateSerialize(Struct, parser, runtimeSchema);
            }
            else
            {
                GenerateSerialize(Struct, parser);
            }
            return SerializeActions;
        }

        Expression GenerateSerialize(Serialize serialize, IParser parser)
        {
            return GenerateSerialize(serialize, parser, writer.Param, inline: false);
        }

        Expression GenerateSerialize(SerializeWithSchema serializeWithSchema, IParser parser, RuntimeSchema schema)
        {
            Debug.Assert(schema.HasValue);

            Serialize serialize;
            if (!serializeDelegates.TryGetValue(schema, out serialize))
            {
                serialize = serializeDelegates[schema] = p => serializeWithSchema(p, schema);
            }
            // Transcoding from tagged protocol with runtime schema generates enormous expression tree
            // and for large schemas JIT fails to compile resulting lambda (InvalidProgramException).
            // As a workaround we don't inline nested serialize expressions in this case.
            var inline = !typeof(ITaggedProtocolReader).IsAssignableFrom(parser.ReaderParam.Type);

            inline = inline && (this.inlineNested || !schema.IsStruct);

            return GenerateSerialize(serialize, parser, writer.Param, inline);
        }

        Expression Struct(IParser parser)
        {
            return Struct(parser, RuntimeSchema.Empty);
        }

        Expression Struct(IParser parser, RuntimeSchema schema)
        {
            return Struct(parser, schema, false);
        }

        Expression Struct(IParser parser, RuntimeSchema schema, bool isBase)
        {
            var metadata = schema.HasValue ? Expression.Constant(schema.StructDef.metadata) : noMetadata;
            var baseSchema = schema.HasBase ? schema.GetBaseSchema() : RuntimeSchema.Empty;

            return parser.Apply(new Transform(
                Begin: () => isBase ? writer.WriteBaseBegin(metadata) : writer.WriteStructBegin(metadata),
                End: () => isBase ? writer.WriteBaseEnd() : writer.WriteStructEnd(),
                Fields: schema.HasValue ?
                    from field in schema.StructDef.fields
                    select new Field(
                        Id: field.id,
                        Value: (fieldParser, fieldType) =>
                            Expression.Block(
                                writer.WriteFieldBegin(fieldType, field.id, field.metadata),
                                Value(fieldParser, fieldType, schema.GetFieldSchema(field)),
                                writer.WriteFieldEnd()),
                        Omitted: () => writer.WriteFieldOmitted(field.type.id, field.id, field.metadata)) :
                    null,
                UnknownField: (fieldParser, fieldType, fieldId) =>
                    Expression.Block(
                        writer.WriteFieldBegin(fieldType, fieldId, noMetadata),
                        Value(fieldParser, fieldType),
                        writer.WriteFieldEnd()),
                Base: baseParser =>
                    baseSchema.HasValue ? Struct(baseParser, baseSchema, isBase: true) : Expression.Empty(),
                UnknownEnd: () => writer.WriteBaseEnd()));
        }

        Expression Container(IParser parser)
        {
            return Container(parser, RuntimeSchema.Empty);
        }

        Expression Container(IParser parser, RuntimeSchema schema)
        {
            var expectedValueType = schema.HasValue ? schema.TypeDef.element.id : (BondDataType?)null;

            return parser.Container(expectedValueType,
                (valueParser, elementType, next, count, arraySegment) =>
                {
                    var body = ControlExpression.While(next,
                        Expression.Block(
                            writer.WriteItemBegin(),
                            schema.HasValue ?
                                Value(valueParser, elementType, schema.GetElementSchema()) :
                                Value(valueParser, elementType),
                            writer.WriteItemEnd()));

                    var blob = parser.Blob(count);
                    if ((blob != null) || (arraySegment != null))
                    {
                        body = PrunedExpression.IfThenElse(
                            Expression.Equal(elementType, Expression.Constant(BondDataType.BT_INT8)),
                            writer.WriteBytes(arraySegment ?? blob),
                            body);

                        // For binary protocols we can write blob directly using protocols's WriteBytes
                        // even if the container is not a blob (blob is BT_LIST of BT_INT8).
                        if (binaryWriter)
                        {
                            body = PrunedExpression.IfThenElse(
                                Expression.Equal(elementType, Expression.Constant(BondDataType.BT_UINT8)),
                                writer.WriteBytes(arraySegment ?? blob),
                                body);
                        }
                    }

                    return Expression.Block(
                        writer.WriteContainerBegin(count, elementType),
                        body,
                        writer.WriteContainerEnd());
                });
        }

        Expression Map(IParser parser)
        {
            return Map(parser, RuntimeSchema.Empty);
        }

        Expression Map(IParser parser, RuntimeSchema schema)
        {
            var expectedValueType = schema.HasValue ? schema.TypeDef.element.id : (BondDataType?)null;
            var expectedKeyType = schema.HasValue ? schema.TypeDef.key.id : (BondDataType?)null;

            return parser.Map(expectedKeyType, expectedValueType,
                (keyParser, valueParser, keyType, valueType, nextKey, nextValue, count) =>
                Expression.Block(
                    writer.WriteContainerBegin(count, keyType, valueType),
                        ControlExpression.While(nextKey,
                            Expression.Block(
                                writer.WriteItemBegin(),
                                schema.HasValue ?
                                    Value(keyParser, keyType, schema.GetKeySchema()) :
                                    Value(keyParser, keyType),
                                writer.WriteItemEnd(),
                                nextValue,
                                writer.WriteItemBegin(),
                                schema.HasValue ?
                                    Value(valueParser, valueType, schema.GetElementSchema()) :
                                    Value(valueParser, valueType),
                                writer.WriteItemEnd())),
                    writer.WriteContainerEnd()));
        }

        Expression Value(IParser parser, Expression valueType)
        {
            if (parser.IsBonded)
            {
                return parser.Bonded(value =>
                    writer.WriteBonded(PrunedExpression.Convert(value, typeof(IBonded))));
            }

            var switchCases = new List<DeferredSwitchCase>
            {
                PrunedExpression.SwitchCase(
                    () => GenerateSerialize(Container, parser),
                    BondDataType.BT_LIST,
                    BondDataType.BT_SET),
                PrunedExpression.SwitchCase(
                    () => GenerateSerialize(Map, parser),
                    BondDataType.BT_MAP),
                PrunedExpression.SwitchCase(
                    () => GenerateSerialize(Struct, parser),
                    BondDataType.BT_STRUCT)
            };

            switchCases.AddRange(
                from type in new[]
                {
                    BondDataType.BT_BOOL,
                    BondDataType.BT_UINT8,
                    BondDataType.BT_UINT16,
                    BondDataType.BT_UINT32,
                    BondDataType.BT_UINT64,
                    BondDataType.BT_FLOAT,
                    BondDataType.BT_DOUBLE,
                    BondDataType.BT_STRING,
                    BondDataType.BT_INT8,
                    BondDataType.BT_INT16,
                    BondDataType.BT_INT32,
                    BondDataType.BT_INT64,
                    BondDataType.BT_WSTRING
                }
                select
                    PrunedExpression.SwitchCase(
                        () => parser.Scalar(Expression.Constant(type), type,
                            value => writer.Write(value, type)),
                        type));

            return PrunedExpression.Switch(
                valueType,
                ThrowExpression.InvalidTypeException(valueType),
                switchCases);
        }

        Expression Value(IParser parser, Expression valueType, RuntimeSchema schema)
        {
            Debug.Assert(schema.HasValue);

            if (parser.IsBonded || (untaggedWriter && schema.IsBonded))
                return parser.Bonded(value =>
                    writer.WriteBonded(PrunedExpression.Convert(value, typeof(IBonded))));


            if (schema.IsStruct)
                return GenerateSerialize(Struct, parser, schema);

            if (schema.IsMap)
                return GenerateSerialize(Map, parser, schema);

            if (schema.IsContainer)
                return GenerateSerialize(Container, parser, schema);

            return parser.Scalar(valueType, schema.TypeDef.id,
                value => writer.Write(value, schema.TypeDef.id));
        }
    }
}
