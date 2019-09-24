﻿using System;
using System.Diagnostics;

namespace ProtoBuf.Serializers
{
    internal sealed class UInt32Serializer : IRuntimeProtoSerializerNode
    {
        private static readonly Type expectedType = typeof(uint);

        public Type ExpectedType => expectedType;

        bool IRuntimeProtoSerializerNode.RequiresOldValue => false;

        bool IRuntimeProtoSerializerNode.ReturnsValue => true;

        public object Read(ref ProtoReader.State state, object value)
        {
            Debug.Assert(value == null); // since replaces
            return state.ReadUInt32();
        }

        public void Write(ref ProtoWriter.State state, object value)
        {
            state.WriteUInt32((uint)value);
        }

        void IRuntimeProtoSerializerNode.EmitWrite(Compiler.CompilerContext ctx, Compiler.Local valueFrom)
        {
            ctx.EmitStateBasedWrite(nameof(ProtoWriter.State.WriteUInt32), valueFrom);
        }
        void IRuntimeProtoSerializerNode.EmitRead(Compiler.CompilerContext ctx, Compiler.Local entity)
        {
            ctx.EmitStateBasedRead(nameof(ProtoReader.State.ReadUInt32), typeof(uint));
        }
    }
}