﻿using System;
using System.Diagnostics;

namespace ProtoBuf.Serializers
{
    internal sealed class SByteSerializer : IRuntimeProtoSerializerNode
    {
        private static readonly Type expectedType = typeof(sbyte);

        public Type ExpectedType => expectedType;

        bool IRuntimeProtoSerializerNode.RequiresOldValue => false;

        bool IRuntimeProtoSerializerNode.ReturnsValue => true;

        public object Read(ref ProtoReader.State state, object value)
        {
            Debug.Assert(value == null); // since replaces
            return state.ReadSByte();
        }

        public void Write(ProtoWriter dest, ref ProtoWriter.State state, object value)
        {
            state.WriteSByte((sbyte)value);
        }

        void IRuntimeProtoSerializerNode.EmitWrite(Compiler.CompilerContext ctx, Compiler.Local valueFrom)
        {
            ctx.EmitStateBasedWrite(nameof(ProtoWriter.State.WriteSByte), valueFrom);
        }
        void IRuntimeProtoSerializerNode.EmitRead(Compiler.CompilerContext ctx, Compiler.Local entity)
        {
            ctx.EmitStateBasedRead(nameof(ProtoReader.State.ReadSByte), ExpectedType);
        }
    }
}