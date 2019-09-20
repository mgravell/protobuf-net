﻿using System;
using System.Collections;
using System.Diagnostics;
using System.Reflection;
using ProtoBuf.Meta;

namespace ProtoBuf.Serializers
{
    internal sealed class ImmutableCollectionDecorator : ListDecorator
    {
        protected override bool RequireAdd { get { return false; } }

#pragma warning disable RCS1163, IDE0060 // Unused parameter.
        private static Type ResolveIReadOnlyCollection(Type declaredType, Type t)
#pragma warning restore RCS1163, IDE0060 // Unused parameter.
        {
            if (CheckIsIReadOnlyCollectionExactly(declaredType)) return declaredType;
            foreach (Type intImpl in declaredType.GetInterfaces())
            {
                if (CheckIsIReadOnlyCollectionExactly(intImpl)) return intImpl;
            }
            return null;
        }

        private static bool CheckIsIReadOnlyCollectionExactly(Type t)
        {
            if (t != null && t.IsGenericType && t.Name.StartsWith("IReadOnlyCollection`"))
            {
                Type[] typeArgs = t.GetGenericArguments();
                if (typeArgs.Length != 1 && typeArgs[0] != t) return false;

                return true;
            }
            return false;
        }

        internal static bool IdentifyImmutable(Type declaredType, out MethodInfo builderFactory, out PropertyInfo isEmpty, out PropertyInfo length, out MethodInfo add, out MethodInfo addRange, out MethodInfo finish)
        {
            builderFactory = add = addRange = finish = null;
            isEmpty = length = null;
            if (declaredType == null) return false;
            Type declaredTypeInfo = declaredType;

            // try to detect immutable collections; firstly, they are all generic, and all implement IReadOnlyCollection<T> for some T
            if (!declaredTypeInfo.IsGenericType) return false;
            Type[] typeArgs = declaredTypeInfo.GetGenericArguments(), effectiveType;
            switch (typeArgs.Length)
            {
                case 1:
                    effectiveType = typeArgs;
                    break; // fine
                case 2:
                    Type kvp = typeof(System.Collections.Generic.KeyValuePair<,>);
                    if (kvp == null) return false;
                    kvp = kvp.MakeGenericType(typeArgs);
                    effectiveType = new Type[] { kvp };
                    break;
                default:
                    return false; // no clue!
            }

            if (ResolveIReadOnlyCollection(declaredType, null) == null) return false; // no IReadOnlyCollection<T> found

            // and we want to use the builder API, so for generic Foo<T> or IFoo<T> we want to use Foo.CreateBuilder<T>
            string name = declaredType.Name;
            int i = name.IndexOf('`');
            if (i <= 0) return false;
#pragma warning disable IDE0057 // substring can be simplified
            name = declaredTypeInfo.IsInterface ? name.Substring(1, i - 1) : name.Substring(i);
#pragma warning restore IDE0057 // substring can be simplified

            Type outerType = TypeModel.ResolveKnownType(declaredType.Namespace + "." + name, declaredTypeInfo.Assembly);
            // I hate special-cases...
            if (outerType == null && name == "ImmutableSet")
            {
                outerType = TypeModel.ResolveKnownType(declaredType.Namespace + ".ImmutableHashSet", declaredTypeInfo.Assembly);
            }
            if (outerType == null) return false;

            foreach (MethodInfo method in outerType.GetMethods())
            {
                if (!method.IsStatic || method.Name != "CreateBuilder" || !method.IsGenericMethodDefinition || method.GetParameters().Length != 0
                    || method.GetGenericArguments().Length != typeArgs.Length)
                {
                    continue;
                }

                builderFactory = method.MakeGenericMethod(typeArgs);
                break;
            }
            Type voidType = typeof(void);
            if (builderFactory == null || builderFactory.ReturnType == null || builderFactory.ReturnType == voidType) return false;

            Type typeInfo = declaredType;
            isEmpty = Helpers.GetProperty(typeInfo, "IsDefaultOrEmpty", false)
                ?? Helpers.GetProperty(typeInfo, "IsEmpty", false); //struct based immutabletypes can have both a "default" and "empty" state

            if (isEmpty == null)
            {
                //Fallback to checking length if a "IsEmpty" property is not found
                length = (Helpers.GetProperty(typeInfo, "Length", false)
                    ?? Helpers.GetProperty(typeInfo, "Count", false))
                    ?? Helpers.GetProperty(ResolveIReadOnlyCollection(declaredType, effectiveType[0]), "Count", false);

                if (length == null) return false;
            }

            add = Helpers.GetInstanceMethod(builderFactory.ReturnType, "Add", effectiveType);
            if (add == null) return false;

            finish = Helpers.GetInstanceMethod(builderFactory.ReturnType, "ToImmutable", Type.EmptyTypes);
            if (finish == null || finish.ReturnType == null || finish.ReturnType == voidType) return false;

            if (!(finish.ReturnType == declaredType || declaredType.IsAssignableFrom(finish.ReturnType))) return false;

            addRange = Helpers.GetInstanceMethod(builderFactory.ReturnType, "AddRange", new Type[] { declaredType });
            if (addRange == null)
            {
                Type enumerable = typeof(System.Collections.Generic.IEnumerable<>);
                addRange = Helpers.GetInstanceMethod(builderFactory.ReturnType, "AddRange", new Type[] { enumerable.MakeGenericType(effectiveType) });
            }

            return true;
        }

        private readonly MethodInfo builderFactory, add, addRange, finish;
        private readonly PropertyInfo isEmpty, length;
        internal ImmutableCollectionDecorator(Type declaredType, Type concreteType, IRuntimeProtoSerializerNode tail, int fieldNumber, bool writePacked, WireType packedWireType, bool returnList, bool overwriteList, bool supportNull,
            MethodInfo builderFactory, PropertyInfo isEmpty, PropertyInfo length, MethodInfo add, MethodInfo addRange, MethodInfo finish)
            : base(declaredType, concreteType, tail, fieldNumber, writePacked, packedWireType, returnList, overwriteList, supportNull)
        {
            this.builderFactory = builderFactory;
            this.isEmpty = isEmpty;
            this.length = length;
            this.add = add;
            this.addRange = addRange;
            this.finish = finish;
        }

        public override object Read(ProtoReader source, ref ProtoReader.State state, object value)
        {
            object builderInstance = builderFactory.Invoke(null, null);
            int field = source.FieldNumber;
            object[] args = new object[1];
            if (AppendToCollection && value != null && (isEmpty != null ? !(bool)isEmpty.GetValue(value, null) : (int)length.GetValue(value, null) != 0))
            {
                if (addRange != null)
                {
                    args[0] = value;
                    addRange.Invoke(builderInstance, args);
                }
                else
                {
                    foreach (object item in (ICollection)value)
                    {
                        args[0] = item;
                        add.Invoke(builderInstance, args);
                    }
                }
            }

            if (packedWireType != WireType.None && source.WireType == WireType.String)
            {
                SubItemToken token = state.StartSubItem();
                while (ProtoReader.HasSubValue(packedWireType, source))
                {
                    args[0] = Tail.Read(source, ref state, null);
                    add.Invoke(builderInstance, args);
                }
                state.EndSubItem(token);
            }
            else
            {
                do
                {
                    args[0] = Tail.Read(source, ref state, null);
                    add.Invoke(builderInstance, args);
                } while (state.TryReadFieldHeader(field));
            }

            return finish.Invoke(builderInstance, null);
        }

        protected override void EmitRead(Compiler.CompilerContext ctx, Compiler.Local valueFrom)
        {
            using Compiler.Local oldList = AppendToCollection ? ctx.GetLocalWithValue(ExpectedType, valueFrom) : null;
            using Compiler.Local builder = new Compiler.Local(ctx, builderFactory.ReturnType);
            ctx.EmitCall(builderFactory);
            ctx.StoreValue(builder);

            if (AppendToCollection)
            {
                Compiler.CodeLabel done = ctx.DefineLabel();
                if (!ExpectedType.IsValueType)
                {
                    ctx.LoadValue(oldList);
                    ctx.BranchIfFalse(done, false); // old value null; nothing to add
                }

                ctx.LoadAddress(oldList, oldList.Type);
                if (isEmpty != null)
                {
                    ctx.EmitCall(Helpers.GetGetMethod(isEmpty, false, false));
                    ctx.BranchIfTrue(done, false); // old list is empty; nothing to add
                }
                else
                {
                    ctx.EmitCall(Helpers.GetGetMethod(length, false, false));
                    ctx.BranchIfFalse(done, false); // old list is empty; nothing to add
                }

                Type voidType = typeof(void);
                if (addRange != null)
                {
                    ctx.LoadValue(builder);
                    ctx.LoadValue(oldList);
                    ctx.EmitCall(addRange);
                    if (addRange.ReturnType != null && add.ReturnType != voidType) ctx.DiscardValue();
                }
                else
                {
                    // loop and call Add repeatedly
                    MethodInfo moveNext, current, getEnumerator = GetEnumeratorInfo(out moveNext, out current);
                    Debug.Assert(moveNext != null);
                    Debug.Assert(current != null);
                    Debug.Assert(getEnumerator != null);

                    Type enumeratorType = getEnumerator.ReturnType;
                    using Compiler.Local iter = new Compiler.Local(ctx, enumeratorType);
                    ctx.LoadAddress(oldList, ExpectedType);
                    ctx.EmitCall(getEnumerator);
                    ctx.StoreValue(iter);
                    using (ctx.Using(iter))
                    {
                        Compiler.CodeLabel body = ctx.DefineLabel(), next = ctx.DefineLabel();
                        ctx.Branch(next, false);

                        ctx.MarkLabel(body);
                        ctx.LoadAddress(builder, builder.Type);
                        ctx.LoadAddress(iter, enumeratorType);
                        ctx.EmitCall(current);
                        ctx.EmitCall(add);
                        if (add.ReturnType != null && add.ReturnType != voidType) ctx.DiscardValue();

                        ctx.MarkLabel(@next);
                        ctx.LoadAddress(iter, enumeratorType);
                        ctx.EmitCall(moveNext);
                        ctx.BranchIfTrue(body, false);
                    }
                }

                ctx.MarkLabel(done);
            }

            EmitReadList(ctx, builder, Tail, add, packedWireType, false);

            ctx.LoadAddress(builder, builder.Type);
            ctx.EmitCall(finish);
            if (ExpectedType != finish.ReturnType)
            {
                ctx.Cast(ExpectedType);
            }
        }
    }
}