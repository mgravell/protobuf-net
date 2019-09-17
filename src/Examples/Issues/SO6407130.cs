﻿using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization;
using Xunit;
using ProtoBuf;
using ProtoBuf.Meta;
using System;

namespace Examples.Issues
{
    
    public class SO6407130
    {
        public class A
        {
            public int X { get; private set; }
            public A(int x)
            {
                X = x;
            }

            public static implicit operator ASurrogate(A a)
            {
                return a == null ? null : new ASurrogate { X = a.X };
            }
            public static implicit operator A(ASurrogate a)
            {
                return a == null ? null : new A(a.X);
            }
        }

        [ProtoContract]
        public abstract class ASurrogateBase
        {
            public abstract int X { get; set; }
        }

        [ProtoContract]
        public class ASurrogate : ASurrogateBase
        {
            [ThreadStatic] // just in case...
            public static int HackyFlags;
            [OnDeserializing]
            public void OnDeserializing(StreamingContext context)
            {
                HackyFlags |= 1;
            }

            [OnDeserialized]
            public void OnDeserialized(StreamingContext context)
            {
                HackyFlags |= 2;
            }
            [OnSerializing]
            public void OnSerializing(StreamingContext context)
            {
                HackyFlags |= 4;
            }

            [OnSerialized]
            public void OnSerialized(StreamingContext context)
            {
                HackyFlags |= 8;
            }
            [ProtoMember(1)]
            public override int X { get; set; }
        }

        [ProtoContract]
        public class B
        {
            [ProtoMember(1)]
            public A A { get; set; }
        }
        [Fact]
        public void Execute()
        {
            var m = RuntimeTypeModel.Create();
            m.AutoCompile = false;
            m.Add(typeof(ASurrogateBase), true).AddSubType(1, typeof(ASurrogate));
            m.Add(typeof(A), false).SetSurrogate(typeof(ASurrogate));
             
            TestModel(m, "Runtime");

            m.CompileInPlace();
            TestModel(m, "CompileInPlace");

            TestModel(m.Compile(), "Compile");

            m.Compile("SO6407130", "SO6407130.dll");
            PEVerify.AssertValid("SO6407130.dll");
        }
#pragma warning disable IDE0060
        static void TestModel(TypeModel model, string caption)
#pragma warning restore IDE0060
        {
            var b = new B { A = new A(117) };
            ASurrogate.HackyFlags = 0;
            using var ms = new MemoryStream();
            model.Serialize(ms, b);
            Assert.Equal(12, ASurrogate.HackyFlags); //, caption);

            ms.Position = 0;
            ASurrogate.HackyFlags = 0;
#pragma warning disable CS0618
            var b2 = (B)model.Deserialize(ms, null, typeof(B));
#pragma warning restore CS0618
            Assert.Equal(3, ASurrogate.HackyFlags); //, caption);
            Assert.Equal(117, b2.A.X); //, caption);
        }
    }
}
