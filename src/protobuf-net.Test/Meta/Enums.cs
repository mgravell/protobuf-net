﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Xunit;
using ProtoBuf.Meta;
using System.ComponentModel;
using System.IO;
using ProtoBuf.Internal;

namespace ProtoBuf.unittest.Meta
{
    
    public class Enums
    {
        public enum I8 : sbyte { A, B, C }
        public enum U8 : byte { A, B, C }
        public enum I16 : short { A, B, C }
        public enum U16 : ushort { A, B, C }
        public enum I32 : int { A, B, C }
        public enum U32 : uint { A, B, C }
        public enum I64 : long { A, B, C }
        public enum U64 : ulong { A, B, C }

        [ProtoContract]
        public class AllTheEnums {
            [ProtoMember(1)] public I8 I8 { get; set; }
            [ProtoMember(2)] public U8 U8 { get; set; }
            [ProtoMember(3), DefaultValue(I16.C)] public I16 I16 { get; set; }
            [ProtoMember(4), DefaultValue("C")] public U16 U16 { get; set; }
            [ProtoMember(5), DefaultValue(3)] public I32 I32 { get; set; }
            [ProtoMember(6)] public U32 U32 { get; set; }
            [ProtoMember(7)] public I64 I64 { get; set; }
            [ProtoMember(8)] public U64 U64 { get; set; }
        }
        static RuntimeTypeModel BuildModel(bool withPassThru) {
            var model = RuntimeTypeModel.Create();
            if (withPassThru)
            {
                model.Add(typeof(I8), true);
                model.Add(typeof(U8), true);
                model.Add(typeof(I16), true);
                model.Add(typeof(U16), true);
                model.Add(typeof(I32), true);
                model.Add(typeof(U32), true);
                model.Add(typeof(I64), true);
                model.Add(typeof(U64), true);
            }
            model.Add(typeof(AllTheEnums), true);
            return model;
        }

        [Fact]
        public void CanCompileEnumsAsPassthru()
        {
            var model = BuildModel(true);
            model.Compile("AllTheEnumsPassThru", "AllTheEnumsPassThru.dll");
            PEVerify.Verify("AllTheEnumsPassThru.dll");
        }

        [Fact]
        public void CanCompileEnumsAsMapped()
        {
            var model = BuildModel(false);
            model.Compile("AllTheEnumsMapped", "AllTheEnumsMapped.dll");
            PEVerify.Verify("AllTheEnumsMapped.dll");
        }

        [Fact]
        public void CanRoundTripAsPassthru()
        {
            var model = BuildModel(true);

            AllTheEnums ate = new AllTheEnums
            {
                 I8 = I8.B, U8 = U8.B,
                 I16 = I16.B, U16 = U16.B,
                 I32 = I32.B, U32 = U32.B,
                 I64 = I64.B, U64 = U64.B
            }, clone;

            clone = (AllTheEnums)model.DeepClone(ate);
            CompareAgainstClone(ate, clone, "Runtime");

            model.CompileInPlace();
            clone = (AllTheEnums)model.DeepClone(ate);
            CompareAgainstClone(ate, clone, "CompileInPlace");

            clone = (AllTheEnums)model.Compile().DeepClone(ate);
            CompareAgainstClone(ate, clone, "Compile");
        }
        [Fact]
        public void CanRoundTripAsMapped()
        {
            var model = BuildModel(false);

            AllTheEnums ate = new AllTheEnums
            {
                I8 = I8.B,
                U8 = U8.B,
                I16 = I16.B,
                U16 = U16.B,
                I32 = I32.B,
                U32 = U32.B,
                I64 = I64.B,
                U64 = U64.B
            }, clone;

            clone = (AllTheEnums)model.DeepClone(ate);
            CompareAgainstClone(ate, clone, "Runtime");

            model.CompileInPlace();
            clone = (AllTheEnums)model.DeepClone(ate);
            CompareAgainstClone(ate, clone, "CompileInPlace");

            clone = (AllTheEnums)model.Compile().DeepClone(ate);
            CompareAgainstClone(ate, clone, "Compile");
        }

#pragma warning disable IDE0060
        static void CompareAgainstClone(AllTheEnums original, AllTheEnums clone, string caption)
#pragma warning restore IDE0060
        {
            Assert.NotNull(original); //, caption + " (original)");
            Assert.NotNull(clone); //, caption + " (clone)");
            Assert.NotSame(original, clone); //, caption);
            Assert.Equal(original.I8, clone.I8); //, caption);
            Assert.Equal(original.U8, clone.U8); //, caption);
            Assert.Equal(original.I16, clone.I16); //, caption);
            Assert.Equal(original.U16, clone.U16); //, caption);
            Assert.Equal(original.I32, clone.I32); //, caption);
            Assert.Equal(original.U32, clone.U32); //, caption);
            Assert.Equal(original.I64, clone.I64); //, caption);
            Assert.Equal(original.U64, clone.U64); //, caption);
        }

        TTo ChangeType<TTo>(TypeModel model, object value)
        {
            using var ms = new MemoryStream();
#pragma warning disable CS0618
            model.Serialize(ms, value);
            ms.Position = 0;
            return (TTo)model.Deserialize(ms, null, typeof(TTo));
#pragma warning restore CS0618
        }

        [Fact]
        public void AddInvalidEnum()
        {
            var model = RuntimeTypeModel.Create();
            var mt = model.Add(typeof(EnumWithThings), false);
            var fields = mt.GetFields();
            Assert.Empty(fields);

            mt.Add("HazThis");
            fields = mt.GetFields();
            var field = Assert.Single(fields);
            Assert.Equal(1, field.FieldNumber);

            var ex = Assert.Throws<ArgumentException>(() => mt.Add("NotThis"));
            Assert.Equal("memberName", ex.ParamName);
        }
        public enum EnumWithThings
        {
            None = 0,
            HazThis = 42,
        }

        [Fact]
        public void CanSerializeUnknownEnum()
        {
            var model = RuntimeTypeModel.Create();
            model.AutoAddMissingTypes = false;
            Assert.True(model.CanSerialize(typeof(EnumWithThings)));
            Assert.Equal(ObjectScope.Scalar, DynamicStub.CanSerialize(typeof(EnumWithThings), null, out var wt));
            Assert.Equal(ObjectScope.Scalar, DynamicStub.CanSerialize(typeof(EnumWithThings?), null, out wt));

            using var ms = new MemoryStream();


            var writeState = ProtoWriter.State.Create(ms, null);
            try
            {
                writeState.WriteFieldHeader(1, WireType.Varint);
                Assert.True(DynamicStub.TrySerialize(ObjectScope.Scalar, typeof(EnumWithThings), null, ref writeState, EnumWithThings.HazThis));
                writeState.WriteFieldHeader(2, WireType.Varint);
                Assert.True(DynamicStub.TrySerialize(ObjectScope.Scalar, typeof(EnumWithThings?), null, ref writeState, EnumWithThings.HazThis));
                writeState.Close();
            }
            catch
            {
                writeState.Abandon();
                throw;
            }
            finally
            {
                writeState.Dispose();
            }
            ms.Position = 0;

            var readState = ProtoReader.State.Create(ms, null);
            try
            {
                object val = null;
                Assert.Equal(1, readState.ReadFieldHeader());
                Assert.True(DynamicStub.TryDeserialize(ObjectScope.Scalar, typeof(EnumWithThings), null, ref readState, ref val));
                Assert.Equal(typeof(EnumWithThings), val.GetType());

                val = null;
                Assert.Equal(2, readState.ReadFieldHeader());
                Assert.True(DynamicStub.TryDeserialize(ObjectScope.Scalar, typeof(EnumWithThings?), null, ref readState, ref val));
                Assert.Equal(typeof(EnumWithThings), val.GetType());
                val = null;

                Assert.Equal(0, readState.ReadFieldHeader());
            }
            finally
            {
                readState.Dispose();
            }
        }
    }
}
