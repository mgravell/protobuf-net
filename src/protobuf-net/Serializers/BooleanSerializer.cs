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
            state.WriteBoolean((bool)value);
        }

        public object Read(ref ProtoReader.State state, object value)
        {
            Debug.Assert(value == null); // since replaces
            return state.ReadBoolean();
        }

        bool IRuntimeProtoSerializerNode.RequiresOldValue => false;

        bool IRuntimeProtoSerializerNode.ReturnsValue => true;

        void IRuntimeProtoSerializerNode.EmitWrite(Compiler.CompilerContext ctx, Compiler.Local valueFrom)
        {
            ctx.EmitStateBasedWrite(nameof(ProtoWriter.State.WriteBoolean), valueFrom);
        }
        void IRuntimeProtoSerializerNode.EmitRead(Compiler.CompilerContext ctx, Compiler.Local entity)
        {
            ctx.EmitStateBasedRead(nameof(ProtoReader.State.ReadBoolean), ExpectedType);
        }
    }
}