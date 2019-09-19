﻿
using System;
using System.IO;
using System.Text;
using System.Reflection;

#pragma warning disable RCS1163
namespace ProtoBuf
{
    /// <summary>
    /// Not all frameworks are created equal (fx1.1 vs fx2.0,
    /// micro-framework, compact-framework,
    /// silverlight, etc). This class simply wraps up a few things that would
    /// otherwise make the real code unnecessarily messy, providing fallback
    /// implementations if necessary.
    /// </summary>
    internal static class Helpers
    {
        internal static MethodInfo GetInstanceMethod(Type declaringType, string name)
            => declaringType.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        internal static MethodInfo GetStaticMethod(Type declaringType, string name)
            => declaringType.GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        internal static MethodInfo GetInstanceMethod(Type declaringType, string name, Type[] types)
        {
            if (types == null) types = Type.EmptyTypes;
            return declaringType.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, types, null);
        }

        internal static bool IsSubclassOf(Type type, Type baseClass)
            => type.IsSubclassOf(baseClass);

        public static ProtoTypeCode GetTypeCode(Type type)
        {
            TypeCode code = Type.GetTypeCode(type);
            switch (code)
            {
                case TypeCode.Empty:
                case TypeCode.Boolean:
                case TypeCode.Char:
                case TypeCode.SByte:
                case TypeCode.Byte:
                case TypeCode.Int16:
                case TypeCode.UInt16:
                case TypeCode.Int32:
                case TypeCode.UInt32:
                case TypeCode.Int64:
                case TypeCode.UInt64:
                case TypeCode.Single:
                case TypeCode.Double:
                case TypeCode.Decimal:
                case TypeCode.DateTime:
                case TypeCode.String:
                    return (ProtoTypeCode)code;
            }
            if (type == typeof(TimeSpan)) return ProtoTypeCode.TimeSpan;
            if (type == typeof(Guid)) return ProtoTypeCode.Guid;
            if (type == typeof(Uri)) return ProtoTypeCode.Uri;
            if (type == typeof(byte[])) return ProtoTypeCode.ByteArray;
            if (type == typeof(Type)) return ProtoTypeCode.Type;

            return ProtoTypeCode.Unknown;
        }

        internal static MethodInfo GetGetMethod(PropertyInfo property, bool nonPublic, bool allowInternal)
        {
            if (property == null) return null;
            MethodInfo method = property.GetGetMethod(nonPublic);
            if (method == null && !nonPublic && allowInternal)
            { // could be "internal" or "protected internal"; look for a non-public, then back-check
                method = property.GetGetMethod(true);
                if (method == null && !(method.IsAssembly || method.IsFamilyOrAssembly))
                {
                    method = null;
                }
            }
            return method;
        }

        internal static MethodInfo GetSetMethod(PropertyInfo property, bool nonPublic, bool allowInternal)
        {
            if (property == null) return null;

            MethodInfo method = property.GetSetMethod(nonPublic);
            if (method == null && !nonPublic && allowInternal)
            { // could be "internal" or "protected internal"; look for a non-public, then back-check
                method = property.GetGetMethod(true);
                if (method == null && !(method.IsAssembly || method.IsFamilyOrAssembly))
                {
                    method = null;
                }
            }
            return method;
        }

        internal static ConstructorInfo GetConstructor(Type type, Type[] parameterTypes, bool nonPublic)
        {
            return type.GetConstructor(
                nonPublic ? BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                          : BindingFlags.Instance | BindingFlags.Public,
                    null, parameterTypes, null);
        }
        internal static ConstructorInfo[] GetConstructors(Type type, bool nonPublic)
        {
            return type.GetConstructors(
                nonPublic ? BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                          : BindingFlags.Instance | BindingFlags.Public);
        }

        internal static void GetBuffer(MemoryStream stream, out ArraySegment<byte> segment)
        {
            if (stream == null || !stream.TryGetBuffer(out segment))
            {
                throw new InvalidOperationException("Unable to obtain buffer from MemoryStream");
            }

        }

        internal static PropertyInfo GetProperty(Type type, string name, bool nonPublic)
        {
            return type.GetProperty(name,
                nonPublic ? BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                          : BindingFlags.Instance | BindingFlags.Public);
        }

        internal static MemberInfo[] GetInstanceFieldsAndProperties(Type type, bool publicOnly)
        {
            BindingFlags flags = publicOnly ? BindingFlags.Public | BindingFlags.Instance : BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic;
            PropertyInfo[] props = type.GetProperties(flags);
            FieldInfo[] fields = type.GetFields(flags);
            MemberInfo[] members = new MemberInfo[fields.Length + props.Length];
            props.CopyTo(members, 0);
            fields.CopyTo(members, props.Length);
            return members;
        }

        internal static Type GetMemberType(MemberInfo member)
        {
            return member.MemberType switch
            {
                MemberTypes.Field => ((FieldInfo)member).FieldType,
                MemberTypes.Property => ((PropertyInfo)member).PropertyType,
                _ => null,
            };
        }
    }
    /// <summary>
    /// Intended to be a direct map to regular TypeCode, but:
    /// - with missing types
    /// - existing on WinRT
    /// </summary>
    internal enum ProtoTypeCode
    {
        Empty = 0,
        Unknown = 1, // maps to TypeCode.Object
        Boolean = 3,
        Char = 4,
        SByte = 5,
        Byte = 6,
        Int16 = 7,
        UInt16 = 8,
        Int32 = 9,
        UInt32 = 10,
        Int64 = 11,
        UInt64 = 12,
        Single = 13,
        Double = 14,
        Decimal = 15,
        DateTime = 16,
        String = 18,

        // additions
        TimeSpan = 100,
        ByteArray = 101,
        Guid = 102,
        Uri = 103,
        Type = 104
    }
}
#pragma warning restore RCS1163