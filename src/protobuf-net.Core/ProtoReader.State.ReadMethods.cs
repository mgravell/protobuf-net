using ProtoBuf.Internal;
using ProtoBuf.Meta;
using ProtoBuf.Serializers;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProtoBuf
{
    partial class ProtoReader
    {
#if PLAT_AGGRESSIVE_OPTIMIZE
        internal const MethodImplOptions HotPath = MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization;
#else
        internal const MethodImplOptions HotPath = MethodImplOptions.AggressiveInlining;
#endif

        ref partial struct State
        {


            /// <summary>
            /// Reads an unsigned 16-bit integer from the stream; supported wire-types: Variant, Fixed32, Fixed64
            /// </summary>
            [MethodImpl(HotPath)]
            public ushort ReadUInt16()
            {
                checked { return (ushort)ReadUInt32(); }
            }

            /// <summary>
            /// Reads a signed 16-bit integer from the stream: Variant, Fixed32, Fixed64, SignedVariant
            /// </summary>
            [MethodImpl(HotPath)]
            public short ReadInt16()
            {
                checked { return (short)ReadInt32(); }
            }

            /// <summary>
            /// Returns the position of the current reader (note that this is not necessarily the same as the position
            /// in the underlying stream, if multiple readers are used on the same stream)
            /// </summary>
            [MethodImpl(HotPath)]
            public long GetPosition() => _reader._longPosition;

            /// <summary>
            /// Reads an unsigned 8-bit integer from the stream; supported wire-types: Variant, Fixed32, Fixed64
            /// </summary>
            [MethodImpl(HotPath)]
            public byte ReadByte()
            {
                checked { return (byte)ReadUInt32(); }
            }

            /// <summary>
            /// Reads a signed 8-bit integer from the stream; supported wire-types: Variant, Fixed32, Fixed64, SignedVariant
            /// </summary>
            [MethodImpl(HotPath)]
            public sbyte ReadSByte()
            {
                checked { return (sbyte)ReadInt32(); }
            }

            /// <summary>
            /// Reads an unsigned 32-bit integer from the stream; supported wire-types: Variant, Fixed32, Fixed64, SignedVariant
            /// </summary>
            [MethodImpl(HotPath)]
            public uint ReadUInt32()
            {
                switch (_reader.WireType)
                {
                    case WireType.Varint:
                        return ReadUInt32Varint(Read32VarintMode.Signed);
                    case WireType.Fixed32:
                        return _reader.ImplReadUInt32Fixed(ref this);
                    case WireType.Fixed64:
                        ulong val = _reader.ImplReadUInt64Fixed(ref this);
                        checked { return (uint)val; }
                    default:
                        ThrowWireTypeException();
                        return default;
                }
            }

            /// <summary>
            /// Reads a signed 32-bit integer from the stream; supported wire-types: Variant, Fixed32, Fixed64, SignedVariant
            /// </summary>
            [MethodImpl(HotPath)]
            public int ReadInt32()
            {
                switch (_reader.WireType)
                {
                    case WireType.Varint:
                        return (int)ReadUInt32Varint(Read32VarintMode.Signed);
                    case WireType.Fixed32:
                        return (int)_reader.ImplReadUInt32Fixed(ref this);
                    case WireType.Fixed64:
                        long l = ReadInt64();
                        checked { return (int)l; }
                    case WireType.SignedVarint:
                        return Zag(ReadUInt32Varint(Read32VarintMode.Signed));
                    default:
                        ThrowWireTypeException();
                        return default;
                }
            }

            /// <summary>
            /// Reads a signed 64-bit integer from the stream; supported wire-types: Variant, Fixed32, Fixed64, SignedVariant
            /// </summary>
            [MethodImpl(HotPath)]
            public long ReadInt64()
            {
                switch (_reader.WireType)
                {
                    case WireType.Varint:
                        return (long)ReadUInt64Varint();
                    case WireType.Fixed32:
                        return (int)_reader.ImplReadUInt32Fixed(ref this);
                    case WireType.Fixed64:
                        return (long)_reader.ImplReadUInt64Fixed(ref this);
                    case WireType.SignedVarint:
                        return Zag(ReadUInt64Varint());
                    default:
                        ThrowWireTypeException();
                        return default;
                }
            }

            /// <summary>
            /// Reads a double-precision number from the stream; supported wire-types: Fixed32, Fixed64
            /// </summary>
            [MethodImpl(HotPath)]
            public double ReadDouble()
            {
                switch (_reader.WireType)
                {
                    case WireType.Fixed32:
                        return ReadSingle();
                    case WireType.Fixed64:
                        long value = ReadInt64();
                        unsafe { return *(double*)&value; }
                    default:
                        ThrowWireTypeException();
                        return default;
                }
            }

            /// <summary>
            /// Reads a single-precision number from the stream; supported wire-types: Fixed32, Fixed64
            /// </summary>
            [MethodImpl(HotPath)]
            public float ReadSingle()
            {
                switch (_reader.WireType)
                {
                    case WireType.Fixed32:
                        {
                            int value = ReadInt32();
                            unsafe { return *(float*)&value; }
                        }
                    case WireType.Fixed64:
                        {
                            double value = ReadDouble();
                            float f = (float)value;
                            if (float.IsInfinity(f) && !double.IsInfinity(value)) ThrowOverflow();
                            return f;
                        }
                    default:
                        ThrowWireTypeException();
                        return default;
                }
            }

            [MethodImpl(ProtoReader.HotPath)]
            private void PrepareToReadRepeated<T>(ref SerializerFeatures features, SerializerFeatures serializerFeatures, out SerializerFeatures category, out bool packed)
            {
                if (serializerFeatures.IsRepeated()) TypeModel.ThrowNestedListsNotSupported(typeof(T));
                features.InheritFrom(serializerFeatures);
                category = serializerFeatures.GetCategory();
                packed = false;
                if (TypeHelper<T>.CanBePacked && WireType == WireType.String)
                {
                    // the wire type should never by "string" for a type that *can* be
                    // packed, so this *is* packed
                    if (category != SerializerFeatures.CategoryScalar)
                        ThrowInvalidOperationException("Packed data expected a scalar serializer");

                    packed = true;
                }
            }

            [MethodImpl(ProtoReader.HotPath)]
            private void ReadRepeatedCore<TSerializer, TList, T>(ref TList values, SerializerFeatures category, WireType wireType, in TSerializer serializer,
                T initialValue)
                where TSerializer : ISerializer<T>
                where TList : ICollection<T>
            {
                int field = FieldNumber;
                do
                {
                    T element;
                    switch (category)
                    {
                        case SerializerFeatures.CategoryScalar:
                            Hint(wireType);
                            element = serializer.Read(ref this, initialValue);
                            break;
                        case SerializerFeatures.CategoryMessage:
                        case SerializerFeatures.CategoryMessageWrappedAtRoot:
                            element = ReadMessage<TSerializer, T>(default, initialValue, serializer);
                            break;
                        default:
                            category.ThrowInvalidCategory();
                            element = default;
                            break;
                    }
                    values.Add(element);
                } while (TryReadFieldHeader(field));
            }

            [MethodImpl(ProtoReader.HotPath)]
            private void ReadPackedScalar<TSerializer, TList, T>(ref TList list, WireType wireType, in TSerializer serializer)
                where TSerializer : ISerializer<T>
                where TList : ICollection<T>
            {
                var bytes = checked((int)ReadUInt32Varint(Read32VarintMode.Unsigned));
                if (bytes == 0) return;
                switch (wireType)
                {
                    case WireType.Fixed32:
                        if ((bytes % 4) != 0) ThrowHelper.ThrowInvalidOperationException("packed length should be multiple of 4");
                        var count = bytes / 4;
                        goto ReadFixedQuantity;
                    case WireType.Fixed64:
                        if ((bytes % 8) != 0) ThrowHelper.ThrowInvalidOperationException("packed length should be multiple of 8");
                        count = bytes / 8;
                    ReadFixedQuantity:
                        // boost the List<T> capacity if we can, as long as it is within reason (i.e. don't let
                        // a small message lie and claim to have a huge payload)

                        const int MAX_GROW = 8192; // if they are much bigge than this, then the doubling API will help, anyhows
                        if (list is List<T> l) l.Capacity = Math.Max(l.Capacity, l.Count + Math.Min(count, MAX_GROW));

                        for (int i = 0; i < count; i++)
                        {
                            _reader.WireType = wireType;
                            list.Add(serializer.Read(ref this, default));
                        }
                        break;
                    case WireType.Varint:
                    case WireType.SignedVarint:
                        long end = GetPosition() + bytes;
                        do
                        {
                            _reader.WireType = wireType;
                            list.Add(serializer.Read(ref this, default));
                        } while (GetPosition() < end);
                        if (GetPosition() != end) ThrowHelper.ThrowInvalidOperationException("over-read packed data");
                        break;
                    default:
                        ThrowHelper.ThrowInvalidPackedOperationException(WireType, typeof(T));
                        break;
                }
            }

            internal ReadBuffer<T> FillBuffer<TSerializer, T>(SerializerFeatures features, in TSerializer serializer, T initialValue)
                where TSerializer : ISerializer<T>
            {
                PrepareToReadRepeated<T>(ref features, serializer.Features, out var category, out var packed);
                var buffer = ReadBuffer<T>.Create();
                try
                {
                    var wireType = features.GetWireType();
                    if (packed) ReadPackedScalar<TSerializer, ReadBuffer<T>, T>(ref buffer, wireType, serializer);
                    else ReadRepeatedCore<TSerializer, ReadBuffer<T>, T>(ref buffer, category, wireType, serializer, initialValue);
                    return buffer;
                }
                catch
                {
                    try { buffer.Dispose(); } catch { }
                    throw;
                }
            }

            /// <summary>
            /// Reads a boolean value from the stream; supported wire-types: Variant, Fixed32, Fixed64
            /// </summary>
            [MethodImpl(HotPath)]
            public bool ReadBoolean() => ReadUInt32() != 0;

            /// <summary>
            /// Reads an unsigned 64-bit integer from the stream; supported wire-types: Variant, Fixed32, Fixed64
            /// </summary>
            [MethodImpl(HotPath)]
            public ulong ReadUInt64()
            {
                switch (_reader.WireType)
                {
                    case WireType.Varint: return ReadUInt64Varint();
                    case WireType.Fixed32: return _reader.ImplReadUInt32Fixed(ref this);
                    case WireType.Fixed64: return _reader.ImplReadUInt64Fixed(ref this);
                    default:
                        ThrowWireTypeException();
                        return default;
                }
            }

            /// <summary>
            /// Reads a byte-sequence from the stream, appending them to an existing byte-sequence (which can be null); supported wire-types: String
            /// </summary>
            [MethodImpl(MethodImplOptions.NoInlining)]
            public byte[] AppendBytes(byte[] value)
                => AppendBytes(value, DefaultMemoryConverter<byte>.Instance);

            /// <summary>
            /// Reads a byte-sequence from the stream, appending them to an existing byte-sequence; supported wire-types: String
            /// </summary>
            [MethodImpl(ProtoReader.HotPath)]
            public ReadOnlyMemory<byte> AppendBytes(ReadOnlyMemory<byte> value)
                => AppendBytesImpl(value, DefaultMemoryConverter<byte>.Instance);

            /// <summary>
            /// Reads a byte-sequence from the stream, appending them to an existing byte-sequence; supported wire-types: String
            /// </summary>
            [MethodImpl(ProtoReader.HotPath)]
            public Memory<byte> AppendBytes(Memory<byte> value)
                => AppendBytesImpl(value, DefaultMemoryConverter<byte>.Instance);

            /// <summary>
            /// Reads a byte-sequence from the stream, appending them to an existing byte-sequence; supported wire-types: String
            /// </summary>
            [MethodImpl(ProtoReader.HotPath)]
            public ArraySegment<byte> AppendBytes(ArraySegment<byte> value)
                => AppendBytesImpl(value, DefaultMemoryConverter<byte>.Instance);

            /// <summary>
            /// Reads a byte-sequence from the stream, appending them to an existing byte-sequence (which can be null); supported wire-types: String
            /// </summary>
            [MethodImpl(ProtoReader.HotPath)]
            public TStorage AppendBytes<TStorage>(TStorage value, IMemoryConverter<TStorage, byte> converter = null)
                => AppendBytesImpl(value, converter ?? DefaultMemoryConverter<byte>.GetFor<TStorage>(Model));

            private TStorage AppendBytesImpl<TStorage>(TStorage value, IMemoryConverter<TStorage, byte> converter)
            {
                switch (_reader.WireType)
                {
                    case WireType.String:
                        int len = (int)ReadUInt32Varint(Read32VarintMode.Signed);
                        _reader.WireType = WireType.None;
                        if (len == 0) return converter.NonNull(value);

                        // expand the storage
#if DEBUG
                        var oldLength = converter.GetLength(value);
#endif
                        var newChunk = converter.Expand(Context, ref value, len);
#if DEBUG
                        if (converter.GetLength(value) != (oldLength + len))
                            ThrowHelper.ThrowInvalidOperationException($"The memory converter ({converter.GetType().NormalizeName()}) got the lengths wrong for the updated value; expected {oldLength + len}, got {converter.GetLength(value)}");
                        if (newChunk.Length != len)
                            ThrowHelper.ThrowInvalidOperationException($"The memory converter ({converter.GetType().NormalizeName()}) got the lengths wrong for the returned chunk; expected {len}, got {newChunk.Length}");
#endif               
                        // read the data into the new part
                        _reader.ImplReadBytes(ref this, newChunk.Span);

                        return value;
                    //case WireType.Varint:
                    //    return new byte[0];
                    default:
                        ThrowWireTypeException();
                        return default;
                }
            }

            /// <summary>
            /// Tries to read a string-like type directly into a span; if successful, the span
            /// returned indicates the available amount of data; if unsuccessful, an exception
            /// is thrown; this should only be used when there is confidence that the length
            /// is bounded.
            /// </summary>
            public Span<byte> ReadBytes(Span<byte> destination)
            {
                switch (_reader.WireType)
                {
                    case WireType.String:
                        int len = (int)ReadUInt32Varint(Read32VarintMode.Signed);
                        if (len > destination.Length)
                            ThrowHelper.ThrowInvalidOperationException($"Insufficient space in the target span to read a string/bytes value; {destination.Length} vs {len} bytes");
                        _reader.WireType = WireType.None;
                        destination = destination.Slice(0, len);
                        _reader.ImplReadBytes(ref this, destination);
                        return destination;
                    default:
                        ThrowWireTypeException();
                        return default;
                }
            }

            ///// <summary>
            ///// Reads a byte-sequence from the stream, appending them to an existing byte-sequence (which can be empty); supported wire-types: String
            ///// </summary>
            ///// <remarks>A custom allocator may be employed, in which case the sequence it returns will be treated as mutable</remarks>
            //[MethodImpl(MethodImplOptions.NoInlining)]
            //public ReadOnlySequence<byte> AppendBytes(ReadOnlySequence<byte> value, 
            //    Func<ISerializationContext, int, ReadOnlySequence<byte>> allocator = default)
            //{
            //    switch (_reader.WireType)
            //    {
            //        case WireType.String:
            //            int len = (int)ReadUInt32Varint(Read32VarintMode.Signed);
            //            _reader.WireType = WireType.None;
            //            if (len == 0) return value;

            //            ReadOnlySequence<byte> newData;
            //            int newLength = checked((int)(value.Length + len));
            //            if (allocator is null)
            //            {
            //                newData = new ReadOnlySequence<byte>(new byte[newLength]);
            //            }
            //            else
            //            {
            //                newData = allocator(Context, newLength)
            //                    .Slice(0, newLength); // don't trust the allocator to get the length right!
            //            }

            //            // copy the old data (if any) into the new result
            //            if (!value.IsEmpty) CopySequence(from: value, to: newData);
            //            // read the data into the new part
            //            _reader.ImplReadBytes(ref this, newData.Slice(start: value.Length));
            //            return newData;
            //        default:
            //            ThrowWireTypeException();
            //            return default;
            //    }

            //    static void CopySequence(in ReadOnlySequence<byte> from, in ReadOnlySequence<byte> to)
            //    {
            //        if (to.IsSingleSegment)
            //        {
            //            from.CopyTo(MemoryMarshal.AsMemory(to.First).Span);
            //        }
            //        else
            //        {
            //            if (to.Length < from.Length) ThrowHelper.ThrowInvalidOperationException();
            //            SequencePosition origin = from.Start;
            //            foreach (var segment in to)
            //            {
            //                var target = MemoryMarshal.AsMemory(segment).Span;
            //                var tmp = from.Slice(origin, target.Length);
            //                origin = tmp.End;
            //                tmp.CopyTo(target);
            //            }
            //        }
            //    }
            //}

            /// <summary>
            /// Begins consuming a nested message in the stream; supported wire-types: StartGroup, String
            /// </summary>
            /// <remarks>The token returned must be help and used when callining EndSubItem</remarks>
            // [Obsolete(PreferReadMessage, false)]
            [MethodImpl(MethodImplOptions.NoInlining)]
            public SubItemToken StartSubItem()
            {
                var reader = _reader;
                switch (_reader.WireType)
                {
                    case WireType.StartGroup:
                        reader.WireType = WireType.None; // to prevent glitches from double-calling
                        reader._depth++;
                        return new SubItemToken((long)(-reader._fieldNumber));
                    case WireType.String:
                        long len = (long)ReadUInt64Varint();
                        if (len < 0) ThrowInvalidOperationException();
                        long lastEnd = reader.blockEnd64;
                        reader.blockEnd64 = reader._longPosition + len;
                        reader._depth++;
                        return new SubItemToken(lastEnd);
                    default:
                        ThrowWireTypeException();
                        return default;
                }
            }

            /// <summary>
            /// Makes the end of consuming a nested message in the stream; the stream must be either at the correct EndGroup
            /// marker, or all fields of the sub-message must have been consumed (in either case, this means ReadFieldHeader
            /// should return zero)
            /// </summary>
            // [Obsolete(PreferReadMessage, false)]
            [MethodImpl(MethodImplOptions.NoInlining)]
            public void EndSubItem(SubItemToken token)
            {
                long value64 = token.value64;
                var reader = _reader;
                switch (reader.WireType)
                {
                    case WireType.EndGroup:
                        if (value64 >= 0) ThrowProtoException("A length-based message was terminated via end-group; this indicates data corruption");
                        if (-(int)value64 != reader._fieldNumber) ThrowProtoException("Wrong group was ended"); // wrong group ended!
                        reader.WireType = WireType.None; // this releases ReadFieldHeader
                        reader._depth--;
                        break;
                    // case WireType.None: // TODO reinstate once reads reset the wire-type
                    default:
                        long position = reader._longPosition;
                        if (value64 < position) ThrowProtoException($"Sub-message not read entirely; expected {value64}, was {position}");
                        if (reader.blockEnd64 != position && reader.blockEnd64 != long.MaxValue)
                        {
                            ThrowProtoException($"Sub-message not read correctly (end {reader.blockEnd64} vs {position})");
                        }
                        reader.blockEnd64 = value64;
                        reader._depth--;
                        break;
                        /*default:
                            throw reader.BorkedIt(); */
                }
            }

            /// <summary>
            /// Reads (merges) a sub-message from the stream, internally calling StartSubItem and EndSubItem, and (in between)
            /// parsing the message in accordance with the model associated with the reader
            /// </summary>
            [MethodImpl(HotPath)]
            internal object ReadObject(object value, Type type) => ReadTypedObject(value, type);

            [MethodImpl(MethodImplOptions.NoInlining)]
            internal object ReadTypedObject(object value, Type type)
            {
                var model = Model;
                if (model is null) ThrowInvalidOperationException("Cannot deserialize sub-objects unless a model is provided");

                if (DynamicStub.TryDeserialize(ObjectScope.WrappedMessage, type, model, ref this, ref value))
                    return value;


                SubItemToken token = StartSubItem();
                if (type is object && model.TryDeserializeAuxiliaryType(ref this, DataFormat.Default, TypeModel.ListItemTag, type, ref value, true, false, true, false, null))
                {
                    // handled it the easy way
                }
                else
                {
                    TypeModel.ThrowUnexpectedType(type, Model);
                }
                EndSubItem(token);
                return value;
            }


            internal void SkipAllFields()
            {
                while (ReadFieldHeader() > 0) SkipField();
            }

            /// <summary>
            /// Reads a string from the stream (using UTF8); supported wire-types: String
            /// </summary>
            [MethodImpl(HotPath)]
#pragma warning disable IDE0060 // map isn't implemented yet, but we definitely want it
            public string ReadString(StringMap map = null)
#pragma warning restore IDE0060
            {
                if (_reader.WireType == WireType.String)
                {
                    int bytes = (int)ReadUInt32Varint(Read32VarintMode.Unsigned);
                    if (bytes == 0) return "";
                    var s = _reader.ImplReadString(ref this, bytes);
                    if (_reader.InternStrings) { s = _reader.Intern(s); }
                    return s;
                }
                ThrowWireTypeException();
                return default;
            }

            [MethodImpl(HotPath)]
            private uint ReadUInt32Varint(Read32VarintMode mode)
            {
                int read = _reader.ImplTryReadUInt32VarintWithoutMoving(ref this, mode, out uint value);
                if (read <= 0)
                {
                    if (mode == Read32VarintMode.FieldHeader) return 0;
                    ThrowEoF();
                }
                _reader.ImplSkipBytes(ref this, read);
                return value;
            }

            [MethodImpl(HotPath)]
            private ulong ReadUInt64Varint()
            {
                int read = _reader.ImplTryReadUInt64VarintWithoutMoving(ref this, out ulong value);
                if (read <= 0) ThrowEoF();

                _reader.ImplSkipBytes(ref this, read);
                return value;
            }

            /// <summary>
            /// Verifies that the stream's current wire-type is as expected, or a specialized sub-type (for example,
            /// SignedVariant) - in which case the current wire-type is updated. Otherwise an exception is thrown.
            /// </summary>
            [MethodImpl(HotPath)]
            public void Assert(WireType wireType)
            {
                var actual = _reader.WireType;
                if (actual == wireType) { }  // fine; everything as we expect
                else if (((int)wireType & 7) == (int)actual)
                {   // the underling type is a match; we're customising it with an extension
                    _reader.WireType = wireType;
                }
                else
                {   // nope; that is *not* what we were expecting!
                    ThrowWireTypeException();
                }
            }

#if FEAT_DYNAMIC_REF
            internal object GetKeyedObject(int key) => _reader.GetKeyedObject(key);

            internal void SetKeyedObject(int key, object value) => _reader.SetKeyedObject(key, value);

            internal void TrapNextObject(int key) => _reader.TrapNextObject(key);
#endif

            /// <summary>
            /// Discards the data for the current field.
            /// </summary>
            [MethodImpl(HotPath)]
            public void SkipField()
            {
                switch (_reader.WireType)
                {
                    case WireType.Fixed32:
                        _reader.ImplSkipBytes(ref this, 4);
                        break;
                    case WireType.Fixed64:
                        _reader.ImplSkipBytes(ref this, 8);
                        break;
                    case WireType.String:
                        long len = (long)ReadUInt64Varint();
                        _reader.ImplSkipBytes(ref this, len);
                        break;
                    case WireType.Varint:
                    case WireType.SignedVarint:
                        ReadUInt64Varint(); // and drop it
                        break;
                    case WireType.StartGroup:
                        SkipGroup();
                        break;
                    case WireType.None: // treat as explicit errorr
                    case WireType.EndGroup: // treat as explicit error
                    default: // treat as implicit error
                        ThrowWireTypeException();
                        break;
                }
            }

            internal Type DeserializeType(string typeName) => _reader.DeserializeType(typeName);

            [MethodImpl(MethodImplOptions.NoInlining)]
            private void SkipGroup()
            {
                int originalFieldNumber = _reader._fieldNumber;
                _reader._depth++; // need to satisfy the sanity-checks in ReadFieldHeader
                while (ReadFieldHeader() > 0) { SkipField(); }
                _reader._depth--;
                if (_reader.WireType == WireType.EndGroup && _reader._fieldNumber == originalFieldNumber)
                { // we expect to exit in a similar state to how we entered
                    _reader.WireType = WireType.None;
                    return;
                }
                ThrowWireTypeException();
            }

            /// <summary>
            /// Reads a field header from the stream, setting the wire-type and retuning the field number. If no
            /// more fields are available, then 0 is returned. This methods respects sub-messages.
            /// </summary>
            [MethodImpl(HotPath)]
            public int ReadFieldHeader()
            {
                // at the end of a group the caller must call EndSubItem to release the
                // reader (which moves the status to Error, since ReadFieldHeader must
                // then be called)
                if (_reader.blockEnd64 <= _reader._longPosition || _reader.WireType == WireType.EndGroup)
                    return 0;

                if (RemainingInCurrent >= 5)
                {
                    var read = ReadVarintUInt32(out var tag);
                    _reader.Advance(read);
                    return _reader.SetTag(tag);
                }
                return ReadFieldHeaderFallback();
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            private int ReadFieldHeaderFallback()
            {
                int read = _reader.ImplTryReadUInt32VarintWithoutMoving(ref this, Read32VarintMode.FieldHeader, out var tag);
                if (read == 0)
                {
                    _reader.WireType = 0;
                    return _reader._fieldNumber = 0;
                }
                _reader.ImplSkipBytes(ref this, read);
                return _reader.SetTag(tag);
            }

            /// <summary>
            /// Looks ahead to see whether the next field in the stream is what we expect
            /// (typically; what we've just finished reading - for example ot read successive list items)
            /// </summary>
            [MethodImpl(HotPath)]
            public bool TryReadFieldHeader(int field)
            {
                var reader = _reader;
                // check for virtual end of stream
                if (reader.blockEnd64 <= reader._longPosition || reader.WireType == WireType.EndGroup) { return false; }

                int read = reader.ImplTryReadUInt32VarintWithoutMoving(ref this, Read32VarintMode.FieldHeader, out uint tag);
                WireType tmpWireType; // need to catch this to exclude (early) any "end group" tokens
                if (read > 0 && ((int)tag >> 3) == field
                    && (tmpWireType = (WireType)(tag & 7)) != WireType.EndGroup)
                {
                    reader.WireType = tmpWireType;
                    reader._fieldNumber = field;
                    reader.ImplSkipBytes(ref this, read);
                    return true;
                }
                return false;
            }

            [MethodImpl(HotPath)]
            internal void CheckFullyConsumed()
            {
                if (!_reader.IsFullyConsumed(ref this) && !_reader.AllowZeroPadding) ThrowProtoException("Incorrect number of bytes consumed");
            }

            /// <summary>
            /// Compares the streams current wire-type to the hinted wire-type, updating the reader if necessary; for example,
            /// a Variant may be updated to SignedVariant. If the hinted wire-type is unrelated then no change is made.
            /// </summary>
            [MethodImpl(HotPath)]
            public void Hint(WireType wireType) => _reader.Hint(wireType);

            [MethodImpl(MethodImplOptions.NoInlining)]
            internal void ThrowWireTypeException()
            {
                var message = _reader is null ? "(no reader)" : $"Invalid wire-type ({_reader.WireType}); this usually means you have over-written a file without truncating or setting the length; see https://stackoverflow.com/q/2152978/23354";
                ThrowProtoException(message);
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            internal void ThrowProtoException(string message)
            {
                throw AddErrorData(new ProtoException(message), _reader, ref this);
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            internal void ThrowEoF()
            {
                throw AddErrorData(new EndOfStreamException(), _reader, ref this);
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            internal void ThrowInvalidOperationException(string message = null)
            {
                var ex = string.IsNullOrWhiteSpace(message) ? new InvalidOperationException() : new InvalidOperationException(message);
                throw AddErrorData(ex, _reader, ref this);
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            internal void ThrowArgumentException(string message)
            {
                throw AddErrorData(new ArgumentException(message), _reader, ref this);
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            internal void ThrowOverflow()
            {
                throw AddErrorData(new OverflowException(), _reader, ref this);
            }

            internal static Exception AddErrorData(Exception exception, ProtoReader source, ref State state)
            {
                if (exception is object && source is object && !exception.Data.Contains("protoSource"))
                {
                    exception.Data.Add("protoSource", string.Format("tag={0}; wire-type={1}; offset={2}; depth={3}",
                        source.FieldNumber, source.WireType, state.GetPosition(), source._depth));
                }
                return exception;
            }

            [MethodImpl(HotPath)]
            private static int Zag(uint ziggedValue)
            {
                const int Int32Msb = 1 << 31;

                int value = (int)ziggedValue;
                return (-(value & 0x01)) ^ ((value >> 1) & ~Int32Msb);
            }

            [MethodImpl(HotPath)]
            private static long Zag(ulong ziggedValue)
            {
                const long Int64Msb = 1L << 63;

                long value = (long)ziggedValue;
                return (-(value & 0x01L)) ^ ((value >> 1) & ~Int64Msb);
            }

            /// <summary>
            /// Throws an exception indication that the given value cannot be mapped to an enum.
            /// </summary>
            [MethodImpl(MethodImplOptions.NoInlining)]
            public void ThrowEnumException(Type type, int value)
            {
                string desc = type is null ? "<null>" : type.FullName;
                throw AddErrorData(new ProtoException("No " + desc + " enum is mapped to the wire-value " + value.ToString()), _reader, ref this);
            }

            /// <summary>
            /// Copies the current field into the instance as extension data
            /// </summary>
            [MethodImpl(MethodImplOptions.NoInlining)]
            public void AppendExtensionData(IExtensible instance)
            {
                if (instance is null) ThrowHelper.ThrowArgumentNullException(nameof(instance));
                IExtension extn = instance.GetExtensionObject(true);
                bool commit = false;
                // unusually we *don't* want "using" here; the "finally" does that, with
                // the extension object being responsible for disposal etc
                Stream dest = extn.BeginAppend();
                try
                {
                    //TODO: replace this with stream-based, buffered raw copying
                    var writeState = ProtoWriter.State.Create(dest, _reader._model, null);
                    try
                    {
                        AppendExtensionField(ref writeState);
                        writeState.Close();
                    }
                    catch
                    {
                        writeState.Abandon();
                        throw;
                    }
                    finally
                    {
                        writeState.Dispose();
                    }

                    commit = true;
                }
                finally { extn.EndAppend(dest, commit); }
            }

            /// <summary>
            /// Indicates the underlying proto serialization format on the wire.
            /// </summary>
            public WireType WireType
            {
                [MethodImpl(HotPath)]
                get => _reader.WireType;
            }

            /// <summary>
            /// Gets / sets a flag indicating whether strings should be checked for repetition; if
            /// true, any repeated UTF-8 byte sequence will result in the same String instance, rather
            /// than a second instance of the same string. Disabled by default. Note that this uses
            /// a <i>custom</i> interner - the system-wide string interner is not used.
            /// </summary>
            public bool InternStrings
            {
                get => _reader.InternStrings;
                set => _reader.InternStrings = value;
            }

            /// <summary>
            /// Gets the number of the field being processed.
            /// </summary>
            public int FieldNumber
            {
                [MethodImpl(HotPath)]
                get => _reader._fieldNumber;
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            private void AppendExtensionField(ref ProtoWriter.State writeState)
            {
                //TODO: replace this with stream-based, buffered raw copying
                var reader = _reader;
                writeState.WriteFieldHeader(reader._fieldNumber, reader.WireType);
                switch (reader.WireType)
                {
                    case WireType.Fixed32:
                        writeState.WriteInt32(ReadInt32());
                        return;
                    case WireType.Varint:
                    case WireType.SignedVarint:
                    case WireType.Fixed64:
                        writeState.WriteInt64(ReadInt64());
                        return;
                    case WireType.String:
                        writeState.WriteBytes(AppendBytes(null));
                        return;
                    case WireType.StartGroup:
                        SubItemToken readerToken = StartSubItem(),
#pragma warning disable CS0618 // fine for groups
                            writerToken = writeState.StartSubItem(null);
                        while (ReadFieldHeader() > 0) { AppendExtensionField(ref writeState); }
                        EndSubItem(readerToken);
                        writeState.EndSubItem(writerToken);
#pragma warning restore CS0618 // fine for groups
                        return;
                    case WireType.None: // treat as explicit errorr
                    case WireType.EndGroup: // treat as explicit error
                    default: // treat as implicit error
                        ThrowWireTypeException();
                        break;
                }
            }

            /// <summary>
            /// Reads a Type from the stream, using the model's DynamicTypeFormatting if appropriate; supported wire-types: String
            /// </summary>
            [MethodImpl(HotPath)]
            public Type ReadType() => TypeModel.DeserializeType(_reader._model, ReadString());

            /// <summary>
            /// Reads a sub-item from the input reader
            /// </summary>
            [MethodImpl(HotPath)]
            public T ReadMessage<[DynamicallyAccessedMembers(DynamicAccess.ContractType)] T>(T value = default)
                => ReadMessage<T>(default, value, null);

            /// <summary>
            /// Reads a sub-item from the input reader
            /// </summary>
            [MethodImpl(ProtoReader.HotPath)]
            public T ReadMessage<[DynamicallyAccessedMembers(DynamicAccess.ContractType)] T>(SerializerFeatures features, T value = default, ISerializer<T> serializer = null)
                => ReadMessage<ISerializer<T>, T>(features, value, serializer ?? TypeModel.GetSerializer<T>(Model));

#pragma warning disable IDE0060 // unused (yet!) features arg
            /// <summary>
            /// Reads a sub-item from the input reader
            /// </summary>
            [MethodImpl(MethodImplOptions.NoInlining)]
            internal T ReadMessage<TSerializer, [DynamicallyAccessedMembers(DynamicAccess.ContractType)] T>(SerializerFeatures features, T value, in TSerializer serializer)
                where TSerializer : ISerializer<T>
#pragma warning restore IDE0060
            {
                var tok = StartSubItem();
                var result = serializer.Read(ref this, value);
                EndSubItem(tok);

                //if (TypeHelper<T>.IsReferenceType && (features & SerializerFeatures.OptionReturnNothingWhenUnchanged) != 0
                //    && (object)result == origRef)
                //{
                //    return default;
                //}

                return result;
            }

            /// <summary>
            /// Reads a value or sub-item from the input reader
            /// </summary>
            [MethodImpl(HotPath)]
            public T ReadAny<[DynamicallyAccessedMembers(DynamicAccess.ContractType)] T>(T value = default)
                => ReadAny<T>(default, value, null);

            /// <summary>
            /// Reads a value or sub-item from the input reader
            /// </summary>
            [MethodImpl(HotPath)]
            public T ReadAny<[DynamicallyAccessedMembers(DynamicAccess.ContractType)] T>(SerializerFeatures features, T value = default, ISerializer<T> serializer = null)
            {
                serializer ??= TypeModel.GetSerializer<T>(Model);
                var serializerFeatures = serializer.Features;
                features.InheritFrom(serializerFeatures);
                switch (serializerFeatures.GetCategory())
                {
                    case SerializerFeatures.CategoryMessage:
                    case SerializerFeatures.CategoryMessageWrappedAtRoot:
                        return ReadMessage<T>(features, value, serializer);
                    case SerializerFeatures.CategoryRepeated:
                        return ((IRepeatedSerializer<T>)serializer).ReadRepeated(ref this, features, value);
                    case SerializerFeatures.CategoryScalar:
                        features.HintIfNeeded(ref this);
                        return serializer.Read(ref this, value);
                    default:
                        features.ThrowInvalidCategory();
                        return default;
                }
            }

            internal TypeModel Model
            {
                [MethodImpl(HotPath)]
                get => _reader?.Model;
                private set => _reader.Model = value;
            }

            /// <summary>
            /// Gets the serializer associated with a specific type
            /// </summary>
            [MethodImpl(HotPath)]
            public ISerializer<T> GetSerializer<[DynamicallyAccessedMembers(DynamicAccess.ContractType)] T>() => TypeModel.GetSerializer<T>(Model);

            /// <summary>
            /// Reads a sub-item from the input reader
            /// </summary>
            [MethodImpl(MethodImplOptions.NoInlining)]
            public T ReadBaseType<[DynamicallyAccessedMembers(DynamicAccess.ContractType)] TBaseType, [DynamicallyAccessedMembers(DynamicAccess.ContractType)] T>(T value = null, ISubTypeSerializer<TBaseType> serializer = null)
                where TBaseType : class
                where T : class, TBaseType
            {
                return (T)(serializer ?? TypeModel.GetSubTypeSerializer<TBaseType>(_reader._model)).ReadSubType(ref this, SubTypeState<TBaseType>.Create<T>(_reader, value));
            }

            /// <summary>
            /// Deserialize an instance of the provided type
            /// </summary>
            [MethodImpl(MethodImplOptions.NoInlining)]
            public T DeserializeRoot<[DynamicallyAccessedMembers(DynamicAccess.ContractType)] T>(T value = default, ISerializer<T> serializer = null)
            {
                value = ReadAsRoot<T>(value, serializer ?? TypeModel.GetSerializer<T>(Model));
                CheckFullyConsumed();
                return value;
            }

            internal T ReadAsRoot<[DynamicallyAccessedMembers(DynamicAccess.ContractType)] T>(T value, ISerializer<T> serializer)
            {
                var features = serializer.Features;
                var category = features.GetCategory();

                switch (category)
                {
                    case SerializerFeatures.CategoryMessageWrappedAtRoot:
                        // to preserve legacy behavior of DateTime/TimeSpan etc
                        return ReadFieldOne(ref this, features, value, serializer);
                    case SerializerFeatures.CategoryMessage:
#if FEAT_DYNAMIC_REF
                    if (TypeHelper<T>.IsReferenceType && value is object)
                        SetRootObject(value);
#endif
                        return serializer.Read(ref this, value);
                    case SerializerFeatures.CategoryRepeated:
                    case SerializerFeatures.CategoryScalar:
                        return ReadFieldOne(ref this, features, value, serializer);
                    default:
                        features.ThrowInvalidCategory();
                        return default;

                }

                static T ReadFieldOne(ref State state, SerializerFeatures features, T value, ISerializer<T> serializer)
                {
                    int field;
                    bool found = false;
                    while ((field = state.ReadFieldHeader()) > 0)
                    {
                        if (field == 1)
                        {
                            found = true;
                            value = state.ReadAny<T>(features, value, serializer);
                        }
                        else
                        {
                            state.SkipField();
                        }
                    }
                    if (TypeHelper<T>.IsReferenceType && !found && value is null)
                    {
                        value = state.CreateInstance<T>(serializer);
                    }
                    return value;
                }
            }

            //[MethodImpl(HotPath)]
            //internal T DeserializeRaw<T>(T value = default, ISerializer<T> serializer = null)
            //    => (serializer ?? TypeModel.GetSerializer<T>(Model)).Read(ref this, value);

            /// <summary>
            /// Gets the serialization context associated with this instance;
            /// </summary>
            public ISerializationContext Context
            {
                [MethodImpl(HotPath)]
                get => _reader;
            }

            /// <summary>
            /// Indicates whether the reader still has data remaining in the current sub-item,
            /// additionally setting the wire-type for the next field if there is more data.
            /// This is used when decoding packed data.
            /// </summary>
            [MethodImpl(HotPath)]
            public bool HasSubValue(WireType wireType) => ProtoReader.HasSubValue(wireType, _reader);

            /// <summary>
            /// Create an instance of the provided type, respecting any custom factory rules
            /// </summary>
            [MethodImpl(MethodImplOptions.NoInlining)]
            public T CreateInstance<[DynamicallyAccessedMembers(DynamicAccess.ContractType)] T>(ISerializer<T> serializer = null)
            {
                var obj = TypeModel.CreateInstance<T>(Context, serializer);
#if FEAT_DYNAMIC_REF
                if (TypeHelper<T>.IsReferenceType) NoteObject(obj);
#endif
                return obj;
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            internal object DeserializeRootFallbackWithModel(object value, Type type, TypeModel overrideModel)
            {
                var oldModel = Model;
                try
                {
                    Model = overrideModel;
                    return DeserializeRootFallback(value, type);
                }
                finally
                {
                    Model = oldModel;
                }
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            internal object DeserializeRootFallback(object value, Type type)
            {
                bool autoCreate = TypeModel.PrepareDeserialize(value, ref type);
#if FEAT_DYNAMIC_REF
                if (value is object) _reader.SetRootObject(value);
#endif
                object obj = Model.DeserializeRootAny(ref this, type, value, autoCreate);
                CheckFullyConsumed();
                return obj;
            }

            [MethodImpl(HotPath)]
            internal T DeserializeRootImpl<T>(T value = default)
            {
                var serializer = TypeModel.TryGetSerializer<T>(Model);
                if (serializer is null)
                {
                    return (T)DeserializeRootFallback(value, typeof(T));
                }
                else
                {
                    return DeserializeRoot<T>(value, serializer);
                }
            }

#if FEAT_DYNAMIC_REF
            /// <summary>
            /// Utility method, not intended for public use; this helps maintain the root object is complex scenarios
            /// </summary>
            [MethodImpl(HotPath)]
            [Browsable(false), EditorBrowsable(EditorBrowsableState.Never)]
            public void NoteObject(object value) => ProtoReader.NoteObject(value, _reader);
#endif
        }
    }
}
