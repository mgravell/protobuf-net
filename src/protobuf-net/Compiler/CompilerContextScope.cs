﻿using ProtoBuf.Internal;
using ProtoBuf.Meta;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;

namespace ProtoBuf.Compiler
{
    internal sealed class CompilerContextScope
    {
        internal static CompilerContextScope CreateInProcess()
        {
            return new CompilerContextScope(null, null, false, null);
        }

        internal static CompilerContextScope CreateForModule(RuntimeTypeModel model, ModuleBuilder module, bool isFullEmit, string assemblyName)
            => new CompilerContextScope(model, module, isFullEmit, assemblyName);

        private CompilerContextScope(RuntimeTypeModel model, ModuleBuilder module, bool isFullEmit, string assemblyName)
        {
            _model = model;
            _module = module;
            IsFullEmit = isFullEmit;
            AssemblyName = assemblyName;
        }

        internal string AssemblyName { get; }

        public bool IsFullEmit { get; }

        private ModuleBuilder _module;
        private readonly RuntimeTypeModel _model;

        private ModuleBuilder GetModule()
            => _module ?? (_module = GetSharedModule());

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ModuleBuilder GetSharedModule() => SharedModule.Shared;
        static class SharedModule
        {
            internal static readonly ModuleBuilder Shared
                = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(nameof(SharedModule)), AssemblyBuilderAccess.Run)
                    .DefineDynamicModule(nameof(SharedModule));
        }

        internal static ILGenerator Implement(TypeBuilder type, Type interfaceType, string name, bool @explicit = true)
        {
            var decl = interfaceType.GetMethod(name, BindingFlags.Public | BindingFlags.Instance);
            if (decl == null) throw new ArgumentException(nameof(name));
            var args = decl.GetParameters();
            string implName = name; // name.StartsWith("get_") ? name.Substring(4) : name;
            var attribs = (decl.Attributes & ~MethodAttributes.Abstract) | MethodAttributes.Final;

            if (@explicit)
            {
                implName = interfaceType.NormalizeName() + "." + implName;
                attribs &= ~MethodAttributes.MemberAccessMask;
                attribs |= MethodAttributes.Private | MethodAttributes.HideBySig;
            }
            var method = type.DefineMethod(implName, attribs,
                decl.ReturnType, Array.ConvertAll(args, x => x.ParameterType));
            for (int i = 0; i < args.Length; i++)
                method.DefineParameter(i + 1, args[i].Attributes, args[i].Name);
            type.DefineMethodOverride(method, decl);
            return method.GetILGenerator();
        }

        private int _localUniqueId;
        private static int s_globalUniqueId;
        private int Uniquify()
            => IsFullEmit ? Interlocked.Increment(ref _localUniqueId)
            : Interlocked.Increment(ref s_globalUniqueId);

        internal FieldInfo DefineSubTypeStateCallbackField<T>(MethodInfo callback)
        {
            if (typeof(T).IsValueType) ThrowHelper.ThrowInvalidOperationException("Not expected for value-type");
            var delegateType = typeof(Action<T, ISerializationContext>);

            var module = GetModule();
            lock (module)
            {
                TypeBuilder type;
                var newTypeName = "<" + callback.Name + ">_helper_" + Uniquify();
                try
                {
                    type = module.DefineType(newTypeName,
                    TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit | TypeAttributes.Class);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Unable to define type: {newTypeName}", ex);
                }

                var fieldName = "s_" + callback.Name;
                var fieldAttribs = FieldAttributes.Assembly | FieldAttributes.Static | FieldAttributes.InitOnly;
                if (IsFullEmit) fieldAttribs |= FieldAttributes.InitOnly;
                var field = type.DefineField(fieldName, delegateType, fieldAttribs);

                static void WriteCall(ILGenerator il, MethodInfo callback)
                {
                    il.Emit(OpCodes.Ldarg_0);
                    foreach (var p in callback.GetParameters())
                    {
                        var parameterType = p.ParameterType;
                        if (parameterType == typeof(ISerializationContext))
                        {
                            il.Emit(OpCodes.Ldarg_1);
                        }
                        else if (parameterType == typeof(SerializationContext))
                        {
                            il.Emit(OpCodes.Ldarg_1);
                            il.EmitCall(OpCodes.Callvirt, typeof(ISerializationContext).GetProperty(nameof(ISerializationContext.Context)).GetGetMethod(), null);
                        }
                        else if (parameterType == typeof(StreamingContext))
                        {
                            il.Emit(OpCodes.Ldarg_1);
                            il.EmitCall(OpCodes.Callvirt, typeof(ISerializationContext).GetProperty(nameof(ISerializationContext.Context)).GetGetMethod(), null);
                            MethodInfo op = typeof(SerializationContext).GetMethod("op_Implicit", new Type[] { typeof(SerializationContext) });
                            il.EmitCall(OpCodes.Call, op, null);
                        }
                        else
                        {
                            ThrowHelper.ThrowNotSupportedException($"Unknown callback parameter: {p.Name}, {parameterType}");
                        }
                    }
                    il.EmitCall(OpCodes.Callvirt, callback, null);
                    il.Emit(OpCodes.Ret);
                }
                if (IsFullEmit)
                {
                    var method = type.DefineMethod(callback.Name,
                        MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.SpecialName | MethodAttributes.HideBySig | MethodAttributes.RTSpecialName,
                        CallingConventions.Standard, typeof(void), new Type[] { typeof(T), typeof(ISerializationContext) });
                    method.DefineParameter(1, ParameterAttributes.None, "obj");
                    method.DefineParameter(2, ParameterAttributes.None, "context");

                    WriteCall(method.GetILGenerator(), callback);
                    
                    var cctor = type.DefineTypeInitializer();
                    var il = cctor.GetILGenerator();
                    il.Emit(OpCodes.Ldnull);
                    il.Emit(OpCodes.Ldftn, method);
                    il.Emit(OpCodes.Newobj, delegateType.GetConstructors().Single());
                    il.Emit(OpCodes.Stsfld, field);
                    il.Emit(OpCodes.Ret);
                }

#if PLAT_NO_EMITDLL
                Type finalType = type.CreateTypeInfo().AsType();
#else
                Type finalType = type.CreateType();
#endif
                var result = finalType.GetField(fieldName, BindingFlags.Static | BindingFlags.NonPublic);
                if (!IsFullEmit)
                {
                    var dm = new DynamicMethod(callback.Name, typeof(void), new Type[] { typeof(T), typeof(ISerializationContext) }, typeof(T), true);
                    WriteCall(dm.GetILGenerator(), callback);
                    result.SetValue(null, dm.CreateDelegate(delegateType));
                }
                return result;
            }

        }

        internal bool ImplementsServiceFor<T>()
        {
            if (_model == null || typeof(T).IsEnum) return false;
            if (!_model.IsKnownType<T>()) return false;

            var mt = _model[typeof(T)];
            if (mt is null) return false;
            if (mt.SerializerType is object) return false;
            return true;
        }
    }
}
