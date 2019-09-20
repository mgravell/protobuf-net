﻿using System;
using System.Diagnostics;

namespace ProtoBuf.Serializers
{
    internal sealed class BooleanSerializer : IRuntimeProtoSerializerNode
    {
        private static readonly Type expectedType = typeof(bool);

        public Type ExpectedType => expectedType;

        public void Write(ProtoWriter dest, ref ProtoWriter.State state, object value)
        {
            ProtoWriter.WriteBoolean((bool)value, dest, ref state);
        }

        public object Read(ProtoReader source, ref ProtoReader.State state, object value)
        {
            Debug.Assert(value == null); // since replaces
            return state.ReadBoolean();
        }

        bool IRuntimeProtoSerializerNode.RequiresOldValue => false;

        bool IRuntimeProtoSerializerNode.ReturnsValue => true;

        void IRuntimeProtoSerializerNode.EmitWrite(Compiler.CompilerContext ctx, Compiler.Local valueFrom)
        {
            ctx.EmitBasicWrite("WriteBoolean", valueFrom, this);
        }
        void IRuntimeProtoSerializerNode.EmitRead(Compiler.CompilerContext ctx, Compiler.Local entity)
        {
            ctx.EmitStateBasedRead(nameof(ProtoReader.State.ReadBoolean), ExpectedType);
        }
    }
}