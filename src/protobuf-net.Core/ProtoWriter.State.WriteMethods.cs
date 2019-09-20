﻿using ProtoBuf.Internal;
using System;
namespace ProtoBuf
{
    partial class ProtoWriter
    {
        ref partial struct State
        {
            /// <summary>
            /// Writes a string to the stream; supported wire-types: String
            /// </summary>
            public void WriteString(string value)
            {
                var writer = _writer;
                switch (writer.WireType)
                {
                    case WireType.String:
                        if (string.IsNullOrEmpty(value))
                        {
                            writer.AdvanceAndReset(writer.ImplWriteVarint32(ref this, 0));
                        }
                        else
                        {
                            var len = UTF8.GetByteCount(value);
                            writer.AdvanceAndReset(writer.ImplWriteVarint32(ref this, (uint)len) + len);
                            writer.ImplWriteString(ref this, value, len);
                        }
                        break;
                    default:
                        ThrowException(writer);
                        break;
                }
            }

            /// <summary>
            /// Writes a Type to the stream, using the model's DynamicTypeFormatting if appropriate; supported wire-types: String
            /// </summary>
            public void WriteType(Type value)
            {
                WriteString(_writer.SerializeType(value));
            }

            /// <summary>
            /// Writes a field-header, indicating the format of the next data we plan to write.
            /// </summary>
            public void WriteFieldHeader(int fieldNumber, WireType wireType)
            {
                var writer = _writer;
                if (writer.WireType != WireType.None)
                {
                    ThrowHelper.ThrowInvalidOperationException("Cannot write a " + wireType.ToString()
                    + " header until the " + writer.WireType.ToString() + " data has been written");
                }
                if (fieldNumber < 0) ThrowHelper.ThrowArgumentOutOfRangeException(nameof(fieldNumber));
#if DEBUG
            switch (wireType)
            {   // validate requested header-type
                case WireType.Fixed32:
                case WireType.Fixed64:
                case WireType.String:
                case WireType.StartGroup:
                case WireType.SignedVarint:
                case WireType.Varint:
                    break; // fine
                case WireType.None:
                case WireType.EndGroup:
                default:
                    ThrowHelper.ThrowArgumentException("Invalid wire-type: " + wireType.ToString(), nameof(wireType));
            }
#endif
                writer._needFlush = true;
                if (writer.packedFieldNumber == 0)
                {
                    writer.fieldNumber = fieldNumber;
                    writer.WireType = wireType;
                    WriteHeaderCore(fieldNumber, wireType, writer, ref this);
                }
                else if (writer.packedFieldNumber == fieldNumber)
                { // we'll set things up, but note we *don't* actually write the header here
                    switch (wireType)
                    {
                        case WireType.Fixed32:
                        case WireType.Fixed64:
                        case WireType.Varint:
                        case WireType.SignedVarint:
                            break; // fine
                        default:
                            ThrowHelper.ThrowInvalidOperationException("Wire-type cannot be encoded as packed: " + wireType.ToString());
                            break;
                    }
                    writer.fieldNumber = fieldNumber;
                    writer.WireType = wireType;
                }
                else
                {
                    ThrowHelper.ThrowInvalidOperationException("Field mismatch during packed encoding; expected " + writer.packedFieldNumber.ToString() + " but received " + fieldNumber.ToString());
                }
            }
        }
    }
}
