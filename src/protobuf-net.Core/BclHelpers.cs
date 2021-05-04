using ProtoBuf.Internal;
using ProtoBuf.Meta;
using ProtoBuf.Serializers;
using ProtoBuf.WellKnownTypes;
using System;
using System.Buffers;
using System.Buffers.Text;
using System.Runtime.CompilerServices;
using System.Text;
using static ProtoBuf.Internal.PrimaryTypeProvider;

namespace ProtoBuf
{
    internal enum TimeSpanScale
    {
        Days = 0,
        Hours = 1,
        Minutes = 2,
        Seconds = 3,
        Milliseconds = 4,
        Ticks = 5,

        MinMax = 15
    }

    /// <summary>
    /// Provides support for common .NET types that do not have a direct representation
    /// in protobuf, using the definitions from bcl.proto
    /// </summary>
    public static class BclHelpers
    {
        /// <summary>
        /// Creates a new instance of the specified type, bypassing the constructor.
        /// </summary>
        /// <param name="type">The type to create</param>
        /// <returns>The new instance</returns>
        [MethodImpl(ProtoReader.HotPath)]
        public static object GetUninitializedObject(Type type)
        {
            return System.Runtime.Serialization.FormatterServices.GetUninitializedObject(type);
        }

        internal static readonly DateTime[] EpochOrigin = {
            new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified),
            new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Local)
        };

        /// <summary>
        /// Writes a TimeSpan to a protobuf stream using protobuf-net's own representation, bcl.TimeSpan
        /// </summary>
        [MethodImpl(ProtoReader.HotPath)]
        public static void WriteTimeSpan(TimeSpan timeSpan, ProtoWriter dest)
        {
            var state = dest.DefaultState();
            WriteTimeSpanImpl(ref state, timeSpan, DateTimeKind.Unspecified);
        }

        /// <summary>
        /// Writes a TimeSpan to a protobuf stream using protobuf-net's own representation, bcl.TimeSpan
        /// </summary>
        [MethodImpl(ProtoReader.HotPath)]
        public static void WriteTimeSpan(ref ProtoWriter.State state, TimeSpan value)
        {
            WriteTimeSpanImpl(ref state, value, DateTimeKind.Unspecified);
        }

        [MethodImpl(ProtoReader.HotPath)]
        private static void WriteTimeSpanImpl(ref ProtoWriter.State state, TimeSpan timeSpan, DateTimeKind kind)
        {
            switch (state.WireType)
            {
                case WireType.String:
                case WireType.StartGroup:
                    var scaled = new ScaledTicks(timeSpan, kind);
                    state.WriteMessage<ScaledTicks>(SerializerFeatures.OptionSkipRecursionCheck, scaled, SerializerCache<PrimaryTypeProvider>.InstanceField);
                    break;
                case WireType.Fixed64:
                    state.WriteInt64(timeSpan.Ticks);
                    break;
                default:
                    ThrowHelper.ThrowProtoException("Unexpected wire-type: " + state.WireType.ToString());
                    break;
            }
        }

        /// <summary>
        /// Parses a TimeSpan from a protobuf stream using protobuf-net's own representation, bcl.TimeSpan
        /// </summary>
        [MethodImpl(ProtoReader.HotPath)]
        public static TimeSpan ReadTimeSpan(ProtoReader source)
        {
            ProtoReader.State state = source.DefaultState();
            return ReadTimeSpan(ref state);
        }

        /// <summary>
        /// Parses a TimeSpan from a protobuf stream using protobuf-net's own representation, bcl.TimeSpan
        /// </summary>
        [MethodImpl(ProtoReader.HotPath)]
        public static TimeSpan ReadTimeSpan(ref ProtoReader.State state)
        {
            switch (state.WireType)
            {
                case WireType.String:
                case WireType.StartGroup:
                    var scaled = state.ReadMessage<ScaledTicks>(default, default, serializer: SerializerCache<PrimaryTypeProvider>.InstanceField);
                    return scaled.ToTimeSpan();
                case WireType.Fixed64:
                    long ticks = state.ReadInt64();
                    return ticks switch
                    {
                        long.MinValue => TimeSpan.MinValue,
                        long.MaxValue => TimeSpan.MaxValue,
                        _ => TimeSpan.FromTicks(ticks),
                    };
                default:
                    ThrowHelper.ThrowProtoException($"Unexpected wire-type: {state.WireType}");
                    return default;
            }
        }

        /// <summary>
        /// Parses a TimeSpan from a protobuf stream using the standardized format, google.protobuf.Duration
        /// </summary>
        [MethodImpl(ProtoReader.HotPath)]
        public static TimeSpan ReadDuration(ProtoReader source)
        {
            var state = source.DefaultState();
            return ReadDuration(ref state);
        }

        /// <summary>
        /// Parses a TimeSpan from a protobuf stream using the standardized format, google.protobuf.Duration
        /// </summary>
        [MethodImpl(ProtoReader.HotPath)]
        public static TimeSpan ReadDuration(ref ProtoReader.State state)
            => state.ReadMessage<Duration>(default, default, serializer: SerializerCache<PrimaryTypeProvider>.InstanceField);

        /// <summary>
        /// Writes a TimeSpan to a protobuf stream using the standardized format, google.protobuf.Duration
        /// </summary>
        [MethodImpl(ProtoReader.HotPath)]
        public static void WriteDuration(TimeSpan value, ProtoWriter dest)
        {
            var state = dest.DefaultState();
            WriteDuration(ref state, value);
        }

        /// <summary>
        /// Writes a TimeSpan to a protobuf stream using the standardized format, google.protobuf.Duration
        /// </summary>
        [MethodImpl(ProtoReader.HotPath)]
        public static void WriteDuration(ref ProtoWriter.State state, TimeSpan value)
            => state.WriteMessage<Duration>(SerializerFeatures.OptionSkipRecursionCheck, value, SerializerCache<PrimaryTypeProvider>.InstanceField);

        /// <summary>
        /// Parses a DateTime from a protobuf stream using the standardized format, google.protobuf.Timestamp
        /// </summary>
        [MethodImpl(ProtoReader.HotPath)]
        public static DateTime ReadTimestamp(ProtoReader source)
        {
            ProtoReader.State state = source.DefaultState();
            return ReadTimestamp(ref state);
        }

        /// <summary>
        /// Parses a DateTime from a protobuf stream using the standardized format, google.protobuf.Timestamp
        /// </summary>
        [MethodImpl(ProtoReader.HotPath)]
        public static DateTime ReadTimestamp(ref ProtoReader.State state)
        {
            // note: DateTime is only defined for just over 0000 to just below 10000;
            // TimeSpan has a range of +/- 10,675,199 days === 29k years;
            // so we can just use epoch time delta
            return state.ReadMessage<Timestamp>(default, default, serializer: SerializerCache<PrimaryTypeProvider>.InstanceField);
        }

        /// <summary>
        /// Writes a DateTime to a protobuf stream using the standardized format, google.protobuf.Timestamp
        /// </summary>
        [MethodImpl(ProtoReader.HotPath)]
        public static void WriteTimestamp(DateTime value, ProtoWriter dest)
        {
            var state = dest.DefaultState();
            WriteTimestamp(ref state, value);
        }

        /// <summary>
        /// Writes a DateTime to a protobuf stream using the standardized format, google.protobuf.Timestamp
        /// </summary>
        [MethodImpl(ProtoReader.HotPath)]
        public static void WriteTimestamp(ref ProtoWriter.State state, DateTime value)
            => state.WriteMessage<Timestamp>(SerializerFeatures.OptionSkipRecursionCheck, value, SerializerCache<PrimaryTypeProvider>.InstanceField);

        /// <summary>
        /// Parses a DateTime from a protobuf stream
        /// </summary>
        [MethodImpl(ProtoReader.HotPath)]
        public static DateTime ReadDateTime(ProtoReader source)
        {
            ProtoReader.State state = source.DefaultState();
            return ReadDateTime(ref state);
        }

        /// <summary>
        /// Parses a DateTime from a protobuf stream
        /// </summary>
        [MethodImpl(ProtoReader.HotPath)]
        public static DateTime ReadDateTime(ref ProtoReader.State state)
        {
            switch (state.WireType)
            {
                case WireType.String:
                case WireType.StartGroup:
                    var scaled = state.ReadMessage<ScaledTicks>(default, default, serializer: SerializerCache<PrimaryTypeProvider>.InstanceField);
                    return scaled.ToDateTime();
                case WireType.Fixed64:
                    long ticks = state.ReadInt64();
                    return ticks switch
                    {
                        long.MinValue => DateTime.MinValue,
                        long.MaxValue => DateTime.MaxValue,
                        _ => EpochOrigin[(int)DateTimeKind.Unspecified].AddTicks(ticks),
                    };
                default:
                    ThrowHelper.ThrowProtoException($"Unexpected wire-type: {state.WireType}");
                    return default;
            }
        }

        /// <summary>
        /// Writes a DateTime to a protobuf stream, excluding the <c>Kind</c>
        /// </summary>
        [MethodImpl(ProtoReader.HotPath)]
        public static void WriteDateTime(DateTime value, ProtoWriter dest)
        {
            ProtoWriter.State state = dest.DefaultState();
            WriteDateTimeImpl(ref state, value, false);
        }

        /// <summary>
        /// Writes a DateTime to a protobuf stream, excluding the <c>Kind</c>
        /// </summary>
        [MethodImpl(ProtoReader.HotPath)]
        public static void WriteDateTime(ref ProtoWriter.State state, DateTime value)
        {
            WriteDateTimeImpl(ref state, value, false);
        }

        /// <summary>
        /// Writes a DateTime to a protobuf stream, including the <c>Kind</c>
        /// </summary>
        [MethodImpl(ProtoReader.HotPath)]
        public static void WriteDateTimeWithKind(DateTime value, ProtoWriter dest)
        {
            ProtoWriter.State state = dest.DefaultState();
            WriteDateTimeImpl(ref state, value, true);
        }

        /// <summary>
        /// Writes a DateTime to a protobuf stream, including the <c>Kind</c>
        /// </summary>
        [MethodImpl(ProtoReader.HotPath)]
        public static void WriteDateTimeWithKind(ref ProtoWriter.State state, DateTime value)
        {
            WriteDateTimeImpl(ref state, value, true);
        }

        [MethodImpl(ProtoReader.HotPath)]
        private static void WriteDateTimeImpl(ref ProtoWriter.State state, DateTime value, bool includeKind)
        {
            TimeSpan delta = value - EpochOrigin[0];
            WriteTimeSpanImpl(ref state, delta, includeKind ? value.Kind : DateTimeKind.Unspecified);
        }

        /// <summary>
        /// Parses a decimal from a protobuf stream
        /// </summary>
        [MethodImpl(ProtoReader.HotPath)]
        public static decimal ReadDecimal(ProtoReader reader)
        {
            ProtoReader.State state = reader.DefaultState();
            return ReadDecimal(ref state);
        }

        /// <summary>
        /// Parses a decimal from a protobuf stream
        /// </summary>
        [MethodImpl(ProtoReader.HotPath)]
        public static decimal ReadDecimal(ref ProtoReader.State state)
            => state.ReadMessage<decimal>(default, default, serializer: SerializerCache<PrimaryTypeProvider>.InstanceField);

        /// <summary>
        /// Parses a decimal from a protobuf stream
        /// </summary>
        [MethodImpl(ProtoReader.HotPath)]
        public static unsafe decimal ReadDecimalString(ref ProtoReader.State state)
        {
            var ptr = stackalloc byte[MAX_DECIMAL_BYTES];
            var available = state.ReadBytes(new Span<byte>(ptr, MAX_DECIMAL_BYTES));
            if (!(Utf8Parser.TryParse(available, out decimal value, out int bytesConsumed) // default acts like 'G'/'E' - accomodating
                && bytesConsumed == available.Length))
                ThrowHelper.ThrowInvalidOperationException($"Unable to parse decimal: '{Encoding.UTF8.GetString(ptr, available.Length)}'");
            return value;
        }

        /// <summary>
        /// Writes a decimal to a protobuf stream
        /// </summary>
        [MethodImpl(ProtoReader.HotPath)]
        public static void WriteDecimal(decimal value, ProtoWriter writer)
        {
            ProtoWriter.State state = writer.DefaultState();
            WriteDecimal(ref state, value);
        }

        /// <summary>
        /// Writes a decimal to a protobuf stream
        /// </summary>
        [MethodImpl(ProtoReader.HotPath)]
        public static void WriteDecimal(ref ProtoWriter.State state, decimal value)
            => state.WriteMessage<decimal>(SerializerFeatures.OptionSkipRecursionCheck, value, SerializerCache<PrimaryTypeProvider>.InstanceField);

        /// <summary>
        /// Writes a decimal to a protobuf stream
        /// </summary>
        [MethodImpl(ProtoReader.HotPath)]
        public static void WriteDecimalString(ref ProtoWriter.State state, decimal value)
        {
            var arr = ArrayPool<byte>.Shared.Rent(MAX_DECIMAL_BYTES);
            try
            {
                if (!Utf8Formatter.TryFormat(value, arr, out int bytesWritten)) // format 'G' is implicit/default
                    ThrowHelper.ThrowInvalidOperationException($"Unable to format decimal: '{value}'");
                state.WriteBytes(new ReadOnlyMemory<byte>(arr, 0, bytesWritten));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(arr);
            }
        }

        private const int MAX_DECIMAL_BYTES = 32; // CoreLib uses 31; we'll round up (cheaper to wipe)

        /// <summary>
        /// Writes a Guid to a protobuf stream
        /// </summary>
        [MethodImpl(ProtoReader.HotPath)]
        public static void WriteGuid(Guid value, ProtoWriter dest)
        {
            var state = dest.DefaultState();
            WriteGuid(ref state, value);
        }

        /// <summary>
        /// Writes a Guid to a protobuf stream
        /// </summary>
        [MethodImpl(ProtoReader.HotPath)]
        public static void WriteGuid(ref ProtoWriter.State state, Guid value)
            => state.WriteMessage<Guid>(SerializerFeatures.OptionSkipRecursionCheck, value, SerializerCache<PrimaryTypeProvider>.InstanceField);

        /// <summary>
        /// Writes a Guid to a protobuf stream
        /// </summary>
        [MethodImpl(ProtoReader.HotPath)]
        public static void WriteGuidBytes(ref ProtoWriter.State state, Guid value)
            => GuidHelper.Write(ref state, in value, asBytes: true);

        /// <summary>
        /// Writes a Guid to a protobuf stream
        /// </summary>
        [MethodImpl(ProtoReader.HotPath)]
        public static void WriteGuidString(ref ProtoWriter.State state, Guid value)
            => GuidHelper.Write(ref state, in value, asBytes: false);

        /// <summary>
        /// Parses a Guid from a protobuf stream
        /// </summary>
        [MethodImpl(ProtoReader.HotPath)]
        public static Guid ReadGuid(ProtoReader source)
        {
            ProtoReader.State state = source.DefaultState();
            return ReadGuid(ref state);
        }

        /// <summary>
        /// Parses a Guid from a protobuf stream
        /// </summary>
        [MethodImpl(ProtoReader.HotPath)]
        public static Guid ReadGuid(ref ProtoReader.State state)
            => state.ReadMessage<Guid>(default, default, serializer: SerializerCache<PrimaryTypeProvider>.InstanceField);

        /// <summary>
        /// Parses a Guid from a protobuf stream
        /// </summary>
        [MethodImpl(ProtoReader.HotPath)]
        public static Guid ReadGuidBytes(ref ProtoReader.State state)
            => GuidHelper.Read(ref state); // note that this is forgiving and handles 16/32/36 formats

        /// <summary>
        /// Parses a Guid from a protobuf stream
        /// </summary>
        [MethodImpl(ProtoReader.HotPath)]
        public static Guid ReadGuidString(ref ProtoReader.State state)
            => GuidHelper.Read(ref state); // note that this is forgiving and handles 16/32/36 formats

#if FEAT_DYNAMIC_REF
        private const int
            FieldExistingObjectKey = 1,
            FieldNewObjectKey = 2,
            FieldExistingTypeKey = 3,
            FieldNewTypeKey = 4,
            FieldTypeName = 8,
            FieldObject = 10;

        /// <summary>
        /// Optional behaviours that introduce .NET-specific functionality
        /// </summary>
        [Flags]
        public enum NetObjectOptions : byte
        {
            /// <summary>
            /// No special behaviour
            /// </summary>
            None = 0,
            /// <summary>
            /// Enables full object-tracking/full-graph support.
            /// </summary>
            AsReference = 1,
            /// <summary>
            /// Embeds the type information into the stream, allowing usage with types not known in advance.
            /// </summary>
            DynamicType = 2,
            /// <summary>
            /// If false, the constructor for the type is bypassed during deserialization, meaning any field initializers
            /// or other initialization code is skipped.
            /// </summary>
            UseConstructor = 4,
            /// <summary>
            /// Should the object index be reserved, rather than creating an object promptly
            /// </summary>
            LateSet = 8
        }

        /// <summary>
        /// Reads an *implementation specific* bundled .NET object, including (as options) type-metadata, identity/re-use, etc.
        /// </summary>
        public static object ReadNetObject(object value, ProtoReader source, Type type, NetObjectOptions options)
        {
            ProtoReader.State state = source.DefaultState();
            return ReadNetObject(ref state, value, type, options);
        }

        /// <summary>
        /// Reads an *implementation specific* bundled .NET object, including (as options) type-metadata, identity/re-use, etc.
        /// </summary>
        public static object ReadNetObject(ref ProtoReader.State state, object value, Type type, NetObjectOptions options)
        {
            // var source = state.GetReader();
            SubItemToken token = state.StartSubItem();
            int fieldNumber;
            int newObjectKey = -1, newTypeKey = -1, tmp;
            while ((fieldNumber = state.ReadFieldHeader()) > 0)
            {
                switch (fieldNumber)
                {
                    case FieldExistingObjectKey:
                        tmp = state.ReadInt32();
                        value = state.GetKeyedObject(tmp);
                        break;
                    case FieldNewObjectKey:
                        newObjectKey = state.ReadInt32();
                        break;
                    case FieldExistingTypeKey:
                        tmp = state.ReadInt32();
                        type = (Type)state.GetKeyedObject(tmp);
                        break;
                    case FieldNewTypeKey:
                        newTypeKey = state.ReadInt32();
                        break;
                    case FieldTypeName:
                        string typeName = state.ReadString();
                        type = state.DeserializeType(typeName);
                        if (type is null)
                        {
                            ThrowHelper.ThrowProtoException("Unable to resolve type: " + typeName + " (you can use the TypeModel.DynamicTypeFormatting event to provide a custom mapping)");
                        }
                        if (type == typeof(string))
                        { }
                        else
                        {
                            var model = state.Model;
                            var known = model is object && model.IsDefined(type);
                            if (!known)
                                ThrowHelper.ThrowInvalidOperationException("Dynamic type is not a contract-type: " + type.Name);
                        }
                        break;
                    case FieldObject:
                        bool isString = type == typeof(string);
                        bool wasNull = value is null;
                        bool lateSet = wasNull && (isString || ((options & NetObjectOptions.LateSet) != 0));

                        if (newObjectKey >= 0 && !lateSet)
                        {
                            if (value is null)
                            {
                                state.TrapNextObject(newObjectKey);
                            }
                            else
                            {
                                state.SetKeyedObject(newObjectKey, value);
                            }
                            if (newTypeKey >= 0) state.SetKeyedObject(newTypeKey, type);
                        }
                        object oldValue = value;
                        if (isString)
                        {
                            value = state.ReadString();
                        }
                        else
                        {
                            value = state.ReadTypedObject(oldValue, type);
                        }

                        if (newObjectKey >= 0)
                        {
                            if (wasNull && !lateSet)
                            { // this both ensures (via exception) that it *was* set, and makes sure we don't shout
                                // about changed references
                                oldValue = state.GetKeyedObject(newObjectKey);
                            }
                            if (lateSet)
                            {
                                state.SetKeyedObject(newObjectKey, value);
                                if (newTypeKey >= 0) state.SetKeyedObject(newTypeKey, type);
                            }
                        }
                        if (newObjectKey >= 0 && !lateSet && !ReferenceEquals(oldValue, value))
                        {
                            ThrowHelper.ThrowProtoException("A reference-tracked object changed reference during deserialization");
                        }
                        if (newObjectKey < 0 && newTypeKey >= 0)
                        {  // have a new type, but not a new object
                            state.SetKeyedObject(newTypeKey, type);
                        }
                        break;
                    default:
                        state.SkipField();
                        break;
                }
            }
            if (newObjectKey >= 0 && (options & NetObjectOptions.AsReference) == 0)
            {
                ThrowHelper.ThrowProtoException("Object key in input stream, but reference-tracking was not expected");
            }
            state.EndSubItem(token);

            return value;
        }

        /// <summary>
        /// Writes an *implementation specific* bundled .NET object, including (as options) type-metadata, identity/re-use, etc.
        /// </summary>
        public static void WriteNetObject(object value, ProtoWriter dest, NetObjectOptions options)
        {
            ProtoWriter.State state = dest.DefaultState();
            WriteNetObject(ref state, value, options);
        }

        /// <summary>
        /// Writes an *implementation specific* bundled .NET object, including (as options) type-metadata, identity/re-use, etc.
        /// </summary>
        public static void WriteNetObject(ref ProtoWriter.State state, object value, NetObjectOptions options)
        {
            bool dynamicType = (options & NetObjectOptions.DynamicType) != 0,
                 asReference = (options & NetObjectOptions.AsReference) != 0;
            WireType wireType = state.WireType;
#pragma warning disable CS0618
            if (wireType != WireType.StartGroup)
                state.GetWriter().AssertTrackedObjects(); // net-objects - sorry, but they may not play well on newer transports
            SubItemToken token = state.StartSubItem(null);
            bool writeObject = true;
            if (asReference)
            {
                int objectKey = state.AddObjectKey(value, out bool existing);
                state.WriteFieldHeader(existing ? FieldExistingObjectKey : FieldNewObjectKey, WireType.Varint);
                state.WriteInt32(objectKey);
                if (existing)
                {
                    writeObject = false;
                }
            }

            if (writeObject)
            {
                Type type = value.GetType();
                if (dynamicType)
                {
                    if (!(value is string))
                    {
                        var model = state.Model;
                        var known = model is object && model.IsDefined(type);
                        if (!known)
                            ThrowHelper.ThrowInvalidOperationException("Dynamic type is not a contract-type: " + type.Name);
                    }
                    int typeKey = state.AddObjectKey(type, out bool existing);
                    state.WriteFieldHeader(existing ? FieldExistingTypeKey : FieldNewTypeKey, WireType.Varint);
                    state.WriteInt32(typeKey);
                    if (!existing)
                    {
                        state.WriteFieldHeader(FieldTypeName, WireType.String);
                        state.WriteType(type);
                    }
                }
                state.WriteFieldHeader(FieldObject, wireType);
                if (value is string s)
                {
                    state.WriteString(s);
                }
                else
                {
                    state.WriteObject(value, type);
                }
            }
            state.EndSubItem(token);
#pragma warning restore CS0618 // net-objects - sorry, but they may not play well on newer transports
        }
#endif
    }
}