﻿using ProtoBuf.Meta;
using System;
using System.IO;
using Xunit;

namespace ProtoBuf
{
    public class ManualSerializer
    {
        [Fact]
        public void ReadWriteAutomated()
        {
            var model = RuntimeTypeModel.Create();
            model.AutoCompile = false;
            using (var ms = new MemoryStream())
            {
                var obj = new C { AVal = 123, BVal = 456, CVal = 789 };
                model.Serialize(ms, obj);
                var hex = BitConverter.ToString(ms.GetBuffer(), 0, (int)ms.Length);
                Assert.Equal("22-08-2A-03-18-95-06-10-C8-03-08-7B", hex);
                // 22 = field 4, type String
                // 08 = length 8
                //      2A = field 5, type String
                //      03 = length 3
                //          18 = field 3, type Variant
                //          95-06 = 789 (raw) or -395 (zigzag)
                //      10 = field 2, type Variant
                //      C8-03 = 456(raw) or 228(zigzag)
                // 08 = field 1, type Variant
                // 7B = 123(raw) or - 62(zigzag)

                ms.Position = 0;
                var raw = model.Deserialize(ms, null, typeof(A));
                var clone = Assert.IsType<C>(raw);
                Assert.NotSame(obj, clone);
                Assert.Equal(123, clone.AVal);
                Assert.Equal(456, clone.BVal);
                Assert.Equal(789, clone.CVal);
            }
        }

        [Fact]
        public void ReadWriteManual()
        {
            using (var ms = new MemoryStream())
            {
                var obj = new C { AVal = 123, BVal = 456, CVal = 789 };
                using (var writer = ProtoWriter.Create(out var state, ms, null))
                {
                    ProtoWriter.Serialize<A>(obj, writer, ref state, ModelSerializer.Serializer);
                    var hex = BitConverter.ToString(ms.GetBuffer(), 0, (int)ms.Length);
                    Assert.Equal("22-08-2A-03-18-95-06-10-C8-03-08-7B", hex);

                }
                ms.Position = 0;
                using (var reader = ProtoReader.Create(out var state, ms, null))
                {
                    var raw = reader.Deserialize<A>(ref state, null, ModelSerializer.Serializer);
                    var clone = Assert.IsType<C>(raw);
                    Assert.NotSame(obj, clone);
                    Assert.Equal(123, clone.AVal);
                    Assert.Equal(456, clone.BVal);
                    Assert.Equal(789, clone.CVal);
                }
            }
        }
    }

    class ModelSerializer
        : IProtoSerializer<A, A>, IProtoSerializer<A, B>, IProtoSerializer<A, C>,
        IProtoFactory<A, A>, IProtoFactory<A, B>, IProtoFactory<A, C>
    {
        private ModelSerializer() { }
        public static ModelSerializer Serializer { get; } = new ModelSerializer();

        A IProtoFactory<A, A>.Create(SerializationContext context) => new A();
        B IProtoFactory<A, B>.Create(SerializationContext context) => new B();
        C IProtoFactory<A, C>.Create(SerializationContext context) => new C();

        void IProtoFactory<A, A>.Copy(SerializationContext context, A from, A to)
        {
            to.AVal = from.AVal;
        }

        void IProtoFactory<A, B>.Copy(SerializationContext context, A from, B to)
        {
            if (from is B b)
            {
                to.BVal = b.BVal;
            }

            ((IProtoFactory<A, A>)Serializer).Copy(context, from, to);
        }

        void IProtoFactory<A, C>.Copy(SerializationContext context, A from, C to)
        {
            if (from is C c)
            {
                to.CVal = c.CVal;
            }

            if (from is B b)
            {
                ((IProtoFactory<A, B>)Serializer).Copy(context, b, to);
            }
            else
            {
                ((IProtoFactory<A, A>)Serializer).Copy(context, from, to);
            }
        }

        void IProtoSerializer<A, A>.Serialize(ProtoWriter writer, ref ProtoWriter.State state, A value)
        {
            if (value is B b)
            {
                ProtoWriter.WriteFieldHeader(4, WireType.String, writer, ref state);
                ProtoWriter.WriteSubItem<A, B>(b, writer, ref state, Serializer, false);
            }
            if (value.AVal != 0)
            {
                ProtoWriter.WriteFieldHeader(1, WireType.Variant, writer, ref state);
                ProtoWriter.WriteInt32(value.AVal, writer, ref state);
            }
        }

        A IProtoSerializer<A, A>.Deserialize(ProtoReader reader, ref ProtoReader.State state, A value)
        {
            int field;
            while ((field = reader.ReadFieldHeader(ref state)) != 0)
            {
                switch (field)
                {
                    case 1:
                        if (value == null) value = new A();
                        value.AVal = reader.ReadInt32(ref state);
                        break;
                    case 4:
                        value = reader.ReadSubItem<A, B>(ref state, value, Serializer);
                        break;
                    default:
                        reader.SkipField(ref state);
                        break;
                }
            }
            return value;
        }

        void IProtoSerializer<A, B>.Serialize(ProtoWriter writer, ref ProtoWriter.State state, B value)
        {
            if (value is C c)
            {
                ProtoWriter.WriteFieldHeader(5, WireType.String, writer, ref state);
                ProtoWriter.WriteSubItem<A, C>(c, writer, ref state, Serializer, false);
            }
            if (value.BVal != 0)
            {
                ProtoWriter.WriteFieldHeader(2, WireType.Variant, writer, ref state);
                ProtoWriter.WriteInt32(value.BVal, writer, ref state);
            }
        }

        B IProtoSerializer<A, B>.Deserialize(ProtoReader reader, ref ProtoReader.State state, A value)
        {
            int field;
            B typed = value as B;
            while ((field = reader.ReadFieldHeader(ref state)) != 0)
            {
                switch (field)
                {
                    case 2:
                        if (typed == null) typed = reader.Cast<A, B>(value, Serializer);
                        typed.BVal = reader.ReadInt32(ref state);
                        break;
                    case 5:
                        typed = reader.ReadSubItem<A, C>(ref state, typed, Serializer);
                        break;
                    default:
                        reader.SkipField(ref state);
                        break;
                }
            }
            return typed ?? reader.Cast<A, B>(value, Serializer);
        }

        void IProtoSerializer<A, C>.Serialize(ProtoWriter writer, ref ProtoWriter.State state, C value)
        {
            if (value.CVal != 0)
            {
                ProtoWriter.WriteFieldHeader(3, WireType.Variant, writer, ref state);
                ProtoWriter.WriteInt32(value.CVal, writer, ref state);
            }
        }

        C IProtoSerializer<A, C>.Deserialize(ProtoReader reader, ref ProtoReader.State state, A value)
        {
            int field;
            C typed = value as C;
            while ((field = reader.ReadFieldHeader(ref state)) != 0)
            {
                switch (field)
                {
                    case 3:
                        if (typed == null) typed = reader.Cast<A, C>(value, Serializer);
                        typed.CVal = reader.ReadInt32(ref state);
                        break;
                    default:
                        reader.SkipField(ref state);
                        break;
                }
            }
            return typed ?? reader.Cast<A, C>(value, Serializer);
        }
    }

    [ProtoContract]
    [ProtoInclude(4, typeof(B))]
    public class A
    {
        [ProtoMember(1)]
        public int AVal { get; set; }
    }

    [ProtoContract]
    [ProtoInclude(5, typeof(C))]
    public class B : A
    {
        [ProtoMember(2)]
        public int BVal { get; set; }
    }
    [ProtoContract]
    public class C : B
    {
        [ProtoMember(3)]
        public int CVal { get; set; }

    }
}