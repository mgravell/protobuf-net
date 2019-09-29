﻿using ProtoBuf.Internal;
using ProtoBuf.Meta;
using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace ProtoBuf
{
    [Flags]
    public enum SerializerFeatures
    {
        /// <summary>
        /// Base-128 variable-length encoding
        /// </summary>
        WireTypeVarint = WireType.Varint,

        /// <summary>
        /// Fixed-length 8-byte encoding
        /// </summary>
        WireTypeFixed64 = WireType.Fixed64,

        /// <summary>
        /// Length-variant-prefixed encoding
        /// </summary>
        WireTypeString = WireType.String,

        /// <summary>
        /// Indicates the start of a group
        /// </summary>
        WireTypeStartGroup = WireType.StartGroup,

        /// <summary>
        /// Indicates the end of a group
        /// </summary>
        WireTypeEndGroup = WireType.EndGroup,

        /// <summary>
        /// Fixed-length 4-byte encoding
        /// </summary>10
        WireTypeFixed32 = WireType.Fixed32,

        /// <summary>
        /// Denotes a varint that should be interpreted using
        /// zig-zag semantics (so -ve numbers aren't a significant overhead)
        /// </summary>
        WireTypeSignedVarint = WireTypeVarint | ZigZag,

        /// <summary>
        /// Indicates whether zig-zag encoding should be used
        /// </summary>
        ZigZag = 1 << 3,

        /// <summary>
        /// Scalars are simple types such as integers, not messages; when written as
        /// a root message, a field-one wrapper is added
        /// </summary>
        Scalar = 1 << 4,

        /// <summary>
        /// Indicates a type that is formally a message, but which is treated like a
        /// scalar (i.e. a field-one wrapper) at the root level; see: DateTime/TimeSpan
        /// </summary>
        Wrapped = 1 << 5,

        /// <summary>
        /// Indicates 
        /// </summary>
        Repeated = 1 << 6,

        /// <summary>
        /// Explicitly disables packed encoding; normally, packed encoding is
        /// used by default when appropriate
        /// </summary>
        PackedDisabled = 1 << 7,
    }

    internal static class SerializerFeaturesExtensions
    {
        public static bool IsRepeated(this SerializerFeatures features)
            => (features & SerializerFeatures.Repeated) != 0;

        public static bool IsScalar(this SerializerFeatures features)
            => (features & SerializerFeatures.Scalar) != 0;

        public static bool IsWrapped(this SerializerFeatures features)
            => (features & SerializerFeatures.Wrapped) != 0;

        public static bool IsPackedDisabled(this SerializerFeatures features)
            => (features & SerializerFeatures.PackedDisabled) != 0;

        public static WireType GetWireType(this SerializerFeatures features)
            => (WireType)((int)features & 15);
    }

    /// <summary>
    /// Abstract API capable of serializing/deserializing messages or values
    /// </summary>
    public interface ISerializer<T>
    {
        /// <summary>
        /// Deserialize an instance from the supplied writer
        /// </summary>
        T Read(ref ProtoReader.State state, T value);

        /// <summary>
        /// Serialize an instance to the supplied writer
        /// </summary>
        void Write(ref ProtoWriter.State state, T value);

        /// <summary>
        /// Indicates the default wire-type for this type
        /// </summary>
        WireType DefaultWireType { get; }
    }

    /// <summary>
    /// Indicates that the serializer processes scalar values (scalars are things like enums; the values are never merged)
    /// </summary>
    public interface IScalarSerializer<T> : ISerializer<T> { }

    /// <summary>
    /// Indicates that the serializer processes messages, but that at the root they should be hidden behind an extra layer of indirection
    /// </summary>
    internal interface IWrappedSerializer<T> : ISerializer<T> { }

    internal interface IListSerializer<T> : ISerializer<T> { }


    /// <summary>
    /// Abstract API capable of serializing/deserializing objects as part of a type hierarchy
    /// </summary>
    public interface ISubTypeSerializer<T> where T : class
    {
        /// <summary>
        /// Serialize an instance to the supplied writer
        /// </summary>
        void WriteSubType(ref ProtoWriter.State state, T value);

        /// <summary>
        /// Deserialize an instance from the supplied writer
        /// </summary>
        T ReadSubType(ref ProtoReader.State state, SubTypeState<T> value);
    }

    /// <summary>
    /// Represents the state of an inheritance deserialization operation
    /// </summary>
    public struct SubTypeState<T>
        where T : class
    {
        private readonly ISerializationContext _context;
        private readonly Func<ISerializationContext, object> _ctor;
        private object _value;
        private Action<T, ISerializationContext> _onBeforeDeserialize;

        /// <summary>
        /// Create a new value, using the provided concrete type if a new instance is required
        /// </summary>
        public static SubTypeState<T> Create<TValue>(ISerializationContext context, TValue value)
            where TValue : class, T
            => new SubTypeState<T>(context, TypeHelper<TValue>.Factory, value, null);

        private SubTypeState(ISerializationContext context, Func<ISerializationContext, object> ctor,
            object value, Action<T, ISerializationContext> onBeforeDeserialize)
        {
            _context = context;
            _ctor = ctor;
            _value = value;
            _onBeforeDeserialize = onBeforeDeserialize;
        }

        /// <summary>
        /// Gets or sets the current instance represented
        /// </summary>
        public T Value
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => (_value as T) ?? Cast();
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set => _value = value;
        }

        /// <summary>
        /// Ensures that the instance has a value
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void CreateIfNeeded() => _ = Value;

        internal object RawValue => _value;

        /// <summary>
        /// Indicates whether an instance currently exists
        /// </summary>
        public bool HasValue => _value is object;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private T Cast()
        {
            // pick the best available constructor; conside C : B : A, and we're currently deserializing
            // layer B at the point the object is first needed; the caller could have asked
            // for Deserialize<A>, in which case we'll choose B (because we're at that layer), but the
            // caller could have asked for Deserialize<C>, in which case we'll prefer C (because that's
            // what they asked for)
            var typed = ((_ctor as Func<ISerializationContext, T>) ?? TypeHelper<T>.Factory)(_context);

            if (_value != null) typed = Merge(_context, _value, typed);
            _onBeforeDeserialize?.Invoke(typed, _context);
            _value = typed;
            return typed;

            // this isn't especially efficient, but it should work
            static T Merge(ISerializationContext context, object value, T typed)
            {
                using var ms = new MemoryStream();
                // this <object> sneakily finds the correct base-type
                context.Model.Serialize<object>(ms, value, context.Context);
                ms.Position = 0;
                return context.Model.Deserialize<T>(ms, typed, context.Context);
            }
        }


        /// <summary>
        /// Parse the input as a sub-type of the instance
        /// </summary>
        public void ReadSubType<TSubType>(ref ProtoReader.State state, ISubTypeSerializer<TSubType> serializer = null) where TSubType : class, T
        {
            var tok = state.StartSubItem();
            _value = (serializer ?? TypeModel.GetSubTypeSerializer<TSubType>(_context.Model)).ReadSubType(ref state,
                new SubTypeState<TSubType>(_context, _ctor, _value, _onBeforeDeserialize));
            state.EndSubItem(tok);
        }

        /// <summary>
        /// Specifies a serialization callback to be used when the item is constructed; if the item already exists, the callback is executed immediately
        /// </summary>
        public void OnBeforeDeserialize(Action<T, ISerializationContext> callback)
        {
            if (callback != null)
            {
                if (_value is T obj) callback.Invoke(obj, _context);
                else if (_onBeforeDeserialize is object) ThrowHelper.ThrowInvalidOperationException("Only one pending " + nameof(OnBeforeDeserialize) + " callback is supported");
                else _onBeforeDeserialize = callback;
            }
        }
    }

    /// <summary>
    /// Abstract API capable of serializing/deserializing complex objects with inheritance
    /// </summary>
    public interface IFactory<T>
    {
        /// <summary>
        /// Create a new instance of the type
        /// </summary>
        T Create(ISerializationContext context);
    }
}
