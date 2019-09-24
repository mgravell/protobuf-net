﻿using ProtoBuf.Internal;
using ProtoBuf.WellKnownTypes;
using System;
using System.Runtime.CompilerServices;

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
                    state.WriteSubItem<ScaledTicks>(scaled, WellKnownSerializer.Instance);
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
            long ticks = ReadTimeSpanTicks(ref state, out DateTimeKind _);
            if (ticks == long.MinValue) return TimeSpan.MinValue;
            if (ticks == long.MaxValue) return TimeSpan.MaxValue;
            return TimeSpan.FromTicks(ticks);
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
            => state.ReadSubItem<Duration>(serializer: WellKnownSerializer.Instance);

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
            => state.WriteSubItem<Duration>(value, WellKnownSerializer.Instance);

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
            return state.ReadSubItem<Timestamp>(serializer: WellKnownSerializer.Instance);
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
            => state.WriteSubItem<Timestamp>(value, WellKnownSerializer.Instance);

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
            long ticks = ReadTimeSpanTicks(ref state, out DateTimeKind kind);
            if (ticks == long.MinValue) return DateTime.MinValue;
            if (ticks == long.MaxValue) return DateTime.MaxValue;
            return EpochOrigin[(int)kind].AddTicks(ticks);
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
            TimeSpan delta;
            switch (state.WireType)
            {
                case WireType.StartGroup:
                case WireType.String:
                    if (value == DateTime.MaxValue)
                    {
                        delta = TimeSpan.MaxValue;
                        includeKind = false;
                    }
                    else if (value == DateTime.MinValue)
                    {
                        delta = TimeSpan.MinValue;
                        includeKind = false;
                    }
                    else
                    {
                        delta = value - EpochOrigin[0];
                    }
                    break;
                default:
                    delta = value - EpochOrigin[0];
                    break;
            }
            WriteTimeSpanImpl(ref state, delta, includeKind ? value.Kind : DateTimeKind.Unspecified);
        }

        private static long ReadTimeSpanTicks(ref ProtoReader.State state, out DateTimeKind kind)
        {
            switch (state.WireType)
            {
                case WireType.String:
                case WireType.StartGroup:
                    var scaled = state.ReadSubItem<ScaledTicks>(serializer: WellKnownSerializer.Instance);
                    kind = scaled.Kind;
                    return scaled.ToTicks();
                case WireType.Fixed64:
                    kind = DateTimeKind.Unspecified;
                    return state.ReadInt64();
                default:
                    ThrowHelper.ThrowProtoException($"Unexpected wire-type: {state.WireType}");
                    kind = default;
                    return default;
            }
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
            => state.ReadSubItem<decimal>(serializer: WellKnownSerializer.Instance);

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
            => state.WriteSubItem<decimal>(value, WellKnownSerializer.Instance);

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
            => state.WriteSubItem<Guid>(value, WellKnownSerializer.Instance);

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
            => state.ReadSubItem<Guid>(serializer: WellKnownSerializer.Instance);

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
        public static object ReadNetObject(object value, ProtoReader source, int key, Type type, NetObjectOptions options)
        {
            ProtoReader.State state = source.DefaultState();
            return ReadNetObject(ref state, value, key, type, options);
        }

        /// <summary>
        /// Reads an *implementation specific* bundled .NET object, including (as options) type-metadata, identity/re-use, etc.
        /// </summary>
        internal static object ReadNetObject(ref ProtoReader.State state, object value, int key, Type type, NetObjectOptions options)
        {
            var source = state.GetReader();
            SubItemToken token = state.StartSubItem();
            int fieldNumber;
            int newObjectKey = -1, newTypeKey = -1, tmp;
            while ((fieldNumber = state.ReadFieldHeader()) > 0)
            {
                switch (fieldNumber)
                {
                    case FieldExistingObjectKey:
                        tmp = state.ReadInt32();
                        value = source.NetCache.GetKeyedObject(tmp);
                        break;
                    case FieldNewObjectKey:
                        newObjectKey = state.ReadInt32();
                        break;
                    case FieldExistingTypeKey:
                        tmp = state.ReadInt32();
                        type = (Type)source.NetCache.GetKeyedObject(tmp);
                        key = source.GetTypeKey(ref type);
                        break;
                    case FieldNewTypeKey:
                        newTypeKey = state.ReadInt32();
                        break;
                    case FieldTypeName:
                        string typeName = state.ReadString();
                        type = source.DeserializeType(typeName);
                        if (type == null)
                        {
                            ThrowHelper.ThrowProtoException("Unable to resolve type: " + typeName + " (you can use the TypeModel.DynamicTypeFormatting event to provide a custom mapping)");
                        }
                        if (type == typeof(string))
                        {
                            key = -1;
                        }
                        else
                        {
                            key = source.GetTypeKey(ref type);
                            if (key < 0)
                                ThrowHelper.ThrowInvalidOperationException("Dynamic type is not a contract-type: " + type.Name);
                        }
                        break;
                    case FieldObject:
                        bool isString = type == typeof(string);
                        bool wasNull = value == null;
                        bool lateSet = wasNull && (isString || ((options & NetObjectOptions.LateSet) != 0));

                        if (newObjectKey >= 0 && !lateSet)
                        {
                            if (value == null)
                            {
                                source.TrapNextObject(newObjectKey);
                            }
                            else
                            {
                                source.NetCache.SetKeyedObject(newObjectKey, value);
                            }
                            if (newTypeKey >= 0) source.NetCache.SetKeyedObject(newTypeKey, type);
                        }
                        object oldValue = value;
                        if (isString)
                        {
                            value = state.ReadString();
                        }
                        else
                        {
                            value = state.ReadTypedObject(oldValue, key, type);
                        }

                        if (newObjectKey >= 0)
                        {
                            if (wasNull && !lateSet)
                            { // this both ensures (via exception) that it *was* set, and makes sure we don't shout
                                // about changed references
                                oldValue = source.NetCache.GetKeyedObject(newObjectKey);
                            }
                            if (lateSet)
                            {
                                source.NetCache.SetKeyedObject(newObjectKey, value);
                                if (newTypeKey >= 0) source.NetCache.SetKeyedObject(newTypeKey, type);
                            }
                        }
                        if (newObjectKey >= 0 && !lateSet && !ReferenceEquals(oldValue, value))
                        {
                            ThrowHelper.ThrowProtoException("A reference-tracked object changed reference during deserialization");
                        }
                        if (newObjectKey < 0 && newTypeKey >= 0)
                        {  // have a new type, but not a new object
                            source.NetCache.SetKeyedObject(newTypeKey, type);
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
        public static void WriteNetObject(object value, ProtoWriter dest, int key, NetObjectOptions options)
        {
            ProtoWriter.State state = dest.DefaultState();
            WriteNetObject(ref state, value, key, options);
        }

        /// <summary>
        /// Writes an *implementation specific* bundled .NET object, including (as options) type-metadata, identity/re-use, etc.
        /// </summary>
        internal static void WriteNetObject(ref ProtoWriter.State state, object value, int key, NetObjectOptions options)
        {
            var dest = state.GetWriter();
            bool dynamicType = (options & NetObjectOptions.DynamicType) != 0,
                 asReference = (options & NetObjectOptions.AsReference) != 0;
            WireType wireType = dest.WireType;
            SubItemToken token = state.StartSubItem(null);
            bool writeObject = true;
            if (asReference)
            {
                int objectKey = dest.NetCache.AddObjectKey(value, out bool existing);
                state.WriteFieldHeader(existing ? FieldExistingObjectKey : FieldNewObjectKey, WireType.Varint);
                state.WriteInt32(objectKey);
                if (existing)
                {
                    writeObject = false;
                }
            }

            if (writeObject)
            {
                if (dynamicType)
                {
                    Type type = value.GetType();

                    if (!(value is string))
                    {
                        key = dest.GetTypeKey(ref type);
                        if (key < 0) ThrowHelper.ThrowInvalidOperationException("Dynamic type is not a contract-type: " + type.Name);
                    }
                    int typeKey = dest.NetCache.AddObjectKey(type, out bool existing);
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
                    state.WriteObject(value, key);
                }
            }
            state.EndSubItem(token);
        }
    }
}