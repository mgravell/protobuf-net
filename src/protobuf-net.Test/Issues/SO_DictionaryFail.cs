﻿using ProtoBuf.Meta;
using ProtoBuf.unittest;
using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace ProtoBuf.Test.Issues
{
    public class SO_DictionaryFail
    {
        [Fact]
        public void TupleDictionary()
        {
            var model = RuntimeTypeModel.Create();
            model.AutoCompile = false;
            model.Add<Tuple<Dictionary<string,double>, Dictionary<string,double>>>();
            Test(model);

            var dll = model.CompileAndVerify(deleteOnSuccess: false);

            model.CompileInPlace();
            Test(model);

            Test(model.Compile());

            Test(dll);
        }

        [Theory]
        [InlineData(true, "0A-03-01-02-03")]
        [InlineData(false, "08-01-08-02-08-03")]
        public void DoNotEmitPackedRootsByDefault(bool allowed, string expected)
        {
            var model = RuntimeTypeModel.Create();
            Assert.False(model.AllowPackedEncodingAtRoot);
            if (allowed)
            {
                model.AllowPackedEncodingAtRoot = true;
                Assert.True(model.AllowPackedEncodingAtRoot);
            }
            using var ms = new MemoryStream();
            model.Serialize(ms, new List<int> { 1, 2, 3 });
            var hex = BitConverter.ToString(ms.GetBuffer(), 0, (int)ms.Length);
            Assert.Equal(expected, hex);

            ms.Position = 0;
            var clone = model.Deserialize<List<int>>(ms);
            Assert.Equal(3, clone.Count);
            Assert.Equal(1, clone[0]);
            Assert.Equal(2, clone[1]);
            Assert.Equal(3, clone[2]);
        }

        private static void Test(TypeModel model)
        {
            var data = Tuple.Create(
                new Dictionary<string, double>
                {
                    {"abc", 123 },
                    {"def", 456 },
                    {"ghi", 789 },
                },
                new Dictionary<string, double>
                {
                    {"jkl", 1011 },
                    {"mno", 1213 },
                });

            

            using var ms = new MemoryStream();
            model.Serialize(ms, data);
            var hex = BitConverter.ToString(ms.GetBuffer(), 0, (int)ms.Length);

            // verified against 2.4.1
            const string expected = "0A-0E-0A-03-61-62-63-11-00-00-00-00-00-C0-5E-40-0A-0E-0A-03-64-65-66-11-00-00-00-00-00-80-7C-40-0A-0E-0A-03-67-68-69-11-00-00-00-00-00-A8-88-40-12-0E-0A-03-6A-6B-6C-11-00-00-00-00-00-98-8F-40-12-0E-0A-03-6D-6E-6F-11-00-00-00-00-00-F4-92-40";
            Assert.Equal(expected, hex);



            ms.Position = 0;
            var clone = model.Deserialize<Tuple<Dictionary<string, double>, Dictionary<string, double>>>(ms);
            Assert.NotSame(data, clone);

            var x = clone.Item1;
            Assert.Equal(3, x.Count);
            Assert.True(x.TryGetValue("abc", out var val));
            Assert.Equal(123, val);
            Assert.True(x.TryGetValue("def", out val));
            Assert.Equal(456, val);
            Assert.True(x.TryGetValue("ghi", out val));
            Assert.Equal(789, val);

            var y = clone.Item2;
            Assert.Equal(2, y.Count);
            Assert.True(y.TryGetValue("jkl", out val));
            Assert.Equal(1011, val);
            Assert.True(y.TryGetValue("mno", out val));
            Assert.Equal(1213, val);
        }
    }
}