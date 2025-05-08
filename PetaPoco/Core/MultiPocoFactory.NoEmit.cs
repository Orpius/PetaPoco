#if !USE_REFLECTION_EMIT

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using PetaPoco.Core;

namespace PetaPoco.Internal
{
    internal class MultiPocoFactory
    {
        // Various cached stuff
        private static readonly Cache<Tuple<Type, ArrayKey<Type>, string, string, int>, object> MultiPocoFactories
            = new Cache<Tuple<Type, ArrayKey<Type>, string, string, int>, object>();

        private static readonly Cache<ArrayKey<Type>, object> AutoMappers
            = new Cache<ArrayKey<Type>, object>();

        // Instance data used by the MultiPoco factory delegate
        //private List<Delegate> delegates;

        //public Delegate GetItem(int index) => delegates[index];

        // Build (or fetch) a strongly-typed Func<T0, T1, …, T0> that wires child POCOs onto their parents
        public static object GetAutoMapper(Type[] types)
        {
            var key = new ArrayKey<Type>(types);
            return AutoMappers.GetOrAdd(key, () => BuildAutoMapper(types));
        }

        private static Delegate BuildAutoMapper(Type[] types)
        {
            // 1) Create one ParameterExpression per POCO type
            var parameters = types
                             .Select((t, i) => Expression.Parameter(t, "arg" + i))
                             .ToArray();

            var bodyExpressions = new List<Expression>();

            // 2) For each child type, find its matching parent property and emit a setter call
            for (int i = 1; i < types.Length; i++)
            {
                bool handled = false;
                for (int j = i - 1; j >= 0; j--)
                {
                    // look for a property on types[j] of type types[i]
                    var props = types[j]
                                .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                .Where(p => p.PropertyType == types[i])
                                .ToArray();

                    if (props.Length == 0)
                    {
                        continue;
                    }

                    if (props.Length > 1)
                    {
                        throw new InvalidOperationException(
                            $"Can't auto join {types[i].Name} as {types[j].Name} has more than one property of type {types[i].Name}");
                    }

                    MethodInfo setMethod = props[0].GetSetMethod(true)
                                           ?? throw new InvalidOperationException(
                                               $"{props[0].Name} is missing a setter or is readonly. If intentional, decorate with [PetaPoco.IgnoreAttribute].");

                    // emit:    arg{j}.Setter( arg{i} );
                    bodyExpressions.Add(
                        Expression.Call(parameters[j], setMethod, parameters[i])
                    );

                    handled = true;
                    break;
                }

                if (!handled)
                {
                    throw new InvalidOperationException($"Can't auto join {types[i].Name}");
                }
            }

            // 3) Finally return the root instance (arg0)
            bodyExpressions.Add(parameters[0]);

            // 4) Build and compile the lambda: Func<T0, T1, …, T0>
            var funcType = Expression.GetFuncType(types.Concat(new[] { types[0] }).ToArray());
            var lambda = Expression.Lambda(funcType,
                                           Expression.Block(bodyExpressions),
                                           parameters);

            return lambda.Compile();
        }

        // (same as before—this still uses your PocoData to get single-POCO factories)
        private static Delegate FindSplitPoint(
            Type typeThis, Type typeNext,
            string cs, string sql,
            IDataReader r, ref int pos,
            IMapper mapper)
        {
            if (typeNext == null)
            {
                return PocoData.ForType(typeThis, mapper).GetFactory(
                    sql, 
                    cs, 
                    pos, 
                    r.FieldCount - pos, 
                    r, 
                    mapper);
            }

            var pdThis = PocoData.ForType(typeThis, mapper);
            var pdNext = PocoData.ForType(typeNext, mapper);

            int firstCol = pos;
            var seen = new Dictionary<string, bool>();

            for (; pos < r.FieldCount; pos++)
            {
                var name = r.GetName(pos);

                if (seen.ContainsKey(name)
                    || (!pdThis.Columns.ContainsKey(name) && pdNext.Columns.ContainsKey(name)))
                {
                    return pdThis.GetFactory(sql, cs, firstCol, pos - firstCol, r, mapper);
                }

                seen[name] = true;
            }

            throw new InvalidOperationException(
                $"Couldn't find split point between {typeThis.Name} and {typeNext.Name}");
        }

        // Build a closure rather than emitting IL
        private static Func<IDataReader, object, TRet> CreateMultiPocoFactory<TRet>(
            Type[] types, string cs, string sql,
            IDataReader reader, IMapper mapper)
        {
            // 1) Gather the individual POCO factories
            var delegates = new List<Delegate>();
            int pos = 0;

            for (int i = 0; i < types.Length; i++)
            {
                var del = FindSplitPoint(
                    types[i],
                    i + 1 < types.Length ? types[i + 1] : null,
                    cs,     sql,
                    reader, ref pos,
                    mapper);

                delegates.Add(del);
            }

            // 2) Return a simple Func<reader, callback, TRet> that
            //    - invokes each factory via DynamicInvoke(reader)
            //    - collects the POCOs into an object[]
            //    - finally does callback.DynamicInvoke(pocos) and casts back to TRet
            return (r, callbackObj) =>
                   {
                       object[] pocos = new object[delegates.Count];

                       for (int i = 0; i < delegates.Count; i++)
                       {
                           // each delegate is Func<IDataReader,object,TPoco> or Func<IDataReader,TPoco>
                           // we only know it's some Delegate taking (IDataReader, maybe object) and returning the POCO.
                           // most PetaPoco factories ignore the object arg, so DynamicInvoke(reader) works.
                           pocos[i] = delegates[i].DynamicInvoke(r);
                       }

                       var result = ((Delegate)callbackObj).DynamicInvoke(pocos);
                       return (TRet)result;
                   };
        }

        internal static void FlushCaches()
        {
            MultiPocoFactories.Flush();
            AutoMappers.Flush();
        }

        public static Func<IDataReader, object, TRet> GetFactory<TRet>(
            Type[] types, string cs, string sql,
            IDataReader r, IMapper mapper)
        {
            var key = Tuple.Create(typeof(TRet), new ArrayKey<Type>(types), cs, sql, r.FieldCount);

            var result = (Func<IDataReader, object, TRet>)MultiPocoFactories.GetOrAdd(
                key, 
                () => CreateMultiPocoFactory<TRet>(types, cs, sql, r, mapper));

            return result;
        }
    }
}
#endif
