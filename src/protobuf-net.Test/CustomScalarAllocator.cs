﻿using Pipelines.Sockets.Unofficial.Arenas;
using ProtoBuf.Meta;
using ProtoBuf.Serializers;
using ProtoBuf.unittest;
using System;
using System.Buffers;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace ProtoBuf
{
    public class CustomScalarAllocator
    {
        [Fact]
        public void CustomScalarIL()
        {
            var model = RuntimeTypeModel.Create();
            model.Add<HazRegularString>();
            model.Add<HazBlobish>();
            model.CompileAndVerify();
        }

        [Fact]
        public void CustomBlobLikeReader()
        {
            using var ms = new MemoryStream();
            var s = "a ☁ ☂ bc ☃ ☄";
            Serializer.Serialize(ms, new HazRegularString { Value = s });
            var expected = Encoding.UTF8.GetBytes(s);
            ms.Position = 0;

            using var arena = new Arena<byte>();
            var ctx = new MyCustomContext(arena);
            var blobish = Serializer.Deserialize<HazBlobish>(ms, context: ctx);
            Assert.True(blobish.Value.Payload.ToArray().SequenceEqual(expected));
        }

        [ProtoContract]
        public class HazRegularString
        {
            [ProtoMember(1)]
            public string Value { get; set; }
        }

        [ProtoContract]
        public class HazBlobish
        {
            [ProtoMember(1)]
            public Blobish Value { get; set; }
        }

        class MyCustomContext : SerializationContext, IBlobAllocator
        {
            private readonly Arena<byte> _arena;
            public MyCustomContext(Arena<byte> arena)
                => _arena = arena;

            ReadOnlySequence<byte> IBlobAllocator.Allocate(int length)
                => _arena.Allocate(length);
        }
        interface IBlobAllocator
        {
            ReadOnlySequence<byte> Allocate(int length);
        }


        [ProtoContract(Serializer = typeof(Blobish.Serializer))]
        public readonly struct Blobish
        {
            public ReadOnlySequence<byte> Payload { get; }
            public Blobish(ReadOnlySequence<byte> payload)
                => Payload = payload;
            public static Blobish Empty => default;
            public bool IsEmpty => Payload.IsEmpty;


            private sealed class Serializer : ISerializer<Blobish>
            {
                SerializerFeatures ISerializer<Blobish>.Features =>
                    SerializerFeatures.CategoryScalar | SerializerFeatures.WireTypeString;

                Blobish ISerializer<Blobish>.Read(ref ProtoReader.State state, Blobish value)
                    => new Blobish(state.AppendBytes(value.Payload, (ctx, length) =>
                    {
                        var allocator = ctx.Context as IBlobAllocator;
                        if (allocator == null) throw new InvalidOperationException(
                            "in reality, we'd probably allocate a regular array here?");
                        return allocator.Allocate(length);
                    }));

                void ISerializer<Blobish>.Write(ref ProtoWriter.State state, Blobish value)
                    => state.WriteBytes(value.Payload);
            }
        }
    }
}
