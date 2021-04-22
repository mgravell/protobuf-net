using System;
using System.Collections.Generic;
using ProtoBuf.Meta;
using Xunit;

namespace ProtoBuf.unittest.Serializers
{
    public sealed class ImplicitTupleOptionTests
    {
        public class ImplicitTupleType
        {
            public int Value { get; }
	
            public ImplicitTupleType(int value)
            {
                Value = value;
            }
        }

        [Fact]
        public void ImplicitTupleTypeFailsWhenOptionDisabled()
        {
            var model = RuntimeTypeModel.Create();
            model.AllowImplicitTuples = false;
            var kv = new KeyValuePair<int, string>(0xdead, "hello");
            Assert.Throws<InvalidOperationException>(() => model.DeepClone(kv));
        }

        [Fact]
        public void DictionaryWorksWithoutImplicitTuples()
        {
            var value = new Dictionary<int, string>()
            {
                [123] = "abc",
                [456] = "def"
            };
            var model = RuntimeTypeModel.Create();
            model.AllowImplicitTuples = false;
            var clone = model.DeepClone(value);
            Assert.Equal(value.Count, clone.Count); //, "Runtime");
            Assert.Equal("abc", clone[123]); //, "Runtime");
            Assert.Equal("def", clone[456]); //, "Runtime");

            model.CompileInPlace();
            clone = model.DeepClone(value);
            Assert.Equal(value.Count, clone.Count); //, "CompileInPlace");
            Assert.Equal("abc", clone[123]); //, "CompileInPlace");
            Assert.Equal("def", clone[456]); //, "CompileInPlace");

            clone = model.Compile().DeepClone(value);
            Assert.Equal(value.Count, clone.Count); //, "Compile");
            Assert.Equal("abc", clone[123]); //, "Compile");
            Assert.Equal("def", clone[456]); //, "Compile");
        }
    }
}