﻿#if !NO_INTERNAL_CONTEXT
using System.IO;
using Xunit;
using System.Reflection;
using ProtoBuf.Serializers;
using ProtoBuf.Compiler;

namespace ProtoBuf.unittest.Meta
{
    public struct CustomerStruct
    {
        public int Id { get; set; }
        public string Name;
    }

    public class TestCustomerStruct
    {
        [Fact]
        public void RunStructDesrializerForEmptyStream()
        {
            var model = ProtoBuf.Meta.RuntimeTypeModel.Create();
            var head = TypeSerializer.Create(typeof(CustomerStruct),
                new int[] { 1, 2 },
                new IRuntimeProtoSerializerNode[] {
                    new PropertyDecorator(typeof(CustomerStruct), typeof(CustomerStruct).GetProperty("Id"), new TagDecorator(1, WireType.Varint, false, PrimitiveSerializer<Int32Serializer>.Singleton)),
                    new FieldDecorator(typeof(CustomerStruct), typeof(CustomerStruct).GetField("Name"), new TagDecorator(2, WireType.String, false, PrimitiveSerializer<StringSerializer>.Singleton))
                }, null, false, true, null, null, null, null);
            var deser = CompilerContext.BuildDeserializer<CustomerStruct>(model.Scope, head, model);

            var state = ProtoReader.State.Create(Stream.Null, null, null);
            try
            {
                var result = deser(ref state, default);
                Assert.IsType<CustomerStruct>(result);
            }
            finally
            {
                state.Dispose();
            }

            state = ProtoReader.State.Create(Stream.Null, null, null);
            try
            {
                CustomerStruct before = new CustomerStruct { Id = 123, Name = "abc" };
                CustomerStruct after = (CustomerStruct)deser(ref state, before);
                Assert.Equal(before.Id, after.Id);
                Assert.Equal(before.Name, after.Name);
            }
            finally
            {
                state.Dispose();
            }
        }
        [Fact]
        public void GenerateTypeSerializer()
        {
            var model = ProtoBuf.Meta.RuntimeTypeModel.Create();
            var head = TypeSerializer.Create(typeof(CustomerStruct),
                new int[] { 1, 2 },
                new IRuntimeProtoSerializerNode[] {
                    new PropertyDecorator(typeof(CustomerStruct), typeof(CustomerStruct).GetProperty("Id"), new TagDecorator(1, WireType.Varint,false,  PrimitiveSerializer<Int32Serializer>.Singleton)),
                    new FieldDecorator(typeof(CustomerStruct), typeof(CustomerStruct).GetField("Name"), new TagDecorator(2, WireType.String,false,  PrimitiveSerializer<StringSerializer>.Singleton))
                }, null, false, true, null, null, null, null);
            var ser = CompilerContext.BuildSerializer<CustomerStruct>(model.Scope, head, model);
            var deser = CompilerContext.BuildDeserializer<CustomerStruct>(model.Scope, head, model);
            CustomerStruct cs1 = new CustomerStruct { Id = 123, Name = "Fred" };
            using MemoryStream ms = new MemoryStream();
            var writeState = ProtoWriter.State.Create(ms, null, null);
            try
            {
                ser(ref writeState, cs1);
                writeState.Close();
            }
            finally
            {
                writeState.Dispose();
            }
            byte[] blob = ms.ToArray();
            ms.Position = 0;
            var state = ProtoReader.State.Create(ms, null, null);
            try
            {
                CustomerStruct? cst = (CustomerStruct?)deser(ref state, default);
                Assert.True(cst.HasValue);
                CustomerStruct cs2 = cst.Value;
                Assert.Equal(cs1.Id, cs2.Id);
                Assert.Equal(cs1.Name, cs2.Name);
            }
            finally
            {
                state.Dispose();
            }
        }
    }
}
#endif