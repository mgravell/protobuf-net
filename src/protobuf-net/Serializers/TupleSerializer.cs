﻿using System;
using System.Reflection;
using ProtoBuf.Meta;

namespace ProtoBuf.Serializers
{
    internal sealed class TupleSerializer : IProtoTypeSerializer
    {

        bool IProtoTypeSerializer.IsSubType => false;
        private readonly MemberInfo[] members;
        private readonly ConstructorInfo ctor;
        private readonly IRuntimeProtoSerializerNode[] tails;
        public TupleSerializer(RuntimeTypeModel model, ConstructorInfo ctor, MemberInfo[] members)
        {
            this.ctor = ctor ?? throw new ArgumentNullException(nameof(ctor));
            this.members = members ?? throw new ArgumentNullException(nameof(members));
            this.tails = new IRuntimeProtoSerializerNode[members.Length];

            ParameterInfo[] parameters = ctor.GetParameters();
            for (int i = 0; i < members.Length; i++)
            {
                Type finalType = parameters[i].ParameterType;

                Type itemType = null, defaultType = null;

                MetaType.ResolveListTypes(finalType, ref itemType, ref defaultType);
                Type tmp = itemType ?? finalType;

                bool asReference = false;
                int typeIndex = model.FindOrAddAuto(tmp, false, true, false);
                if (typeIndex >= 0)
                {
                    asReference = model[tmp].AsReferenceDefault;
                }
                IRuntimeProtoSerializerNode tail = ValueMember.TryGetCoreSerializer(model, DataFormat.Default, tmp, out WireType wireType, asReference, false, false, true), serializer;
                if (tail == null)
                {
                    throw new InvalidOperationException("No serializer defined for type: " + tmp.FullName);
                }

                tail = new TagDecorator(i + 1, wireType, false, tail);
                if (itemType == null)
                {
                    serializer = tail;
                }
                else
                {
                    if (finalType.IsArray)
                    {
                        serializer = new ArrayDecorator(tail, i + 1, false, wireType, finalType, false, false);
                    }
                    else
                    {
                        serializer = ListDecorator.Create(finalType, defaultType, tail, i + 1, false, wireType, true, false, false);
                    }
                }
                tails[i] = serializer;
            }
        }
        public bool HasCallbacks(Meta.TypeModel.CallbackType callbackType)
        {
            return false;
        }

        public void EmitCallback(Compiler.CompilerContext ctx, Compiler.Local valueFrom, Meta.TypeModel.CallbackType callbackType) { }
        public Type ExpectedType => ctor.DeclaringType;
        Type IProtoTypeSerializer.BaseType => ExpectedType;

        void IProtoTypeSerializer.Callback(object value, Meta.TypeModel.CallbackType callbackType, SerializationContext context) { }
        object IProtoTypeSerializer.CreateInstance(ISerializationContext source) { throw new NotSupportedException(); }
        private object GetValue(object obj, int index)
        {
            if (members[index] is PropertyInfo prop)
            {
                if (obj == null)
                    return prop.PropertyType.IsValueType ? Activator.CreateInstance(prop.PropertyType, nonPublic: true) : null;
                return prop.GetValue(obj, null);
            }
            else if (members[index] is FieldInfo field)
            {
                if (obj == null)
                    return field.FieldType.IsValueType ? Activator.CreateInstance(field.FieldType, nonPublic: true) : null;
                return field.GetValue(obj);
            }
            else
            {
                throw new InvalidOperationException();
            }
        }

        public object Read(ref ProtoReader.State state, object value)
        {
            object[] values = new object[members.Length];
            bool invokeCtor = false;
            if (value == null)
            {
                invokeCtor = true;
            }
            for (int i = 0; i < values.Length; i++)
                values[i] = GetValue(value, i);
            int field;
            while ((field = state.ReadFieldHeader()) > 0)
            {
                invokeCtor = true;
                if (field <= tails.Length)
                {
                    IRuntimeProtoSerializerNode tail = tails[field - 1];
                    values[field - 1] = tails[field - 1].Read(ref state, tail.RequiresOldValue ? values[field - 1] : null);
                }
                else
                {
                    state.SkipField();
                }
            }
            return invokeCtor ? ctor.Invoke(values) : value;
        }

        public void Write(ref ProtoWriter.State state, object value)
        {
            for (int i = 0; i < tails.Length; i++)
            {
                object val = GetValue(value, i);
                if (val != null) tails[i].Write(ref state, val);
            }
        }

        public bool RequiresOldValue => true;

        public bool ReturnsValue => false;

        bool IProtoTypeSerializer.CanCreateInstance() { return false; }

        private Type GetMemberType(int index)
        {
            Type result = Helpers.GetMemberType(members[index]);
            if (result == null) throw new InvalidOperationException();
            return result;
        }
        public void EmitWrite(Compiler.CompilerContext ctx, Compiler.Local valueFrom)
        {
            using Compiler.Local loc = ctx.GetLocalWithValue(ctor.DeclaringType, valueFrom);
            for (int i = 0; i < tails.Length; i++)
            {
                Type type = GetMemberType(i);
                ctx.LoadAddress(loc, ExpectedType);
                if (members[i] is FieldInfo)
                {
                    ctx.LoadValue((FieldInfo)members[i]);
                }
                else if (members[i] is PropertyInfo)
                {
                    ctx.LoadValue((PropertyInfo)members[i]);
                }
                ctx.WriteNullCheckedTail(type, tails[i], null);
            }
        }

        bool IProtoTypeSerializer.ShouldEmitCreateInstance => false;
        void IProtoTypeSerializer.EmitCreateInstance(Compiler.CompilerContext ctx, bool callNoteObject) { throw new NotSupportedException(); }

        void IProtoTypeSerializer.EmitReadRoot(Compiler.CompilerContext ctx, Compiler.Local valueFrom)
            => EmitRead(ctx, valueFrom);

        void IProtoTypeSerializer.EmitWriteRoot(Compiler.CompilerContext ctx, Compiler.Local valueFrom)
            => EmitWrite(ctx, valueFrom);

        bool IProtoTypeSerializer.HasInheritance => false;

        public void EmitRead(Compiler.CompilerContext ctx, Compiler.Local incoming)
        {
            using Compiler.Local objValue = ctx.GetLocalWithValue(ExpectedType, incoming);
            Compiler.Local[] locals = new Compiler.Local[members.Length];
            try
            {
                for (int i = 0; i < locals.Length; i++)
                {
                    Type type = GetMemberType(i);
                    bool store = true;
                    locals[i] = new Compiler.Local(ctx, type);
                    if (!ExpectedType.IsValueType)
                    {
                        // value-types always read the old value
                        if (type.IsValueType)
                        {
                            switch (Helpers.GetTypeCode(type))
                            {
                                case ProtoTypeCode.Boolean:
                                case ProtoTypeCode.Byte:
                                case ProtoTypeCode.Int16:
                                case ProtoTypeCode.Int32:
                                case ProtoTypeCode.SByte:
                                case ProtoTypeCode.UInt16:
                                case ProtoTypeCode.UInt32:
                                    ctx.LoadValue(0);
                                    break;
                                case ProtoTypeCode.Int64:
                                case ProtoTypeCode.UInt64:
                                    ctx.LoadValue(0L);
                                    break;
                                case ProtoTypeCode.Single:
                                    ctx.LoadValue(0.0F);
                                    break;
                                case ProtoTypeCode.Double:
                                    ctx.LoadValue(0.0D);
                                    break;
                                case ProtoTypeCode.Decimal:
                                    ctx.LoadValue(0M);
                                    break;
                                case ProtoTypeCode.Guid:
                                    ctx.LoadValue(Guid.Empty);
                                    break;
                                default:
                                    ctx.LoadAddress(locals[i], type);
                                    ctx.EmitCtor(type);
                                    store = false;
                                    break;
                            }
                        }
                        else
                        {
                            ctx.LoadNullRef();
                        }
                        if (store)
                        {
                            ctx.StoreValue(locals[i]);
                        }
                    }
                }

                Compiler.CodeLabel skipOld = ExpectedType.IsValueType
                                                    ? new Compiler.CodeLabel()
                                                    : ctx.DefineLabel();
                if (!ExpectedType.IsValueType)
                {
                    ctx.LoadAddress(objValue, ExpectedType);
                    ctx.BranchIfFalse(skipOld, false);
                }
                for (int i = 0; i < members.Length; i++)
                {
                    ctx.LoadAddress(objValue, ExpectedType);
                    if (members[i] is FieldInfo)
                    {
                        ctx.LoadValue((FieldInfo)members[i]);
                    }
                    else if (members[i] is PropertyInfo)
                    {
                        ctx.LoadValue((PropertyInfo)members[i]);
                    }
                    ctx.StoreValue(locals[i]);
                }

                if (!ExpectedType.IsValueType) ctx.MarkLabel(skipOld);

                using (Compiler.Local fieldNumber = new Compiler.Local(ctx, typeof(int)))
                {
                    Compiler.CodeLabel @continue = ctx.DefineLabel(),
                                       processField = ctx.DefineLabel(),
                                       notRecognised = ctx.DefineLabel();
                    ctx.Branch(@continue, false);

                    Compiler.CodeLabel[] handlers = new Compiler.CodeLabel[members.Length];
                    for (int i = 0; i < members.Length; i++)
                    {
                        handlers[i] = ctx.DefineLabel();
                    }

                    ctx.MarkLabel(processField);

                    ctx.LoadValue(fieldNumber);
                    ctx.LoadValue(1);
                    ctx.Subtract(); // jump-table is zero-based
                    ctx.Switch(handlers);

                    // and the default:
                    ctx.Branch(notRecognised, false);
                    for (int i = 0; i < handlers.Length; i++)
                    {
                        ctx.MarkLabel(handlers[i]);
                        IRuntimeProtoSerializerNode tail = tails[i];
                        Compiler.Local oldValIfNeeded = tail.RequiresOldValue ? locals[i] : null;
                        ctx.ReadNullCheckedTail(locals[i].Type, tail, oldValIfNeeded);
                        if (tail.ReturnsValue)
                        {
                            if (locals[i].Type.IsValueType)
                            {
                                ctx.StoreValue(locals[i]);
                            }
                            else
                            {
                                Compiler.CodeLabel hasValue = ctx.DefineLabel(), allDone = ctx.DefineLabel();

                                ctx.CopyValue();
                                ctx.BranchIfTrue(hasValue, true); // interpret null as "don't assign"
                                ctx.DiscardValue();
                                ctx.Branch(allDone, true);
                                ctx.MarkLabel(hasValue);
                                ctx.StoreValue(locals[i]);
                                ctx.MarkLabel(allDone);
                            }
                        }
                        ctx.Branch(@continue, false);
                    }

                    ctx.MarkLabel(notRecognised);
                    ctx.LoadState();
                    ctx.EmitCall(typeof(ProtoReader.State).GetMethod(nameof(ProtoReader.State.SkipField), Type.EmptyTypes));

                    ctx.MarkLabel(@continue);
                    ctx.EmitStateBasedRead(nameof(ProtoReader.State.ReadFieldHeader), typeof(int));
                    ctx.CopyValue();
                    ctx.StoreValue(fieldNumber);
                    ctx.LoadValue(0);
                    ctx.BranchIfGreater(processField, false);
                }
                for (int i = 0; i < locals.Length; i++)
                {
                    ctx.LoadValue(locals[i]);
                }

                ctx.EmitCtor(ctor);
                ctx.StoreValue(objValue);
            }
            finally
            {
                for (int i = 0; i < locals.Length; i++)
                {
                    if (locals[i] != null)
                        locals[i].Dispose(); // release for re-use
                }
            }
        }
    }
}