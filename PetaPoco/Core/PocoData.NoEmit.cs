#if !USE_REFLECTION_EMIT
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using PetaPoco.Internal;

namespace PetaPoco.Core
{
    public partial class PocoData
    {
        private readonly Cache<Tuple<string, string, int, int>, Delegate> PocoFactories
            = new Cache<Tuple<string, string, int, int>, Delegate>();

        /// <inheritdoc />
        public Delegate GetFactory(string sql, string connectionString, int firstColumn, int columnCount, IDataReader reader, IMapper defaultMapper)
        {
            var key = Tuple.Create(sql, connectionString, firstColumn, columnCount);
            return PocoFactories.GetOrAdd(key, () => BuildFactoryDelegate(reader, defaultMapper, firstColumn, columnCount));
        }

        private Delegate BuildFactoryDelegate(IDataReader reader, IMapper mapper, int firstColumn, int columnCount)
        {
            // signature: Func<IDataReader, object>
            var readerParam = Expression.Parameter(typeof(IDataReader), "r");

            // handle ExpandoObject
            if (Type == typeof(object))
            {
                var addMethod = typeof(IDictionary<string, object>).GetMethod("Add");
                var expandoCtor = typeof(System.Dynamic.ExpandoObject).GetConstructor(Type.EmptyTypes);
                var dictType = typeof(IDictionary<string, object>);

                // var obj = new ExpandoObject();
                var objVar = Expression.Variable(typeof(object), "obj");
                var expandoVar = Expression.Variable(typeof(IDictionary<string, object>), "dict");
                var expressions = new List<Expression>
                {
                    Expression.Assign(objVar, Expression.New(expandoCtor)),
                    Expression.Assign(expandoVar, Expression.Convert(objVar, dictType))
                };

                // for each column: dict.Add(name, value)
                for (int i = firstColumn; i < firstColumn + columnCount; i++)
                {
                    var nameConst = Expression.Constant(reader.GetName(i));
                    var idxConst = Expression.Constant(i);

                    // r.IsDBNull(i)
                    var isDbNullCall = Expression.Call(readerParam, typeof(IDataRecord).GetMethod("IsDBNull"), idxConst);
                    // r.GetValue(i)
                    var getValueCall = Expression.Call(readerParam, fnGetValue, idxConst);
                    var valueExpr = Expression.Convert(getValueCall, typeof(object));

                    // conditional: isDbNull ? null : value
                    var safeValue = Expression.Condition(
                        isDbNullCall,
                        Expression.Constant(null, typeof(object)),
                        valueExpr
                    );

                    expressions.Add(
                        Expression.Call(
                            expandoVar,
                            addMethod,
                            nameConst,
                            safeValue)
                    );
                }

                expressions.Add(objVar);
                var body = Expression.Block(new[] { objVar, expandoVar }, expressions);
                var lambda = Expression.Lambda(body.GetType(), body, readerParam);
                return Expression.Lambda(Expression.GetFuncType(typeof(IDataReader), typeof(object)), body, readerParam).Compile();
            }

            // value types, string, byte[]
            if (Type.IsValueType || Type == typeof(string) || Type == typeof(byte[]))
            {
                var idx = Expression.Constant(firstColumn);
                var isDbNull = Expression.Call(readerParam, typeof(IDataRecord).GetMethod("IsDBNull"), idx);
                var getVal = Expression.Call(readerParam, fnGetValue, idx);
                var valObj = Expression.Convert(getVal, typeof(object));

                // converter logic
                var converter = GetConverter(mapper, null, reader.GetFieldType(firstColumn), Type);
                Expression converted = converter != null
                    ? Expression.Convert(
                        Expression.Call(Expression.Constant(converter), fnInvoke, valObj),
                        Type)
                    : Expression.Convert(valObj, Type);

                // if DbNull then null (or default)
                Expression resultExpr = Expression.Condition(
                    isDbNull,
                    Expression.Default(Type),
                    converted);

                return Expression.Lambda(Expression.GetFuncType(typeof(IDataReader), Type), resultExpr, readerParam).Compile();
            }

            // reference POCO
            var ctorInfo = Type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null)
                           ?? throw new InvalidOperationException($"Type [{Type.FullName}] requires a default constructor");

            var pocoVar = Expression.Variable(Type, "poco");
            var blockExprs = new List<Expression>
            {
                Expression.Assign(pocoVar, Expression.New(ctorInfo))
            };

            for (int i = firstColumn; i < firstColumn + columnCount; i++)
            {
                var colName = reader.GetName(i);
                if (!Columns.TryGetValue(colName, out var pc))
                    continue;

                var idxConst = Expression.Constant(i);
                var isDbNull = Expression.Call(readerParam, typeof(IDataRecord).GetMethod("IsDBNull"), idxConst);
                var getVal = Expression.Call(readerParam, fnGetValue, idxConst);
                var valObj = Expression.Convert(getVal, typeof(object));

                var converter = GetConverter(mapper, pc, reader.GetFieldType(i), pc.PropertyInfo.PropertyType);
                Expression valueExpr;
                if (converter != null)
                {
                    valueExpr = Expression.Convert(
                        Expression.Call(Expression.Constant(converter), fnInvoke, valObj),
                        pc.PropertyInfo.PropertyType);
                }
                else
                {
                    valueExpr = Expression.Convert(valObj, pc.PropertyInfo.PropertyType);
                }

                var assign = Expression.IfThen(
                    Expression.Not(isDbNull),
                    Expression.Call(
                        Expression.Convert(pocoVar, pc.PropertyInfo.DeclaringType),
                        pc.PropertyInfo.GetSetMethod(true),
                        valueExpr)
                );
                blockExprs.Add(assign);
            }

            // OnLoaded hook
            var onLoaded = RecurseInheritedTypes<MethodInfo>(Type,
                t => t.GetMethod("OnLoaded", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null));
            if (onLoaded != null)
            {
                blockExprs.Add(
                    Expression.Call(
                        Expression.Convert(pocoVar, Type),
                        onLoaded));
            }

            blockExprs.Add(pocoVar);
            var bodyBlock = Expression.Block(new[] { pocoVar }, blockExprs);
            var delegateType = Expression.GetFuncType(typeof(IDataReader), Type);

            return Expression.Lambda(delegateType, bodyBlock, readerParam).Compile();
        }
    }
}
#endif
