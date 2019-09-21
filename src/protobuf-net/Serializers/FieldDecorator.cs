﻿using System;
using System.Diagnostics;
using System.Reflection;

namespace ProtoBuf.Serializers
{
    internal sealed class FieldDecorator : ProtoDecoratorBase
    {
        public override Type ExpectedType { get; }

        private readonly FieldInfo field;

        public override bool RequiresOldValue => true;
        public override bool ReturnsValue => false;
        public FieldDecorator(Type forType, FieldInfo field, IRuntimeProtoSerializerNode tail) : base(tail)
        {
            Debug.Assert(forType != null);
            Debug.Assert(field != null);
            ExpectedType = forType;
            this.field = field;
        }

        public override void Write(ref ProtoWriter.State state, object value)
        {
            Debug.Assert(value != null);
            value = field.GetValue(value);
            if (value != null) Tail.Write(dest, ref state, value);
        }

        public override object Read(ref ProtoReader.State state, object value)
        {
            Debug.Assert(value != null);
            object newValue = Tail.Read(ref state, Tail.RequiresOldValue ? field.GetValue(value) : null);
            if (newValue != null) field.SetValue(value, newValue);
            return null;
        }

        protected override void EmitWrite(Compiler.CompilerContext ctx, Compiler.Local valueFrom)
        {
            ctx.LoadAddress(valueFrom, ExpectedType);
            ctx.LoadValue(field);
            ctx.WriteNullCheckedTail(field.FieldType, Tail, null);
        }
        protected override void EmitRead(Compiler.CompilerContext ctx, Compiler.Local valueFrom)
        {
            using Compiler.Local loc = ctx.GetLocalWithValue(ExpectedType, valueFrom);
            if (Tail.RequiresOldValue)
            {
                ctx.LoadAddress(loc, ExpectedType);
                ctx.LoadValue(field);
            }
            // value is either now on the stack or not needed
            ctx.ReadNullCheckedTail(field.FieldType, Tail, null);

            // the field could be a backing field that needs to be raised back to
            // the property if we're doing a full compile
            MemberInfo member = field;
            ctx.CheckAccessibility(ref member);
            bool writeValue = member is FieldInfo;

            if (writeValue)
            {
                if (Tail.ReturnsValue)
                {
                    using Compiler.Local newVal = new Compiler.Local(ctx, field.FieldType);
                    ctx.StoreValue(newVal);
                    if (field.FieldType.IsValueType)
                    {
                        ctx.LoadAddress(loc, ExpectedType);
                        ctx.LoadValue(newVal);
                        ctx.StoreValue(field);
                    }
                    else
                    {
                        Compiler.CodeLabel allDone = ctx.DefineLabel();
                        ctx.LoadValue(newVal);
                        ctx.BranchIfFalse(allDone, true); // interpret null as "don't assign"

                        ctx.LoadAddress(loc, ExpectedType);
                        ctx.LoadValue(newVal);
                        ctx.StoreValue(field);

                        ctx.MarkLabel(allDone);
                    }
                }
            }
            else
            {
                // can't use result
                if (Tail.ReturnsValue)
                {
                    ctx.DiscardValue();
                }
            }
        }
    }
}