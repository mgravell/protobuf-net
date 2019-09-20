﻿#if !NO_INTERNAL_CONTEXT
using System;
using System.Collections.Generic;
using System.Text;
using ProtoBuf.Compiler;
using Xunit;
using ProtoBuf.unittest.Serializers;

namespace ProtoBuf.Serializers
{
    public class NilTests
    {
        [Fact]
        public void NilShouldAddNothing() {
            Util.Test("123", nil => nil, "");
        }
    }
    internal sealed class NilSerializer : IRuntimeProtoSerializerNode
    {
        private readonly Type type;
        public bool ReturnsValue { get { return true; } }
        public bool RequiresOldValue { get { return true; } }
        public object Read(ref ProtoReader.State state, object value) { return value; }
        Type IRuntimeProtoSerializerNode.ExpectedType { get { return type; } }
        public NilSerializer(Type type) { this.type = type; }
        void IRuntimeProtoSerializerNode.Write(ProtoWriter dest, ref ProtoWriter.State state, object value) { }

        void IRuntimeProtoSerializerNode.EmitWrite(CompilerContext ctx, Local valueFrom)
        {
            // burn the value off the stack if needed (creates a variable and does a stloc)
            using (Local tmp = ctx.GetLocalWithValue(type, valueFrom)) { }
        }
        void IRuntimeProtoSerializerNode.EmitRead(CompilerContext ctx, Local entity)
        {
            throw new NotSupportedException();
        }
    }
}
#endif