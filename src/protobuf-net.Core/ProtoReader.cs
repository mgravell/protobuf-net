
using ProtoBuf.Internal;
using ProtoBuf.Meta;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace ProtoBuf
{
    /// <summary>
    /// A stateful reader, used to read a protobuf stream. Typical usage would be (sequentially) to call
    /// ReadFieldHeader and (after matching the field) an appropriate Read* method.
    /// </summary>
    public abstract partial class ProtoReader : IDisposable, ISerializationContext
    {
        internal const string PreferStateAPI = "If possible, please use the State API; a transitionary implementation is provided, but this API may be removed in a future version",
            PreferReadMessage = "If possible, please use the ReadMessage API; this API may not work correctly with all readers";

        private protected abstract int ImplTryReadUInt64VarintWithoutMoving(ref State state, out ulong value);
        private protected abstract uint ImplReadUInt32Fixed(ref State state);
        private protected abstract ulong ImplReadUInt64Fixed(ref State state);
        private protected abstract string ImplReadString(ref State state, int bytes);
        private protected abstract void ImplSkipBytes(ref State state, long count);
        private protected abstract int ImplTryReadUInt32VarintWithoutMoving(ref State state, Read32VarintMode mode, out uint value);
        private protected abstract void ImplReadBytes(ref State state, Span<byte> target);
        private protected virtual void ImplReadBytes(ref State state, ReadOnlySequence<byte> target)
        {
            if (target.IsSingleSegment)
            {
                ImplReadBytes(ref state, MemoryMarshal.AsMemory(target.First).Span);
            }
            else
            {
                foreach (var segment in target)
                {
                    ImplReadBytes(ref state, MemoryMarshal.AsMemory(segment).Span);
                }
            }
        }

        private protected abstract bool IsFullyConsumed(ref State state);

        private TypeModel _model;
        private int _fieldNumber, _depth;
        private long blockEnd64;
        private readonly NetObjectCache netCache = new NetObjectCache();

        /// <summary>
        /// Gets the number of the field being processed.
        /// </summary>
        public int FieldNumber
        {
            [MethodImpl(HotPath)]
            get => _fieldNumber;
        }

        /// <summary>
        /// Indicates the underlying proto serialization format on the wire.
        /// </summary>
        public WireType WireType
        {
            [MethodImpl(HotPath)]
            get;
            private protected set;
        }

        internal const long TO_EOF = -1;

        /// <summary>
        /// Gets / sets a flag indicating whether strings should be checked for repetition; if
        /// true, any repeated UTF-8 byte sequence will result in the same String instance, rather
        /// than a second instance of the same string. Disabled by default. Note that this uses
        /// a <i>custom</i> interner - the system-wide string interner is not used.
        /// </summary>
        public bool InternStrings { get; set; }

        private protected ProtoReader() { }

#if DEBUG
        int _usageCount;
        partial void OnDispose()
        {
            int count = System.Threading.Interlocked.Decrement(ref _usageCount);
            if (count != 0) ThrowHelper.ThrowInvalidOperationException($"Usage count - expected 0, was {count}");
        }
        partial void OnInit()
        {
            int count = System.Threading.Interlocked.Increment(ref _usageCount);
            if (count != 1) ThrowHelper.ThrowInvalidOperationException($"Usage count - expected 1, was {count}");
        }
#endif
        partial void OnDispose();
        partial void OnInit();

        /// <summary>
        /// Initialize the reader
        /// </summary>
        internal void Init(TypeModel model, object userState)
        {
            OnInit();
            _model = model;

            if (userState is SerializationContext context) context.Freeze();
            UserState = userState;
            _longPosition = 0;
            _depth = _fieldNumber = 0;

            blockEnd64 = long.MaxValue;
            InternStrings = model.HasOption(TypeModel.TypeModelOptions.InternStrings);
            WireType = WireType.None;
#if FEAT_DYNAMIC_REF
            trapCount = 1;
#endif
        }

        /// <summary>
        /// Addition information about this deserialization operation.
        /// </summary>
        public object UserState { get; private set; }

        /// <summary>
        /// Addition information about this deserialization operation.
        /// </summary>
        [Obsolete("Prefer " + nameof(UserState))]
        public SerializationContext Context => SerializationContext.AsSerializationContext(this);

        /// <summary>
        /// Releases resources used by the reader, but importantly <b>does not</b> Dispose the 
        /// underlying stream; in many typical use-cases the stream is used for different
        /// processes, so it is assumed that the consumer will Dispose their stream separately.
        /// </summary>
#pragma warning disable CA1816 // Dispose methods should call SuppressFinalize - no intention of supporting finalizers here
        public virtual void Dispose()
#pragma warning restore CA1816 // Dispose methods should call SuppressFinalize
        {
            OnDispose();
            _model = null;
            if (stringInterner is object)
            {
                stringInterner.Clear();
                stringInterner = null;
            }
            netCache.Clear();
        }

        private protected enum Read32VarintMode
        {
            Signed,
            Unsigned,
            FieldHeader,
        }

        /// <summary>
        /// Returns the position of the current reader (note that this is not necessarily the same as the position
        /// in the underlying stream, if multiple readers are used on the same stream)
        /// </summary>
        public int Position
        {
            [MethodImpl(HotPath)]
            get { return checked((int)_longPosition); }
        }


        /// <summary>
        /// Returns the position of the current reader (note that this is not necessarily the same as the position
        /// in the underlying stream, if multiple readers are used on the same stream)
        /// </summary>
        public long LongPosition
        {
            [MethodImpl(HotPath)]
            get => _longPosition;
        }

        private long _longPosition;

        [MethodImpl(HotPath)]
        internal void Advance(long count) => _longPosition += count;

        /// <summary>
        /// Reads a signed 16-bit integer from the stream: Variant, Fixed32, Fixed64, SignedVariant
        /// </summary>
        [MethodImpl(HotPath)]
        public short ReadInt16() => DefaultState().ReadInt16();


        /// <summary>
        /// Reads an unsigned 16-bit integer from the stream; supported wire-types: Variant, Fixed32, Fixed64
        /// </summary>
        [MethodImpl(HotPath)]
        public ushort ReadUInt16() => DefaultState().ReadUInt16();

        /// <summary>
        /// Reads an unsigned 8-bit integer from the stream; supported wire-types: Variant, Fixed32, Fixed64
        /// </summary>
        [MethodImpl(HotPath)]
        public byte ReadByte() => DefaultState().ReadByte();

        /// <summary>
        /// Reads a signed 8-bit integer from the stream; supported wire-types: Variant, Fixed32, Fixed64, SignedVariant
        /// </summary>
        [MethodImpl(HotPath)]
        public sbyte ReadSByte() => DefaultState().ReadSByte();

        /// <summary>
        /// Reads an unsigned 32-bit integer from the stream; supported wire-types: Variant, Fixed32, Fixed64, SignedVariant
        /// </summary>
        [MethodImpl(HotPath)]
        public uint ReadUInt32() => DefaultState().ReadUInt32();

        /// <summary>
        /// Reads a signed 32-bit integer from the stream; supported wire-types: Variant, Fixed32, Fixed64, SignedVariant
        /// </summary>
        [MethodImpl(HotPath)]
        public int ReadInt32() => DefaultState().ReadInt32();

        /// <summary>
        /// Reads a signed 64-bit integer from the stream; supported wire-types: Variant, Fixed32, Fixed64, SignedVariant
        /// </summary>
        [MethodImpl(HotPath)]
        public long ReadInt64() => DefaultState().ReadInt64();

        private Dictionary<string, string> stringInterner;
        private protected string Intern(string value)
        {
            if (value is null) return null;
            if (value.Length == 0) return "";
            if (stringInterner is null)
            {
                stringInterner = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    { value, value }
                };
            }
            else if (stringInterner.TryGetValue(value, out string found))
            {
                value = found;
            }
            else
            {
                stringInterner.Add(value, value);
            }
            return value;
        }

        private protected static readonly UTF8Encoding UTF8 = new UTF8Encoding();

        /// <summary>
        /// Reads a string from the stream (using UTF8); supported wire-types: String
        /// </summary>
        [MethodImpl(HotPath)]
        public string ReadString() => DefaultState().ReadString();

        /// <summary>
        /// Throws an exception indication that the given value cannot be mapped to an enum.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ThrowEnumException(Type type, int value) => DefaultState().ThrowEnumException(type, value);


        /// <summary>
        /// Reads a double-precision number from the stream; supported wire-types: Fixed32, Fixed64
        /// </summary>
        [MethodImpl(HotPath)]
        public double ReadDouble() => DefaultState().ReadDouble();



        /// <summary>
        /// Reads (merges) a sub-message from the stream, internally calling StartSubItem and EndSubItem, and (in between)
        /// parsing the message in accordance with the model associated with the reader
        /// </summary>
        [MethodImpl(HotPath)]
        public static object ReadObject(object value, [DynamicallyAccessedMembers(DynamicAccess.ContractType)] Type type, ProtoReader reader)
            => reader.DefaultState().ReadObject(value, type);

        /// <summary>
        /// Makes the end of consuming a nested message in the stream; the stream must be either at the correct EndGroup
        /// marker, or all fields of the sub-message must have been consumed (in either case, this means ReadFieldHeader
        /// should return zero)
        /// </summary>
        [MethodImpl(HotPath)]
        public static void EndSubItem(SubItemToken token, ProtoReader reader)
            => reader.DefaultState().EndSubItem(token);

        /// <summary>
        /// Begins consuming a nested message in the stream; supported wire-types: StartGroup, String
        /// </summary>
        /// <remarks>The token returned must be help and used when callining EndSubItem</remarks>
        [MethodImpl(HotPath)]
        public static SubItemToken StartSubItem(ProtoReader reader)
            => reader.DefaultState().StartSubItem();

        /// <summary>
        /// Reads a field header from the stream, setting the wire-type and retuning the field number. If no
        /// more fields are available, then 0 is returned. This methods respects sub-messages.
        /// </summary>
        [MethodImpl(HotPath)]
        public int ReadFieldHeader() => DefaultState().ReadFieldHeader();

        [MethodImpl(HotPath)]
        private int SetTag(uint tag)
        {
            if ((_fieldNumber = (int)(tag >> 3)) < 1) ThrowInvalidField();
            if ((WireType = (WireType)(tag & 7)) == WireType.EndGroup)
            {
                if (_depth > 0) return 0; // spoof an end, but note we still set the field-number
                ThrowUnexpectedEndGroup();
            }
            return _fieldNumber;
        }

        private bool AllowZeroPadding => UserState is ISerializationOptions options && (options.Options & SerializationOptions.AllowZeroPadding) != 0;

        private void ThrowInvalidField()
        {
            if (!(_fieldNumber == 0 && AllowZeroPadding))
            {
                ThrowHelper.ThrowProtoException("Invalid field in source data: " + _fieldNumber.ToString());
            }
        }
        private static void ThrowUnexpectedEndGroup()
            => ThrowHelper.ThrowProtoException("Unexpected end-group in source data; this usually means the source data is corrupt");

        /// <summary>
        /// Looks ahead to see whether the next field in the stream is what we expect
        /// (typically; what we've just finished reading - for example ot read successive list items)
        /// </summary>
        [MethodImpl(HotPath)]
        public bool TryReadFieldHeader(int field) => DefaultState().TryReadFieldHeader(field);

        /// <summary>
        /// Get the TypeModel associated with this reader
        /// </summary>
        public TypeModel Model
        {
            get => _model;
            internal set => _model = value;
        }

        /// <summary>
        /// Compares the streams current wire-type to the hinted wire-type, updating the reader if necessary; for example,
        /// a Variant may be updated to SignedVariant. If the hinted wire-type is unrelated then no change is made.
        /// </summary>
        [MethodImpl(HotPath)]
        public void Hint(WireType wireType)
        {
            if (WireType == wireType) { }  // fine; everything as we expect
            else if (((int)wireType & 7) == (int)this.WireType)
            {   // the underling type is a match; we're customising it with an extension
                WireType = wireType;
            }
            // note no error here; we're OK about using alternative data
        }

        /// <summary>
        /// Verifies that the stream's current wire-type is as expected, or a specialized sub-type (for example,
        /// SignedVariant) - in which case the current wire-type is updated. Otherwise an exception is thrown.
        /// </summary>
        [MethodImpl(HotPath)]
        public void Assert(WireType wireType) => DefaultState().Assert(wireType);

        /// <summary>
        /// Discards the data for the current field.
        /// </summary>
        [MethodImpl(HotPath)]
        public void SkipField() => DefaultState().SkipField();

        /// <summary>
        /// Reads an unsigned 64-bit integer from the stream; supported wire-types: Variant, Fixed32, Fixed64
        /// </summary>
        [MethodImpl(HotPath)]
        public ulong ReadUInt64() => DefaultState().ReadUInt64();

        /// <summary>
        /// Reads a single-precision number from the stream; supported wire-types: Fixed32, Fixed64
        /// </summary>
        [MethodImpl(HotPath)]
        public float ReadSingle() => DefaultState().ReadSingle();

        /// <summary>
        /// Reads a boolean value from the stream; supported wire-types: Variant, Fixed32, Fixed64
        /// </summary>
        [MethodImpl(HotPath)]
        public bool ReadBoolean() => DefaultState().ReadBoolean();

        internal static readonly byte[] EmptyBlob = Array.Empty<byte>();

        /// <summary>
        /// Reads a byte-sequence from the stream, appending them to an existing byte-sequence (which can be null); supported wire-types: String
        /// </summary>
        [MethodImpl(HotPath)]
        public static byte[] AppendBytes(byte[] value, ProtoReader reader)
            => reader.DefaultState().AppendBytes(value);

        //static byte[] ReadBytes(Stream stream, int length)
        //{
        //    if (stream is null) ThrowHelper.ThrowArgumentNullException("stream");
        //    if (length < 0) ThrowHelper.ThrowArgumentOutOfRangeException("length");
        //    byte[] buffer = new byte[length];
        //    int offset = 0, read;
        //    while (length > 0 && (read = stream.Read(buffer, offset, length)) > 0)
        //    {
        //        length -= read;
        //    }
        //    if (length > 0) ThrowEoF();
        //    return buffer;
        //}

        private static int ReadByteOrThrow(Stream source)
        {
            int val = source.ReadByte();
            if (val < 0) ThrowEoF();
            return val;
        }

        /// <summary>
        /// Reads the length-prefix of a message from a stream without buffering additional data, allowing a fixed-length
        /// reader to be created.
        /// </summary>
        public static int ReadLengthPrefix(Stream source, bool expectHeader, PrefixStyle style, out int fieldNumber)
            => ReadLengthPrefix(source, expectHeader, style, out fieldNumber, out int _);

        /// <summary>
        /// Reads a little-endian encoded integer. An exception is thrown if the data is not all available.
        /// </summary>
        public static int DirectReadLittleEndianInt32(Stream source)
        {
            return ReadByteOrThrow(source)
                | (ReadByteOrThrow(source) << 8)
                | (ReadByteOrThrow(source) << 16)
                | (ReadByteOrThrow(source) << 24);
        }

        /// <summary>
        /// Reads a big-endian encoded integer. An exception is thrown if the data is not all available.
        /// </summary>
        public static int DirectReadBigEndianInt32(Stream source)
        {
            return (ReadByteOrThrow(source) << 24)
                 | (ReadByteOrThrow(source) << 16)
                 | (ReadByteOrThrow(source) << 8)
                 | ReadByteOrThrow(source);
        }

        /// <summary>
        /// Reads a varint encoded integer. An exception is thrown if the data is not all available.
        /// </summary>
        public static int DirectReadVarintInt32(Stream source)
        {
            int bytes = TryReadUInt64Varint(source, out ulong val);
            if (bytes <= 0) ThrowEoF();
            return checked((int)val);
        }

        /// <summary>
        /// Reads a string (of a given lenth, in bytes) directly from the source into a pre-existing buffer. An exception is thrown if the data is not all available.
        /// </summary>
        public static void DirectReadBytes(Stream source, byte[] buffer, int offset, int count)
        {
            int read;
            if (source is null) ThrowHelper.ThrowArgumentNullException(nameof(source));
            while (count > 0 && (read = source.Read(buffer, offset, count)) > 0)
            {
                count -= read;
                offset += read;
            }
            if (count > 0) ThrowEoF();
        }

        /// <summary>
        /// Reads a given number of bytes directly from the source. An exception is thrown if the data is not all available.
        /// </summary>
        public static byte[] DirectReadBytes(Stream source, int count)
        {
            byte[] buffer = new byte[count];
            DirectReadBytes(source, buffer, 0, count);
            return buffer;
        }

        /// <summary>
        /// Reads a string (of a given lenth, in bytes) directly from the source. An exception is thrown if the data is not all available.
        /// </summary>
        public static string DirectReadString(Stream source, int length)
        {
            byte[] buffer = new byte[length];
            DirectReadBytes(source, buffer, 0, length);
            return ProtoWriter.UTF8.GetString(buffer, 0, length);
        }

        /// <summary>
        /// Reads the length-prefix of a message from a stream without buffering additional data, allowing a fixed-length
        /// reader to be created.
        /// </summary>
        public static int ReadLengthPrefix(Stream source, bool expectHeader, PrefixStyle style, out int fieldNumber, out int bytesRead)
        {
            if (style == PrefixStyle.None)
            {
                bytesRead = fieldNumber = 0;
                return int.MaxValue; // avoid the long.maxvalue causing overflow
            }
            long len64 = ReadLongLengthPrefix(source, expectHeader, style, out fieldNumber, out bytesRead);
            return checked((int)len64);
        }

        /// <summary>
        /// Reads the length-prefix of a message from a stream without buffering additional data, allowing a fixed-length
        /// reader to be created.
        /// </summary>
        public static long ReadLongLengthPrefix(Stream source, bool expectHeader, PrefixStyle style, out int fieldNumber, out int bytesRead)
        {
            fieldNumber = 0;
            switch (style)
            {
                case PrefixStyle.None:
                    bytesRead = 0;
                    return long.MaxValue;
                case PrefixStyle.Base128:
                    ulong val;
                    int tmpBytesRead;
                    bytesRead = 0;
                    if (expectHeader)
                    {
                        tmpBytesRead = ProtoReader.TryReadUInt64Varint(source, out val);
                        bytesRead += tmpBytesRead;
                        if (tmpBytesRead > 0)
                        {
                            if ((val & 7) != (uint)WireType.String)
                            { // got a header, but it isn't a string
                                ThrowHelper.ThrowInvalidOperationException($"Unexpected wire-type: {(WireType)(val & 7)}, expected {WireType.String})");
                            }
                            fieldNumber = (int)(val >> 3);
                            tmpBytesRead = ProtoReader.TryReadUInt64Varint(source, out val);
                            bytesRead += tmpBytesRead;
                            if (bytesRead == 0) ThrowEoF(); // got a header, but no length
                            return (long)val;
                        }
                        else
                        { // no header
                            bytesRead = 0;
                            return -1;
                        }
                    }
                    // check for a length
                    tmpBytesRead = ProtoReader.TryReadUInt64Varint(source, out val);
                    bytesRead += tmpBytesRead;
                    return bytesRead < 0 ? -1 : (long)val;

                case PrefixStyle.Fixed32:
                    {
                        int b = source.ReadByte();
                        if (b < 0)
                        {
                            bytesRead = 0;
                            return -1;
                        }
                        bytesRead = 4;
                        return b
                             | (ReadByteOrThrow(source) << 8)
                             | (ReadByteOrThrow(source) << 16)
                             | (ReadByteOrThrow(source) << 24);
                    }
                case PrefixStyle.Fixed32BigEndian:
                    {
                        int b = source.ReadByte();
                        if (b < 0)
                        {
                            bytesRead = 0;
                            return -1;
                        }
                        bytesRead = 4;
                        return (b << 24)
                            | (ReadByteOrThrow(source) << 16)
                            | (ReadByteOrThrow(source) << 8)
                            | ReadByteOrThrow(source);
                    }
                default:
                    ThrowHelper.ThrowArgumentOutOfRangeException(nameof(style));
                    bytesRead = default;
                    return default;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowEoF() => default(State).ThrowEoF();

        /// <summary>Read a varint if possible</summary>
        /// <returns>The number of bytes consumed; 0 if no data available</returns>
        private static int TryReadUInt64Varint(Stream source, out ulong value)
        {
            value = 0;
            int b = source.ReadByte();
            if (b < 0) { return 0; }
            value = (uint)b;
            if ((value & 0x80) == 0) { return 1; }
            value &= 0x7F;
            int bytesRead = 1, shift = 7;
            while (bytesRead < 9)
            {
                b = source.ReadByte();
                if (b < 0) ThrowEoF();
                value |= ((ulong)b & 0x7F) << shift;
                shift += 7;
                bytesRead++;

                if ((b & 0x80) == 0) return bytesRead;
            }
            b = source.ReadByte();
            if (b < 0) ThrowEoF();
            if ((b & 1) == 0) // only use 1 bit from the last byte
            {
                value |= ((ulong)b & 0x7F) << shift;
                return ++bytesRead;
            }
            ThrowHelper.ThrowOverflowException();
            return default;
        }

        internal static void Seek(Stream source, long count, byte[] buffer)
        {
            if (source.CanSeek)
            {
                source.Seek(count, SeekOrigin.Current);
                count = 0;
            }
            else if (buffer is object)
            {
                int bytesRead;
                while (count > buffer.Length && (bytesRead = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    count -= bytesRead;
                }
                while (count > 0 && (bytesRead = source.Read(buffer, 0, (int)count)) > 0)
                {
                    count -= bytesRead;
                }
            }
            else // borrow a buffer
            {
                buffer = BufferPool.GetBuffer();
                try
                {
                    int bytesRead;
                    while (count > buffer.Length && (bytesRead = source.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        count -= bytesRead;
                    }
                    while (count > 0 && (bytesRead = source.Read(buffer, 0, (int)count)) > 0)
                    {
                        count -= bytesRead;
                    }
                }
                finally
                {
                    BufferPool.ReleaseBufferToPool(ref buffer);
                }
            }
            if (count > 0) ThrowEoF();
        }

        /// <summary>
        /// Copies the current field into the instance as extension data
        /// </summary>
        [MethodImpl(HotPath)]
        public void AppendExtensionData(IExtensible instance) => DefaultState().AppendExtensionData(instance);

        /// <summary>
        /// Indicates whether the reader still has data remaining in the current sub-item,
        /// additionally setting the wire-type for the next field if there is more data.
        /// This is used when decoding packed data.
        /// </summary>
        public static bool HasSubValue(ProtoBuf.WireType wireType, ProtoReader source)
        {
            if (source is null) ThrowHelper.ThrowArgumentNullException(nameof(source));
            // check for virtual end of stream
            if (source.blockEnd64 <= source._longPosition || wireType == WireType.EndGroup) { return false; }
            source.WireType = wireType;
            return true;
        }

        internal Type DeserializeType(string value)
        {
            return TypeModel.DeserializeType(_model, value);
        }

#if FEAT_DYNAMIC_REF
        internal void SetRootObject(object value)
        {
            netCache.SetKeyedObject(NetObjectCache.Root, value);
            trapCount--;
        }

        internal object GetKeyedObject(int key)
        {
            if (!(this is StreamProtoReader)) ThrowHelper.ThrowTrackedObjects(this);
            return netCache.GetKeyedObject(key);
        }

        internal void SetKeyedObject(int key, object value)
        {
            if (!(this is StreamProtoReader)) ThrowHelper.ThrowTrackedObjects(this); 
            netCache.SetKeyedObject(key, value);
        }

        /// <summary>
        /// Utility method, not intended for public use; this helps maintain the root object is complex scenarios
        /// </summary>
        public static void NoteObject(object value, ProtoReader reader)
        {
            if (reader is null) ThrowHelper.ThrowArgumentNullException(nameof(reader));
            if (reader.trapCount != 0)
            {
                reader.netCache.RegisterTrappedObject(value);
                reader.trapCount--;
            }
        }

        internal void TrapNextObject(int newObjectKey)
        {
            if (!(this is StreamProtoReader)) ThrowHelper.ThrowTrackedObjects(this);
            trapCount++;
            netCache.SetKeyedObject(newObjectKey, null); // use null as a temp
        }

        // this is how many outstanding objects do not currently have
        // values for the purposes of reference tracking; we'll default
        // to just trapping the root object
        // note: objects are trapped (the ref and key mapped) via NoteObject
        private uint trapCount; // uint is so we can use beq/bne more efficiently than bgt
#endif

        /// <summary>
        /// Reads a Type from the stream, using the model's DynamicTypeFormatting if appropriate; supported wire-types: String
        /// </summary>
        public Type ReadType() => DefaultState().ReadType();

        /// <summary>
        /// Merge two objects using the details from the current reader; this is used to change the type
        /// of objects when an inheritance relationship is discovered later than usual during deserilazation.
        /// </summary>
        public static object Merge(ProtoReader parent, object from, object to)
        {
            if (parent is null) ThrowHelper.ThrowArgumentNullException(nameof(parent));
            TypeModel model = parent.Model;
            var userState = parent.UserState;
            if (model is null) ThrowHelper.ThrowInvalidOperationException("Types cannot be merged unless a type-model has been specified");
            using var ms = new MemoryStream();
            var writeState = ProtoWriter.State.Create(ms, model, userState);
            try
            {
                model.SerializeRootFallback(ref writeState, from);
            }
            finally
            {
                writeState.Dispose();
            }
            ms.Position = 0;
            using var state = ProtoReader.State.Create(ms, model, userState);
            return state.DeserializeRootFallback(to, type: null);
        }
    }
}
