using System.Collections.Generic;
using Xunit;
using ProtoBuf.Meta;

namespace ProtoBuf.unittest.Serializers
{
    public class KeyValuePairTests
    {
        [Fact]
        public void BasicPairTest()
        {
            var pair = new KeyValuePair<int, string>(123, "abc");
            var model = RuntimeTypeModel.Create();
            var clone = (KeyValuePair<int, string>)model.DeepClone(pair);
            Assert.Equal(pair.Key, clone.Key); //, "Runtime");
            Assert.Equal(pair.Value, clone.Value); //, "Runtime");

            model.CompileInPlace();
            clone = (KeyValuePair<int, string>)model.DeepClone(pair);
            Assert.Equal(pair.Key, clone.Key); //, "CompileInPlace");
            Assert.Equal(pair.Value, clone.Value); //, "CompileInPlace");

            clone = (KeyValuePair<int, string>)model.Compile().DeepClone(pair);
            Assert.Equal(pair.Key, clone.Key); //, "Compile");
            Assert.Equal(pair.Value, clone.Value); //, "Compile");
        }
        
        [Fact]
        public void DictionaryInt32KeyTest()
        {
            var data = new Dictionary<int, string> { { 123, "abc" }, { 456, "def" } };
            var model = RuntimeTypeModel.Create();
            var clone = (Dictionary<int, string>)model.DeepClone(data);
            Assert.Equal(2, clone.Count);
            Assert.Equal("abc", clone[123]); //, "Runtime");
            Assert.Equal("def", clone[456]); //, "Runtime");

            model.CompileInPlace();
            clone = (Dictionary<int, string>)model.DeepClone(data);
            Assert.Equal(2, clone.Count);
            Assert.Equal("abc", clone[123]); //, "CompileInPlace");
            Assert.Equal("def", clone[456]); //, "CompileInPlace");

            clone = (Dictionary<int, string>)model.Compile().DeepClone(data);
            Assert.Equal(2, clone.Count);
            Assert.Equal("abc", clone[123]); //, "Compile");
            Assert.Equal("def", clone[456]); //, "Compile");
        }

        [Fact]
        public void DictionarySingleKeyTest()
        {
            var data = new Dictionary<float, string> { { 123, "abc" }, { 456, "def" } };
            var model = RuntimeTypeModel.Create();
            var clone = (Dictionary<float, string>)model.DeepClone(data);
            Assert.Equal(2, clone.Count);
            Assert.Equal("abc", clone[123]); //, "Runtime");
            Assert.Equal("def", clone[456]); //, "Runtime");

            model.CompileInPlace();
            clone = (Dictionary<float, string>)model.DeepClone(data);
            Assert.Equal(2, clone.Count);
            Assert.Equal("abc", clone[123]); //, "CompileInPlace");
            Assert.Equal("def", clone[456]); //, "CompileInPlace");

            clone = (Dictionary<float, string>)model.Compile().DeepClone(data);
            Assert.Equal(2, clone.Count);
            Assert.Equal("abc", clone[123]); //, "Compile");
            Assert.Equal("def", clone[456]); //, "Compile");
        }

        [Fact]
        public void TypeWithPairTest()
        {
            var orig = new TypeWithPair { Pair = new KeyValuePair<string, decimal>("abc", 123.45M) };
            var model = RuntimeTypeModel.Create();
            var clone = (TypeWithPair)model.DeepClone(orig);
            Assert.Equal("abc", clone.Pair.Key); //, "Runtime");
            Assert.Equal(123.45M, clone.Pair.Value); //, "Runtime");

            model.Compile("TypeWithPairTest", "TypeWithPairTest.dll");
            PEVerify.Verify("TypeWithPairTest.dll");

            model.CompileInPlace();
            clone = (TypeWithPair)model.DeepClone(orig);
            Assert.Equal("abc", clone.Pair.Key); //, "CompileInPlace");
            Assert.Equal(123.45M, clone.Pair.Value); //, "CompileInPlace");

            clone = (TypeWithPair)model.Compile().DeepClone(orig);
            Assert.Equal("abc", clone.Pair.Key); //, "Compile");
            Assert.Equal(123.45M, clone.Pair.Value); //, "Compile");
        }

        [Fact]
        public void TypeWithDictionaryTest()
        {
            var orig = new TypeWithDictionary { Data = new Dictionary<string, decimal> { { "abc", 123.45M } } };
            var model = RuntimeTypeModel.Create();
            var clone = (TypeWithDictionary)model.DeepClone(orig);
            Assert.Single(clone.Data);
            Assert.Equal(123.45M, clone.Data["abc"]); //, "Runtime");

            model.Compile("TypeWithDictionaryTest", "TypeWithDictionaryTest.dll");
            PEVerify.Verify("TypeWithDictionaryTest.dll");

            model.CompileInPlace();
            clone = (TypeWithDictionary)model.DeepClone(orig);
            Assert.Single(clone.Data);
            Assert.Equal(123.45M, clone.Data["abc"]); //, "Runtime");

            clone = (TypeWithDictionary)model.Compile().DeepClone(orig);
            Assert.Single(clone.Data);
            Assert.Equal(123.45M, clone.Data["abc"]); //, "Runtime");
        }

        [Fact]
        public void ShouldWorkWithAutoLoadDisabledRuntime()
        {
            var orig = new TypeWithDictionary {Data = new Dictionary<string, decimal> {{"abc", 123.45M}}};
            var model = RuntimeTypeModel.Create();
            model.AutoAddMissingTypes = false;
            model.Add(typeof (TypeWithDictionary), true);
            var clone = (TypeWithDictionary) model.DeepClone(orig);
            Assert.Single(clone.Data);
            Assert.Equal(123.45M, clone.Data["abc"]);
        }

        [Fact]
        public void ShouldWorkWithAutoLoadDisabledAndAddedExplicitlyRuntime()
        {
            var orig = new TypeWithDictionary { Data = new Dictionary<string, decimal> { { "abc", 123.45M } } };
            var model = RuntimeTypeModel.Create();
            model.AutoAddMissingTypes = false;
            model.Add(typeof(TypeWithDictionary), true);
            model.Add(typeof(KeyValuePair<string,decimal>), true);
            var clone = (TypeWithDictionary)model.DeepClone(orig);
            Assert.Single(clone.Data);
            Assert.Equal(123.45M, clone.Data["abc"]);
        }
        [Fact]
        public void ShouldWorkWithAutoLoadDisabledCompileInPlace()
        {
            var orig = new TypeWithDictionary {Data = new Dictionary<string, decimal> {{"abc", 123.45M}}};
            var model = RuntimeTypeModel.Create();
            model.AutoAddMissingTypes = false;
            model.Add(typeof (TypeWithDictionary), true);
            model.CompileInPlace();
            var clone = (TypeWithDictionary) model.DeepClone(orig);
            Assert.Single(clone.Data);
            Assert.Equal(123.45M, clone.Data["abc"]);
        }
        [Fact]
        public void ShouldWorkWithAutoLoadDisabledCompile()
        {
            var orig = new TypeWithDictionary { Data = new Dictionary<string, decimal> { { "abc", 123.45M } } };
            var model = RuntimeTypeModel.Create();
            model.AutoAddMissingTypes = false;
            model.Add(typeof(TypeWithDictionary), true);

            var compiled = model.Compile("MapSerializer", "ShouldWorkWithAutoLoadDisabledCompile.dll");
            PEVerify.Verify("ShouldWorkWithAutoLoadDisabledCompile.dll");
            var clone = (TypeWithDictionary)compiled.DeepClone(orig);
            Assert.Single(clone.Data);
            Assert.Equal(123.45M, clone.Data["abc"]);

            clone = (TypeWithDictionary)model.Compile().DeepClone(orig);
            Assert.Single(clone.Data);
            Assert.Equal(123.45M, clone.Data["abc"]);
        }

        [Fact]
        public void TypeWithIDictionaryTest()
        {
            var orig = new TypeWithIDictionary { Data = new Dictionary<string, decimal> { { "abc", 123.45M } } };
            var model = RuntimeTypeModel.Create();
            var clone = (TypeWithIDictionary)model.DeepClone(orig);
            Assert.Equal(1, clone.Data.Count);
            Assert.Equal(123.45M, clone.Data["abc"]); //, "Runtime");

            model.Compile("TypeWithIDictionary", "TypeWithIDictionary.dll");
            PEVerify.Verify("TypeWithIDictionary.dll");

            model.CompileInPlace();
            clone = (TypeWithIDictionary)model.DeepClone(orig);
            Assert.Equal(1, clone.Data.Count);
            Assert.Equal(123.45M, clone.Data["abc"]); //, "Runtime");

            clone = (TypeWithIDictionary)model.Compile().DeepClone(orig);
            Assert.Equal(1, clone.Data.Count);
            Assert.Equal(123.45M, clone.Data["abc"]); //, "Runtime");
        }

        [ProtoContract]
        public class TypeWithPair
        {
            [ProtoMember(1)]
            public KeyValuePair<string, decimal> Pair { get; set; }
        }

        [ProtoContract]
        public class TypeWithDictionary
        {
            [ProtoMember(1)]
            public Dictionary<string, decimal> Data { get; set; }
        }

        [ProtoContract]
        public class TypeWithIDictionary
        {
            [ProtoMember(1)]
            public IDictionary<string, decimal> Data { get; set; }
        }
    }
}
