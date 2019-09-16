﻿using System;

namespace ProtoBuf.Serializers
{
    internal sealed class SByteSerializer : IRuntimeProtoSerializerNode
    {
        private static readonly Type expectedType = typeof(sbyte);

        public Type ExpectedType => expectedType;

        bool IRuntimeProtoSerializerNode.RequiresOldValue => false;

        bool IRuntimeProtoSerializerNode.ReturnsValue => true;

        public object Read(ProtoReader source, ref ProtoReader.State state, object value)
        {
            Helpers.DebugAssert(value == null); // since replaces
            return source.ReadSByte(ref state);
        }

        public void Write(ProtoWriter dest, ref ProtoWriter.State state, object value)
        {
            ProtoWriter.WriteSByte((sbyte)value, dest, ref state);
        }

        void IRuntimeProtoSerializerNode.EmitWrite(Compiler.CompilerContext ctx, Compiler.Local valueFrom)
        {
            ctx.EmitBasicWrite("WriteSByte", valueFrom, this);
        }
        void IRuntimeProtoSerializerNode.EmitRead(Compiler.CompilerContext ctx, Compiler.Local entity)
        {
            ctx.EmitBasicRead("ReadSByte", ExpectedType);
        }
    }
}