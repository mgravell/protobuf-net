﻿using System;
using System.Diagnostics;
using System.Reflection;
using ProtoBuf.Internal;
using ProtoBuf.Meta;

namespace ProtoBuf.Serializers
{
    internal sealed class InheritanceCompiledSerializer<TBase, T> : CompiledSerializer, ISerializer<T>, ISubTypeSerializer<T>, IFactory<T>
        where TBase : class
        where T : class, TBase
    {
        private readonly Compiler.ProtoSerializer<T> subTypeSerializer;
        private readonly Compiler.ProtoSubTypeDeserializer<T> subTypeDeserializer;
        private readonly Func<ISerializationContext, T> factory;

        T ISerializer<T>.Read(ref ProtoReader.State state, T value)
            => state.ReadBaseType<TBase, T>(value);

        T IFactory<T>.Create(ISerializationContext context)
            => factory(context);

        public override object Read(ref ProtoReader.State state, object value)
            => state.ReadBaseType<TBase, T>(TypeHelper<T>.FromObject(value));

        void ISerializer<T>.Write(ref ProtoWriter.State state, T value)
            => state.WriteBaseType<TBase>(value);

        public override void Write(ref ProtoWriter.State state, object value)
            => state.WriteBaseType<TBase>(TypeHelper<T>.FromObject(value));

        void ISubTypeSerializer<T>.WriteSubType(ref ProtoWriter.State state, T value)
            => subTypeSerializer(ref state, value);

        T ISubTypeSerializer<T>.ReadSubType(ref ProtoReader.State state, SubTypeState<T> value)
            => subTypeDeserializer(ref state, value);

        public InheritanceCompiledSerializer(IProtoTypeSerializer head, RuntimeTypeModel model)
            : base(head)
        {
            try
            {
                subTypeSerializer = Compiler.CompilerContext.BuildSerializer<T>(model.Scope, head, model);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Unable to bind serializer: " + ex.Message, ex);
            }
            try
            {
                subTypeDeserializer = Compiler.CompilerContext.BuildSubTypeDeserializer<T>(model.Scope, head, model);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Unable to bind deserializer: " + ex.Message, ex);
            }
            factory = Compiler.CompilerContext.BuildFactory<T>(model.Scope, head, model);
        }
    }

    internal sealed class EnumCompiledSerializer<T> : SimpleCompiledSerializer<T>, IScalarSerializer<T>, IScalarSerializer<T?>
        where T : struct
    {
        public WireType DefaultWireType => WireType.Varint;

        public EnumCompiledSerializer(IProtoTypeSerializer head, RuntimeTypeModel model) : base(head, model) { }

        T? ISerializer<T?>.Read(ref ProtoReader.State state, T? value)
            => deserializer(ref state, default);

        void ISerializer<T?>.Write(ref ProtoWriter.State state, T? value)
            => serializer(ref state, value.Value);
    }
    internal class SimpleCompiledSerializer<T> : CompiledSerializer,
        ISerializer<T>, IFactory<T>
    {
        protected readonly Compiler.ProtoSerializer<T> serializer;
        protected readonly Compiler.ProtoDeserializer<T> deserializer;
        private readonly Func<ISerializationContext, T> factory;
        public SimpleCompiledSerializer(IProtoTypeSerializer head, RuntimeTypeModel model)
            : base(head)
        {
            try
            {
                serializer = Compiler.CompilerContext.BuildSerializer<T>(model.Scope, head, model);
            }
            catch(Exception ex)
            {
                throw new InvalidOperationException($"Unable to bind serializer: " + ex.Message, ex);
            }

            bool isScalar = this is IScalarSerializer<T>;
            try
            {
                deserializer = Compiler.CompilerContext.BuildDeserializer<T>(model.Scope, head, model, isScalar);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Unable to bind deserializer: " + ex.Message, ex);
            }
            factory = isScalar ? ctx => default : Compiler.CompilerContext.BuildFactory<T>(model.Scope, head, model);
        }

        T ISerializer<T>.Read(ref ProtoReader.State state, T value)
            => deserializer(ref state, value);

        public override object Read(ref ProtoReader.State state, object value)
            => deserializer(ref state, TypeHelper<T>.FromObject(value));

        void ISerializer<T>.Write(ref ProtoWriter.State state, T value)
            => serializer(ref state, value);

        public override void Write(ref ProtoWriter.State state, object value)
            => serializer(ref state, TypeHelper<T>.FromObject(value));

        T IFactory<T>.Create(ISerializationContext context)
            => factory(context);
    }
    internal abstract class CompiledSerializer : IProtoTypeSerializer
    {
        bool IProtoTypeSerializer.HasCallbacks(TypeModel.CallbackType callbackType)
        {
            return head.HasCallbacks(callbackType); // these routes only used when bits of the model not compiled
        }

        bool IProtoTypeSerializer.IsSubType => head.IsSubType;

        bool IProtoTypeSerializer.CanCreateInstance() => head.CanCreateInstance();

        object IProtoTypeSerializer.CreateInstance(ISerializationContext context) => head.CreateInstance(context);

        public void Callback(object value, TypeModel.CallbackType callbackType, SerializationContext context)
        {
            head.Callback(value, callbackType, context); // these routes only used when bits of the model not compiled
        }

        public static CompiledSerializer Wrap(IProtoTypeSerializer head, RuntimeTypeModel model)
        {
            if (!(head is CompiledSerializer result))
            {
                ConstructorInfo ctor;
                try
                {
                    if (head.IsSubType)
                    {
                        ctor = Helpers.GetConstructor(typeof(InheritanceCompiledSerializer<,>).MakeGenericType(head.BaseType, head.ExpectedType),
                            new Type[] { typeof(IProtoTypeSerializer), typeof(RuntimeTypeModel) }, true);
                    }
                    else if (head.ExpectedType.IsEnum)
                    {
                        ctor = Helpers.GetConstructor(typeof(EnumCompiledSerializer<>).MakeGenericType(head.BaseType),
                            new Type[] { typeof(IProtoTypeSerializer), typeof(RuntimeTypeModel) }, true);
                    }
                    else
                    {
                        ctor = Helpers.GetConstructor(typeof(SimpleCompiledSerializer<>).MakeGenericType(head.BaseType),
                            new Type[] { typeof(IProtoTypeSerializer), typeof(RuntimeTypeModel) }, true);
                    }
                } catch(Exception ex)
                {
                    throw new InvalidOperationException($"Unable to wrap {head.BaseType}/{head.ExpectedType}", ex);
                }
                try
                {
                    result = (CompiledSerializer)ctor.Invoke(new object[] { head, model });
                }
                catch (System.Reflection.TargetInvocationException tie)
                {
                    throw new InvalidOperationException($"Unable to wrap {head.BaseType.Name}/{head.ExpectedType.Name}: {tie.InnerException.Message}", tie.InnerException);
                }
                Debug.Assert(result.ExpectedType == head.ExpectedType);
            }
            return result;
        }

        protected readonly IProtoTypeSerializer head;
        Type IProtoTypeSerializer.BaseType => head.BaseType;
        protected CompiledSerializer(IProtoTypeSerializer head)
        {
            this.head = head;
        }

        bool IRuntimeProtoSerializerNode.RequiresOldValue => head.RequiresOldValue;

        bool IRuntimeProtoSerializerNode.ReturnsValue => head.ReturnsValue;

        public Type ExpectedType => head.ExpectedType;

        public abstract void Write(ref ProtoWriter.State state, object value);

        public abstract object Read(ref ProtoReader.State state, object value);

        void IRuntimeProtoSerializerNode.EmitWrite(Compiler.CompilerContext ctx, Compiler.Local valueFrom)
            => head.EmitWrite(ctx, valueFrom);

        void IProtoTypeSerializer.EmitWriteRoot(Compiler.CompilerContext ctx, Compiler.Local valueFrom)
            => head.EmitWriteRoot(ctx, valueFrom);

        void IRuntimeProtoSerializerNode.EmitRead(Compiler.CompilerContext ctx, Compiler.Local valueFrom)
            => head.EmitRead(ctx, valueFrom);

        void IProtoTypeSerializer.EmitReadRoot(Compiler.CompilerContext ctx, Compiler.Local valueFrom)
            => head.EmitReadRoot(ctx, valueFrom);

        bool IProtoTypeSerializer.HasInheritance => head.HasInheritance;

        void IProtoTypeSerializer.EmitCallback(Compiler.CompilerContext ctx, Compiler.Local valueFrom, TypeModel.CallbackType callbackType)
            => head.EmitCallback(ctx, valueFrom, callbackType);

        void IProtoTypeSerializer.EmitCreateInstance(Compiler.CompilerContext ctx, bool callNoteObject)
            => head.EmitCreateInstance(ctx, callNoteObject);

        bool IProtoTypeSerializer.ShouldEmitCreateInstance
            => head.ShouldEmitCreateInstance;
    }
}