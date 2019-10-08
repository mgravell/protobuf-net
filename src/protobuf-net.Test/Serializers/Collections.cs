﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using Xunit;

namespace ProtoBuf.Serializers
{
    public class Collections
    {
        [Theory]
        [InlineData(typeof(int[]), typeof(VectorSerializer<int>))]
        [InlineData(typeof(int[,]), null)]
        [InlineData(typeof(List<int>), typeof(ListSerializer<int>))]
        [InlineData(typeof(ListGenericSubclass<int>), typeof(ListSerializer<ListGenericSubclass<int>, int>))]
        [InlineData(typeof(ListNonGenericSubclass), typeof(ListSerializer<ListNonGenericSubclass, int>))]
        [InlineData(typeof(Collection<int>), typeof(CollectionSerializer<Collection<int>, Collection<int>, int>))]
        [InlineData(typeof(ICollection<int>), typeof(CollectionSerializer<ICollection<int>, ICollection<int>, int>))]
        [InlineData(typeof(IList<int>), typeof(CollectionSerializer<IList<int>, IList<int>, int>))]
        [InlineData(typeof(Dictionary<int, string>), typeof(DictionarySerializer<int, string>))]
        [InlineData(typeof(IDictionary<int, string>), typeof(DictionarySerializer<IDictionary<int,string>,int, string>))]
        [InlineData(typeof(ImmutableArray<int>), typeof(ImmutableArraySerializer<int>))]
        [InlineData(typeof(ImmutableDictionary<int, string>), typeof(ImmutableDictionarySerializer<int, string>))]
        [InlineData(typeof(ImmutableSortedDictionary<int, string>), typeof(ImmutableSortedDictionarySerializer<int, string>))]
        [InlineData(typeof(IImmutableDictionary<int, string>), typeof(ImmutableIDictionarySerializer<int, string>))]
        [InlineData(typeof(Queue<int>), typeof(QueueSerializer<int>))]
        [InlineData(typeof(Stack<int>), typeof(StackSerializer<int>))]
        [InlineData(typeof(CustomGenericCollection<int>), typeof(CollectionSerializer<CustomGenericCollection<int>, CustomGenericCollection<int>,int>))]
        [InlineData(typeof(CustomNonGenericCollection), typeof(CollectionSerializer<CustomNonGenericCollection,CustomNonGenericCollection,bool>))]
        [InlineData(typeof(IReadOnlyCollection<string>), typeof(ReadOnlyCollectionSerializer<IReadOnlyCollection<string>, IReadOnlyCollection<string>, string>))]
        [InlineData(typeof(CustomNonGenericReadOnlyCollection), typeof(ReadOnlyCollectionSerializer<CustomNonGenericReadOnlyCollection, CustomNonGenericReadOnlyCollection, string>))]
        [InlineData(typeof(CustomGenericReadOnlyCollection<string>), typeof(ReadOnlyCollectionSerializer<CustomGenericReadOnlyCollection<string>, CustomGenericReadOnlyCollection<string>, string>))]

        public void TestWhatProviderWeGet(Type type, Type expected)
        {
            var provider = RepeatedSerializers.TryGetRepeatedProvider(type);
            if (expected == null)
            {
                Assert.Null(provider);
            }
            else
            {
                Assert.NotNull(provider);
                var ser = provider.Serializer;
                Assert.NotNull(ser);
                Assert.Equal(expected, ser.GetType());
            }
        }

        public class ListGenericSubclass<T> : List<T> { }
        public class ListNonGenericSubclass : List<int> { }

        public class CustomNonGenericReadOnlyCollection : IReadOnlyCollection<string>
        {
            int IReadOnlyCollection<string>.Count => throw new NotImplementedException();
            IEnumerator<string> IEnumerable<string>.GetEnumerator() => throw new NotImplementedException();
            IEnumerator IEnumerable.GetEnumerator() => throw new NotImplementedException();
        }
        public class CustomGenericReadOnlyCollection<T> : IReadOnlyCollection<T>
        {
            int IReadOnlyCollection<T>.Count => throw new NotImplementedException();
            IEnumerator<T> IEnumerable<T>.GetEnumerator() => throw new NotImplementedException();
            IEnumerator IEnumerable.GetEnumerator() => throw new NotImplementedException();
        }

        public class CustomGenericCollection<T> : IList<T>
        {
            #region nope
            T IList<T>.this[int index] {
                get => throw new NotImplementedException();
                set => throw new NotImplementedException();
            }
            int ICollection<T>.Count => throw new NotImplementedException();
            bool ICollection<T>.IsReadOnly => throw new NotImplementedException();
            void ICollection<T>.Add(T item) => throw new NotImplementedException();
            void ICollection<T>.Clear() => throw new NotImplementedException();
            bool ICollection<T>.Contains(T item) => throw new NotImplementedException();
            void ICollection<T>.CopyTo(T[] array, int arrayIndex) => throw new NotImplementedException();
            IEnumerator<T> IEnumerable<T>.GetEnumerator() => throw new NotImplementedException();
            IEnumerator IEnumerable.GetEnumerator() => throw new NotImplementedException();
            int IList<T>.IndexOf(T item) => throw new NotImplementedException();
            void IList<T>.Insert(int index, T item) => throw new NotImplementedException();
            bool ICollection<T>.Remove(T item) => throw new NotImplementedException();
            void IList<T>.RemoveAt(int index) => throw new NotImplementedException();
            #endregion
        }
        public class CustomNonGenericCollection : ICollection<bool>
        {
            #region nope
            int ICollection<bool>.Count => throw new NotImplementedException();
            bool ICollection<bool>.IsReadOnly => throw new NotImplementedException();
            void ICollection<bool>.Add(bool item) => throw new NotImplementedException();
            void ICollection<bool>.Clear() => throw new NotImplementedException();
            bool ICollection<bool>.Contains(bool item) => throw new NotImplementedException();
            void ICollection<bool>.CopyTo(bool[] array, int arrayIndex) => throw new NotImplementedException();
            IEnumerator<bool> IEnumerable<bool>.GetEnumerator() => throw new NotImplementedException();
            IEnumerator IEnumerable.GetEnumerator() => throw new NotImplementedException();
            bool ICollection<bool>.Remove(bool item) => throw new NotImplementedException();
            #endregion
        }

    }
}
