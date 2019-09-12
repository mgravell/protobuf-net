﻿using ProtoBuf.WellKnownTypes;
using System;
using System.Runtime.InteropServices;
using System.Reflection;

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
    public sealed class BclHelpers // should really be static, but I'm cheating with a <T>
    {
        private BclHelpers() { }
        /// <summary>
        /// Creates a new instance of the specified type, bypassing the constructor.
        /// </summary>
        /// <param name="type">The type to create</param>
        /// <returns>The new instance</returns>
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
        [Obsolete(ProtoWriter.UseStateAPI, false)]
        public static void WriteTimeSpan(TimeSpan timeSpan, ProtoWriter dest)
        {
            ProtoWriter.State state = dest.DefaultState();
            WriteTimeSpanImpl(timeSpan, dest, DateTimeKind.Unspecified, ref state);
        }

        /// <summary>
        /// Writes a TimeSpan to a protobuf stream using protobuf-net's own representation, bcl.TimeSpan
        /// </summary>
        public static void WriteTimeSpan(TimeSpan timeSpan, ProtoWriter dest, ref ProtoWriter.State state)
        {
            WriteTimeSpanImpl(timeSpan, dest, DateTimeKind.Unspecified, ref state);
        }

        private static void WriteTimeSpanImpl(TimeSpan timeSpan, ProtoWriter dest, DateTimeKind kind, ref ProtoWriter.State state)
        {
            if (dest == null) throw new ArgumentNullException(nameof(dest));

            switch (dest.WireType)
            {
                case WireType.String:
                case WireType.StartGroup:
                    var scaled = new ScaledTicks(timeSpan, kind);
                    ProtoWriter.WriteSubItem<ScaledTicks>(scaled, dest, ref state, WellKnownSerializer.Instance);
                    break;
                case WireType.Fixed64:
                    ProtoWriter.WriteInt64(timeSpan.Ticks, dest, ref state);
                    break;
                default:
                    throw new ProtoException("Unexpected wire-type: " + dest.WireType.ToString());
            }
        }

        /// <summary>
        /// Parses a TimeSpan from a protobuf stream using protobuf-net's own representation, bcl.TimeSpan
        /// </summary>
        [Obsolete(ProtoReader.UseStateAPI, false)]
        public static TimeSpan ReadTimeSpan(ProtoReader source)
        {
            ProtoReader.State state = source.DefaultState();
            return ReadTimeSpan(source, ref state);
        }

        /// <summary>
        /// Parses a TimeSpan from a protobuf stream using protobuf-net's own representation, bcl.TimeSpan
        /// </summary>
        public static TimeSpan ReadTimeSpan(ProtoReader source, ref ProtoReader.State state)
        {
            long ticks = ReadTimeSpanTicks(source, ref state, out DateTimeKind _);
            if (ticks == long.MinValue) return TimeSpan.MinValue;
            if (ticks == long.MaxValue) return TimeSpan.MaxValue;
            return TimeSpan.FromTicks(ticks);
        }

        /// <summary>
        /// Parses a TimeSpan from a protobuf stream using the standardized format, google.protobuf.Duration
        /// </summary>
        [Obsolete(ProtoReader.UseStateAPI, false)]
        public static TimeSpan ReadDuration(ProtoReader source)
        {
            ProtoReader.State state = source.DefaultState();
            return ReadDuration(source, ref state);
        }

        /// <summary>
        /// Parses a TimeSpan from a protobuf stream using the standardized format, google.protobuf.Duration
        /// </summary>
        public static TimeSpan ReadDuration(ProtoReader source, ref ProtoReader.State state)
            => source.ReadSubItem<Duration>(ref state, WellKnownSerializer.Instance);

        /// <summary>
        /// Writes a TimeSpan to a protobuf stream using the standardized format, google.protobuf.Duration
        /// </summary>
        [Obsolete(ProtoWriter.UseStateAPI)]
        public static void WriteDuration(TimeSpan value, ProtoWriter dest)
        {
            ProtoWriter.State state = dest.DefaultState();
            WriteDuration(value, dest, ref state);
        }

        /// <summary>
        /// Writes a TimeSpan to a protobuf stream using the standardized format, google.protobuf.Duration
        /// </summary>
        public static void WriteDuration(TimeSpan value, ProtoWriter dest, ref ProtoWriter.State state)
            => ProtoWriter.WriteSubItem<Duration>(value, dest, ref state, WellKnownSerializer.Instance);

        /// <summary>
        /// Parses a DateTime from a protobuf stream using the standardized format, google.protobuf.Timestamp
        /// </summary>
        [Obsolete(ProtoReader.UseStateAPI, false)]
        public static DateTime ReadTimestamp(ProtoReader source)
        {
            ProtoReader.State state = source.DefaultState();
            return ReadTimestamp(source, ref state);
        }

        /// <summary>
        /// Parses a DateTime from a protobuf stream using the standardized format, google.protobuf.Timestamp
        /// </summary>
        public static DateTime ReadTimestamp(ProtoReader source, ref ProtoReader.State state)
        {
            // note: DateTime is only defined for just over 0000 to just below 10000;
            // TimeSpan has a range of +/- 10,675,199 days === 29k years;
            // so we can just use epoch time delta
            return source.ReadSubItem<Timestamp>(ref state, WellKnownSerializer.Instance);
        }

        /// <summary>
        /// Writes a DateTime to a protobuf stream using the standardized format, google.protobuf.Timestamp
        /// </summary>
        [Obsolete(ProtoWriter.UseStateAPI, false)]
        public static void WriteTimestamp(DateTime value, ProtoWriter dest)
        {
            ProtoWriter.State state = dest.DefaultState();
            WriteTimestamp(value, dest, ref state);
        }

        /// <summary>
        /// Writes a DateTime to a protobuf stream using the standardized format, google.protobuf.Timestamp
        /// </summary>
        public static void WriteTimestamp(DateTime value, ProtoWriter dest, ref ProtoWriter.State state)
            => ProtoWriter.WriteSubItem<Timestamp>(value, dest, ref state, WellKnownSerializer.Instance);

        /// <summary>
        /// Parses a DateTime from a protobuf stream
        /// </summary>
        [Obsolete(ProtoReader.UseStateAPI, false)]
        public static DateTime ReadDateTime(ProtoReader source)
        {
            ProtoReader.State state = source.DefaultState();
            return ReadDateTime(source, ref state);
        }

        /// <summary>
        /// Parses a DateTime from a protobuf stream
        /// </summary>
        public static DateTime ReadDateTime(ProtoReader source, ref ProtoReader.State state)
        {
            long ticks = ReadTimeSpanTicks(source, ref state, out DateTimeKind kind);
            if (ticks == long.MinValue) return DateTime.MinValue;
            if (ticks == long.MaxValue) return DateTime.MaxValue;
            return EpochOrigin[(int)kind].AddTicks(ticks);
        }

        /// <summary>
        /// Writes a DateTime to a protobuf stream, excluding the <c>Kind</c>
        /// </summary>
        [Obsolete(ProtoWriter.UseStateAPI, false)]
        public static void WriteDateTime(DateTime value, ProtoWriter dest)
        {
            ProtoWriter.State state = dest.DefaultState();
            WriteDateTimeImpl(value, dest, false, ref state);
        }

        /// <summary>
        /// Writes a DateTime to a protobuf stream, excluding the <c>Kind</c>
        /// </summary>
        public static void WriteDateTime(DateTime value, ProtoWriter dest, ref ProtoWriter.State state)
        {
            WriteDateTimeImpl(value, dest, false, ref state);
        }

        /// <summary>
        /// Writes a DateTime to a protobuf stream, including the <c>Kind</c>
        /// </summary>
        [Obsolete(ProtoWriter.UseStateAPI, false)]
        public static void WriteDateTimeWithKind(DateTime value, ProtoWriter dest)
        {
            ProtoWriter.State state = dest.DefaultState();
            WriteDateTimeImpl(value, dest, true, ref state);
        }

        /// <summary>
        /// Writes a DateTime to a protobuf stream, including the <c>Kind</c>
        /// </summary>
        public static void WriteDateTimeWithKind(DateTime value, ProtoWriter dest, ref ProtoWriter.State state)
        {
            WriteDateTimeImpl(value, dest, true, ref state);
        }

        private static void WriteDateTimeImpl(DateTime value, ProtoWriter dest, bool includeKind, ref ProtoWriter.State state)
        {
            if (dest == null) throw new ArgumentNullException(nameof(dest));
            TimeSpan delta;
            switch (dest.WireType)
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
            WriteTimeSpanImpl(delta, dest, includeKind ? value.Kind : DateTimeKind.Unspecified, ref state);
        }

        private static long ReadTimeSpanTicks(ProtoReader source, ref ProtoReader.State state, out DateTimeKind kind)
        {
            switch (source.WireType)
            {
                case WireType.String:
                case WireType.StartGroup:
                    var scaled = source.ReadSubItem<ScaledTicks>(ref state, WellKnownSerializer.Instance);
                    kind = scaled.Kind;
                    return scaled.ToTicks();
                case WireType.Fixed64:
                    kind = DateTimeKind.Unspecified;
                    return source.ReadInt64(ref state);
                default:
                    throw new ProtoException("Unexpected wire-type: " + source.WireType.ToString());
            }
        }

        private const int FieldDecimalLow = 0x01, FieldDecimalHigh = 0x02, FieldDecimalSignScale = 0x03;

        /// <summary>
        /// Parses a decimal from a protobuf stream
        /// </summary>
        [Obsolete(ProtoReader.UseStateAPI, false)]
        public static decimal ReadDecimal(ProtoReader reader)
        {
            ProtoReader.State state = reader.DefaultState();
            return ReadDecimal(reader, ref state);
        }
        /// <summary>
        /// Parses a decimal from a protobuf stream
        /// </summary>
        public static decimal ReadDecimal(ProtoReader reader, ref ProtoReader.State state)
            => reader.ReadSubItem<decimal>(ref state, WellKnownSerializer.Instance);

        /// <summary>
        /// Writes a decimal to a protobuf stream
        /// </summary>
        [Obsolete(ProtoWriter.UseStateAPI, false)]
        public static void WriteDecimal(decimal value, ProtoWriter writer)
        {
            ProtoWriter.State state = writer.DefaultState();
            WriteDecimal(value, writer, ref state);
        }


        /// <summary>
        /// Writes a decimal to a protobuf stream
        /// </summary>
        public static void WriteDecimal(decimal value, ProtoWriter writer, ref ProtoWriter.State state)
            => ProtoWriter.WriteSubItem<decimal>(value, writer, ref state, WellKnownSerializer.Instance);

        /// <summary>
        /// Writes a Guid to a protobuf stream
        /// </summary>
        [Obsolete(ProtoWriter.UseStateAPI, false)]
        public static void WriteGuid(Guid value, ProtoWriter dest)
        {
            ProtoWriter.State state = dest.DefaultState();
            WriteGuid(value, dest, ref state);
        }

        /// <summary>
        /// Writes a Guid to a protobuf stream
        /// </summary>        
        public static void WriteGuid(Guid value, ProtoWriter dest, ref ProtoWriter.State state)
            => ProtoWriter.WriteSubItem<Guid>(value, dest, ref state, WellKnownSerializer.Instance);

        /// <summary>
        /// Parses a Guid from a protobuf stream
        /// </summary>
        [Obsolete(ProtoReader.UseStateAPI, false)]
        public static Guid ReadGuid(ProtoReader source)
        {
            ProtoReader.State state = source.DefaultState();
            return ReadGuid(source, ref state);
        }

        /// <summary>
        /// Parses a Guid from a protobuf stream
        /// </summary>
        public static Guid ReadGuid(ProtoReader source, ref ProtoReader.State state)
            => source.ReadSubItem<Guid>(ref state, WellKnownSerializer.Instance);

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
        [Obsolete(ProtoReader.UseStateAPI, false)]
        public static object ReadNetObject(object value, ProtoReader source, int key, Type type, NetObjectOptions options)
        {
            ProtoReader.State state = source.DefaultState();
            return ReadNetObject(source, ref state, value, key, type, options);
        }

        /// <summary>
        /// Reads an *implementation specific* bundled .NET object, including (as options) type-metadata, identity/re-use, etc.
        /// </summary>
        public static object ReadNetObject(ProtoReader source, ref ProtoReader.State state, object value, int key, Type type, NetObjectOptions options)
        {
            SubItemToken token = ProtoReader.StartSubItem(source, ref state);
            int fieldNumber;
            int newObjectKey = -1, newTypeKey = -1, tmp;
            while ((fieldNumber = source.ReadFieldHeader(ref state)) > 0)
            {
                switch (fieldNumber)
                {
                    case FieldExistingObjectKey:
                        tmp = source.ReadInt32(ref state);
                        value = source.NetCache.GetKeyedObject(tmp);
                        break;
                    case FieldNewObjectKey:
                        newObjectKey = source.ReadInt32(ref state);
                        break;
                    case FieldExistingTypeKey:
                        tmp = source.ReadInt32(ref state);
                        type = (Type)source.NetCache.GetKeyedObject(tmp);
                        key = source.GetTypeKey(ref type);
                        break;
                    case FieldNewTypeKey:
                        newTypeKey = source.ReadInt32(ref state);
                        break;
                    case FieldTypeName:
                        string typeName = source.ReadString(ref state);
                        type = source.DeserializeType(typeName);
                        if (type == null)
                        {
                            throw new ProtoException("Unable to resolve type: " + typeName + " (you can use the TypeModel.DynamicTypeFormatting event to provide a custom mapping)");
                        }
                        if (type == typeof(string))
                        {
                            key = -1;
                        }
                        else
                        {
                            key = source.GetTypeKey(ref type);
                            if (key < 0)
                                throw new InvalidOperationException("Dynamic type is not a contract-type: " + type.Name);
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
                            value = source.ReadString(ref state);
                        }
                        else
                        {
                            value = ProtoReader.ReadTypedObject(source, ref state, oldValue, key, type);
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
                            throw new ProtoException("A reference-tracked object changed reference during deserialization");
                        }
                        if (newObjectKey < 0 && newTypeKey >= 0)
                        {  // have a new type, but not a new object
                            source.NetCache.SetKeyedObject(newTypeKey, type);
                        }
                        break;
                    default:
                        source.SkipField(ref state);
                        break;
                }
            }
            if (newObjectKey >= 0 && (options & NetObjectOptions.AsReference) == 0)
            {
                throw new ProtoException("Object key in input stream, but reference-tracking was not expected");
            }
            ProtoReader.EndSubItem(token, source, ref state);

            return value;
        }

        /// <summary>
        /// Writes an *implementation specific* bundled .NET object, including (as options) type-metadata, identity/re-use, etc.
        /// </summary>
        [Obsolete(ProtoWriter.UseStateAPI, false)]
        public static void WriteNetObject(object value, ProtoWriter dest, int key, NetObjectOptions options)
        {
            ProtoWriter.State state = dest.DefaultState();
            WriteNetObject(value, dest, ref state, key, options);
        }

        /// <summary>
        /// Writes an *implementation specific* bundled .NET object, including (as options) type-metadata, identity/re-use, etc.
        /// </summary>
        public static void WriteNetObject(object value, ProtoWriter dest, ref ProtoWriter.State state, int key, NetObjectOptions options)
        {
            if (dest == null) throw new ArgumentNullException(nameof(dest));
            bool dynamicType = (options & NetObjectOptions.DynamicType) != 0,
                 asReference = (options & NetObjectOptions.AsReference) != 0;
            WireType wireType = dest.WireType;
            SubItemToken token = ProtoWriter.StartSubItem(null, dest, ref state);
            bool writeObject = true;
            if (asReference)
            {
                int objectKey = dest.NetCache.AddObjectKey(value, out bool existing);
                ProtoWriter.WriteFieldHeader(existing ? FieldExistingObjectKey : FieldNewObjectKey, WireType.Variant, dest, ref state);
                ProtoWriter.WriteInt32(objectKey, dest, ref state);
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
                        if (key < 0) throw new InvalidOperationException("Dynamic type is not a contract-type: " + type.Name);
                    }
                    int typeKey = dest.NetCache.AddObjectKey(type, out bool existing);
                    ProtoWriter.WriteFieldHeader(existing ? FieldExistingTypeKey : FieldNewTypeKey, WireType.Variant, dest, ref state);
                    ProtoWriter.WriteInt32(typeKey, dest, ref state);
                    if (!existing)
                    {
                        ProtoWriter.WriteFieldHeader(FieldTypeName, WireType.String, dest, ref state);
                        ProtoWriter.WriteString(dest.SerializeType(type), dest, ref state);
                    }
                }
                ProtoWriter.WriteFieldHeader(FieldObject, wireType, dest, ref state);
                if (value is string s)
                {
                    ProtoWriter.WriteString(s, dest, ref state);
                }
                else
                {
                    ProtoWriter.WriteObject(value, key, dest, ref state);
                }
            }
            ProtoWriter.EndSubItem(token, dest, ref state);
        }
    }
}