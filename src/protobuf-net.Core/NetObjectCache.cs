﻿using ProtoBuf.Internal;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace ProtoBuf
{
    internal sealed class NetObjectCache
    {
        private readonly Dictionary<ObjectKey, long> _knownLengths = new Dictionary<ObjectKey, long>();

        private readonly struct ObjectKey : IEquatable<ObjectKey>
        {
            private readonly object _obj;
            private readonly Type _subTypeLevel;
            [MethodImpl(ProtoReader.HotPath)]
            public ObjectKey(object obj, Type subTypeLevel)
            {
                _obj = obj;
                _subTypeLevel = subTypeLevel;
            }
            public override string ToString() => $"{_subTypeLevel}/{_obj}";
            [MethodImpl(ProtoReader.HotPath)]
            public override int GetHashCode() => RuntimeHelpers.GetHashCode(_obj) ^ _subTypeLevel.GetHashCode();
            [MethodImpl(ProtoReader.HotPath)]
            public override bool Equals(object obj) => obj is ObjectKey key && Equals(key);
            [MethodImpl(ProtoReader.HotPath)]
            public bool Equals(ObjectKey other) => this._obj == other._obj & this._subTypeLevel == other._subTypeLevel;
        }

        [MethodImpl(ProtoReader.HotPath)]
        public bool TryGetKnownLength(object obj, Type subTypeLevel, out long length)
        {
            if (_knownLengths.TryGetValue(new ObjectKey(obj, subTypeLevel), out length))
            {
                return true;
            }
            else
            {
                length = default;
                return false;
            }
        }

        public void SetKnownLength(object obj, Type subTypeLevel, long length)
        {
            var key = new ObjectKey(obj, subTypeLevel);
            _knownLengths[key] = length;
        }

#if FEAT_DYNAMIC_REF

        private List<object> underlyingList;

        private List<object> List => underlyingList ?? (underlyingList = new List<object>());

        internal const int Root = 0;
        internal object GetKeyedObject(int key)
        {
            if (key-- == Root)
            {
                if (rootObject == null) ThrowHelper.ThrowProtoException("No root object assigned");
                return rootObject;
            }
            var list = List;

            if (key < 0 || key >= list.Count)
            {
                Debug.WriteLine("Missing key: " + key);
                ThrowHelper.ThrowProtoException("Internal error; a missing key occurred");
            }

            object tmp = list[key];
            if (tmp == null)
            {
                ThrowHelper.ThrowProtoException("A deferred key does not have a value yet");
            }
            return tmp;
        }

        internal void SetKeyedObject(int key, object value)
        {
            if (key-- == Root)
            {
                if (value == null) ThrowHelper.ThrowArgumentNullException(nameof(value));
                if (rootObject != null && ((object)rootObject != (object)value)) ThrowHelper.ThrowProtoException("The root object cannot be reassigned");
                rootObject = value;
            }
            else
            {
                var list = List;
                if (key == list.Count)
                {
                    list.Add(value);
                }
                else if (key < list.Count)
                {
                    object oldVal = list[key];
                    if (oldVal == null)
                    {
                        list[key] = value;
                    }
                    else if (!ReferenceEquals(oldVal, value))
                    {
                        ThrowHelper.ThrowProtoException("Reference-tracked objects cannot change reference");
                    } // otherwise was the same; nothing to do
                }
                else
                {
                    ThrowHelper.ThrowProtoException("Internal error; a key mismatch occurred");
                }
            }
        }

        private object rootObject;
        internal int AddObjectKey(object value, out bool existing)
        {
            if (value == null) ThrowHelper.ThrowArgumentNullException(nameof(value));

            if ((object)value == (object)rootObject) // (object) here is no-op, but should be
            {                                        // preserved even if this was typed - needs ref-check
                existing = true;
                return Root;
            }

            string s = value as string;
            var list = List;
            int index;

            if (s == null)
            {
                if (objectKeys == null)
                {
                    objectKeys = new Dictionary<object, int>(ReferenceComparer.Default);
                    index = -1;
                }
                else
                {
                    if (!objectKeys.TryGetValue(value, out index)) index = -1;
                }
            }
            else
            {
                if (stringKeys == null)
                {
                    stringKeys = new Dictionary<string, int>();
                    index = -1;
                }
                else
                {
                    if (!stringKeys.TryGetValue(s, out index)) index = -1;
                }
            }

            if (!(existing = index >= 0))
            {
                index = list.Count;
                list.Add(value);
                if (s == null)
                {
                    objectKeys.Add(value, index);
                }
                else
                {
                    stringKeys.Add(s, index);
                }
            }
            return index + 1;
        }

        private int trapStartIndex; // defaults to 0 - optimization for RegisterTrappedObject
                                    // to make it faster at seeking to find deferred-objects

        internal void RegisterTrappedObject(object value)
        {
            if (rootObject == null)
            {
                rootObject = value;
            }
            else
            {
                if (underlyingList != null)
                {
                    for (int i = trapStartIndex; i < underlyingList.Count; i++)
                    {
                        trapStartIndex = i + 1; // things never *become* null; whether or
                                                // not the next item is null, it will never
                                                // need to be checked again

                        if (underlyingList[i] == null)
                        {
                            underlyingList[i] = value;
                            break;
                        }
                    }
                }
            }
        }

        private Dictionary<string, int> stringKeys;

        private System.Collections.Generic.Dictionary<object, int> objectKeys;
        internal sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public readonly static ReferenceComparer Default = new ReferenceComparer();
            private ReferenceComparer() { }

            bool IEqualityComparer<object>.Equals(object x, object y)
            {
                return x == y; // ref equality
            }

            int IEqualityComparer<object>.GetHashCode(object obj)
            {
                return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
            }
        }
#endif

        internal void Clear()
        {
#if FEAT_DYNAMIC_REF
            trapStartIndex = 0;
            rootObject = null;
            if (underlyingList != null) underlyingList.Clear();
            if (stringKeys != null) stringKeys.Clear();
            if (objectKeys != null) objectKeys.Clear();
#endif
            _knownLengths.Clear();
        }
    }
}