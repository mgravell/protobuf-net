﻿using System;
using System.Diagnostics;

namespace ProtoBuf.Serializers
{
    internal class UInt16Serializer : IRuntimeProtoSerializerNode
    {
        private static readonly Type expectedType = typeof(ushort);

        public virtual Type ExpectedType => expectedType;

        bool IRuntimeProtoSerializerNode.RequiresOldValue => false;

        bool IRuntimeProtoSerializerNode.ReturnsValue => true;

        public virtual object Read(ref ProtoReader.State state, object value)
        {
            Debug.Assert(value == null); // since replaces
            return state.ReadUInt16();
        }

        public virtual void Write(ref ProtoWriter.State state, object value)
        {
            state.WriteUInt16((ushort)value);
        }

        void IRuntimeProtoSerializerNode.EmitWrite(Compiler.CompilerContext ctx, Compiler.Local valueFrom)
        {
            ctx.EmitStateBasedWrite(nameof(ProtoWriter.State.WriteUInt16), valueFrom);
        }
        void IRuntimeProtoSerializerNode.EmitRead(Compiler.CompilerContext ctx, Compiler.Local entity)
        {
            ctx.EmitStateBasedRead(nameof(ProtoReader.State.ReadUInt16), typeof(ushort));
        }
    }
}