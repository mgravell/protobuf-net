﻿using System;
using System.Diagnostics;

namespace ProtoBuf.Serializers
{
    internal sealed class StringSerializer : IRuntimeProtoSerializerNode
    {
        private static readonly Type expectedType = typeof(string);

        public Type ExpectedType => expectedType;

        public void Write(ProtoWriter dest, ref ProtoWriter.State state, object value)
        {
            ProtoWriter.WriteString((string)value, dest, ref state);
        }
        bool IRuntimeProtoSerializerNode.RequiresOldValue => false;

        bool IRuntimeProtoSerializerNode.ReturnsValue => true;

        public object Read(ProtoReader source, ref ProtoReader.State state, object value)
        {
            Debug.Assert(value == null); // since replaces
            return source.ReadString(ref state);
        }

        void IRuntimeProtoSerializerNode.EmitWrite(Compiler.CompilerContext ctx, Compiler.Local valueFrom)
        {
            ctx.EmitBasicWrite("WriteString", valueFrom, this);
        }
        void IRuntimeProtoSerializerNode.EmitRead(Compiler.CompilerContext ctx, Compiler.Local entity)
        {
            ctx.EmitBasicRead("ReadString", ExpectedType);
        }
    }
}