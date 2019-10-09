﻿using ProtoBuf.Internal;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reflection;

namespace ProtoBuf.Serializers
{

    // not quite ready to expose this yes
    internal static partial class RepeatedSerializers
    {
        private static readonly Hashtable s_providers;

        private static readonly Hashtable s_methodsPerDeclaringType = new Hashtable(), s_knownTypes = new Hashtable();
        private static MemberInfo Resolve(Type declaringType, string methodName, Type[] targs)
        {
            targs ??= Type.EmptyTypes;
            var methods = (MethodTuple[])s_methodsPerDeclaringType[declaringType];
            if (methods == null)
            {
                var declared = declaringType.GetMethods(BindingFlags.Static | BindingFlags.Public);
                methods = Array.ConvertAll(declared, m => new MethodTuple(m));
                lock (s_methodsPerDeclaringType)
                {
                    s_methodsPerDeclaringType[declaringType] = methods;
                }
            }
            foreach (var method in methods)
            {
                if (method.Name == methodName)
                {
                    if (targs.Length == method.GenericArgCount) return method.Construct(targs);
                }
            }
            return null;
        }

        readonly struct MethodTuple
        {
            public string Name => Method.Name;
            private MethodInfo Method { get; }
            public int GenericArgCount { get; }
            public MethodInfo Construct(Type[] targs)
                => GenericArgCount == 0 ? Method : Method.MakeGenericMethod(targs);
            public MethodTuple(MethodInfo method)
            {
                Method = method;
                GenericArgCount = method.IsGenericMethodDefinition
                    ? method.GetGenericArguments().Length : 0;
            }
        }

        private static readonly Registration s_Array = new Registration(0,
            (root, current, targs) => root == current ? Resolve(typeof(RepeatedSerializer), nameof(RepeatedSerializer.CreateVector), targs) : null, true);

        static RepeatedSerializers()
        {
            s_providers = new Hashtable();

            // the orignal! the best! accept no substitutes!
            Add(typeof(List<>), (root, current, targs) => Resolve(typeof(RepeatedSerializer), nameof(RepeatedSerializer.CreateList),
                root == current ? targs : new[] { root, targs[0] }), false);

            // note that the immutable APIs can look a lot like the non-immutable ones; need to have them with *higher* priority to ensure they get recognized correctly
            Add(typeof(ImmutableArray<>), (root, current, targs) => Resolve(typeof(RepeatedSerializer), nameof(RepeatedSerializer.CreateImmutableArray), targs));
            Add(typeof(ImmutableDictionary<,>), (root, current, targs) => Resolve(typeof(MapSerializer), nameof(MapSerializer.CreateImmutableDictionary), targs));
            Add(typeof(ImmutableSortedDictionary<,>), (root, current, targs) => Resolve(typeof(MapSerializer), nameof(MapSerializer.CreateImmutableSortedDictionary), targs));
            Add(typeof(IImmutableDictionary<,>), (root, current, targs) => Resolve(typeof(MapSerializer), nameof(MapSerializer.CreateIImmutableDictionary), targs));
            Add(typeof(ImmutableList<>), (root, current, targs) => Resolve(typeof(RepeatedSerializer), nameof(RepeatedSerializer.CreateImmutableList), targs));
            Add(typeof(IImmutableList<>), (root, current, targs) => Resolve(typeof(RepeatedSerializer), nameof(RepeatedSerializer.CreateImmutableIList), targs));
            Add(typeof(ImmutableHashSet<>), (root, current, targs) => Resolve(typeof(RepeatedSerializer), nameof(RepeatedSerializer.CreateImmutableHashSet), targs));
            Add(typeof(ImmutableSortedSet<>), (root, current, targs) => Resolve(typeof(RepeatedSerializer), nameof(RepeatedSerializer.CreateImmutableSortedSet), targs));
            Add(typeof(IImmutableSet<>), (root, current, targs) => Resolve(typeof(RepeatedSerializer), nameof(RepeatedSerializer.CreateImmutableISet), targs));
            Add(typeof(ImmutableQueue<>), (root, current, targs) => Resolve(typeof(RepeatedSerializer), nameof(RepeatedSerializer.CreateImmutableQueue), targs));
            Add(typeof(IImmutableQueue<>), (root, current, targs) => Resolve(typeof(RepeatedSerializer), nameof(RepeatedSerializer.CreateImmutableIQueue), targs));
            Add(typeof(ImmutableStack<>), (root, current, targs) => Resolve(typeof(RepeatedSerializer), nameof(RepeatedSerializer.CreateImmutableStack), targs));
            Add(typeof(IImmutableStack<>), (root, current, targs) => Resolve(typeof(RepeatedSerializer), nameof(RepeatedSerializer.CreateImmutableIStack), targs));

            // the concurrent set
            Add(typeof(ConcurrentDictionary<,>), (root, current, targs) => Resolve(typeof(MapSerializer), nameof(MapSerializer.CreateConcurrentDictionary), new[] { root, targs[0], targs[1] }), false);
            Add(typeof(ConcurrentBag<>), (root, current, targs) => Resolve(typeof(RepeatedSerializer), nameof(RepeatedSerializer.CreateConcurrentBag), new[] { root, targs[0] }), false);
            Add(typeof(ConcurrentQueue<>), (root, current, targs) => Resolve(typeof(RepeatedSerializer), nameof(RepeatedSerializer.CreateConcurrentQueue), new[] { root, targs[0] }), false);
            Add(typeof(ConcurrentStack<>), (root, current, targs) => Resolve(typeof(RepeatedSerializer), nameof(RepeatedSerializer.CreateConcurrentStack), new[] { root, targs[0] }), false);
            Add(typeof(IProducerConsumerCollection<>), (root, current, targs) => Resolve(typeof(RepeatedSerializer), nameof(RepeatedSerializer.CreateIProducerConsumerCollection), new[] { root, targs[0] }), false);

            // pretty normal stuff
            Add(typeof(Dictionary<,>), (root, current, targs) => Resolve(typeof(MapSerializer), nameof(MapSerializer.CreateDictionary), root == current ? targs : new[] { root, targs[0], targs[1] }), false);
            Add(typeof(IDictionary<,>), (root, current, targs) => Resolve(typeof(MapSerializer), nameof(MapSerializer.CreateDictionary), new[] { root, targs[0], targs[1] }), false);
            Add(typeof(Queue<>), (root, current, targs) => Resolve(typeof(RepeatedSerializer), nameof(RepeatedSerializer.CreateQueue), new[] { root, targs[0] }), false);
            Add(typeof(Stack<>), (root, current, targs) => Resolve(typeof(RepeatedSerializer), nameof(RepeatedSerializer.CreateStack), new[] { root, targs[0] }), false);

            // fallbacks, these should be at the end
            Add(typeof(ICollection<>), (root, current, targs) => Resolve(typeof(RepeatedSerializer), nameof(RepeatedSerializer.CreateCollection), new[] { root, targs[0] }), false);
            Add(typeof(IReadOnlyCollection<>), (root, current, targs) => Resolve(typeof(RepeatedSerializer), nameof(RepeatedSerializer.CreateReadOnlyCollection), new[] { root, targs[0] }), false);
        }

        public static void Add(Type type, Func<Type, Type, Type[], MemberInfo> implementation, bool exactOnly = true)
        {
            if (type == null) ThrowHelper.ThrowArgumentNullException(nameof(type));
            lock (s_providers)
            {
                var reg = new Registration(s_providers.Count + 1, implementation, exactOnly);
                s_providers.Add(type, reg);
            }
            lock (s_knownTypes)
            {
                s_knownTypes.Clear();
            }
        }

        internal static RepeatedSerializerStub TryGetRepeatedProvider(Type type)
        {
            if (type == null) return null;

            var known = (RepeatedSerializerStub)s_knownTypes[type];
            if (known == null)
            {
                known = RepeatedSerializerStub.Create(type, GetProviderForType(type));
                lock (s_knownTypes)
                {
                    s_knownTypes[type] = known;
                }
            }

            return known.IsEmpty ? null : known;
        }

        private static MemberInfo GetProviderForType(Type type)
        {
            if (type == null) return null;

            if (type.IsArray)
            {
                // the fun bit here is checking we mean a *vector*
                if (type == typeof(byte[])) return null; // special-case, "bytes"
                return s_Array.Resolve(type, type.GetElementType().MakeArrayType());
            }

            MemberInfo bestMatch = null;
            int bestMatchPriority = int.MaxValue;
            void Consider(MemberInfo member, int priority)
            {
                if (priority < bestMatchPriority)
                {
                    bestMatch = member;
                    bestMatchPriority = priority;
                }
            }

            Type current = type;
            while (current != null && current != typeof(object))
            {
                if (TryGetProvider(type, current, out var found, out var priority)) Consider(found, priority);
                current = current.BaseType;
            }

            foreach (var iType in type.GetInterfaces())
            {
                if (TryGetProvider(type, iType, out var found, out var priority)) Consider(found, priority);
            }

            return bestMatch;
        }

        private static bool TryGetProvider(Type root, Type current, out MemberInfo member, out int priority)
        {
            var found = (Registration)s_providers[current];
            if (found == null && current.IsGenericType)
            {
                found = (Registration)s_providers[current.GetGenericTypeDefinition()];
            }

            if (found == null || (found.ExactOnly && root != current))
            {
                member = null;
                priority = default;
                return false;
            }
            member = found.Resolve(root, current);
            priority = found.Priority;
            return true;

        }

        private sealed class Registration
        {
            public MemberInfo Resolve(Type root, Type current)
            {
                Type[] targs;
                if (current.IsGenericType)
                    targs = current.GetGenericArguments();
                else if (current.IsArray)
                    targs = new[] { current.GetElementType() };
                else
                    targs = Type.EmptyTypes;

                return Implementation?.Invoke(root, current, targs);
            }
            public bool ExactOnly { get; }
            public int Priority { get; }
            private Func<Type, Type, Type[], MemberInfo> Implementation { get; }
            public Registration(int priority, Func<Type, Type, Type[], MemberInfo> implementation, bool exactOnly)
            {
                Priority = priority;
                Implementation = implementation;
                ExactOnly = exactOnly;
            }
        }
    }
}
