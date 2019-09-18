﻿// this is a modified version of List<T>
// taken from snapshot of https://github.com/dotnet/coreclr/blob/master/src/System.Private.CoreLib/shared/System/Collections/Generic/List.cs
// at commit https://github.com/dotnet/coreclr/commit/7b9509e01b9e1f840b990c56f5b4dfce33002896

#pragma warning disable CS1591 // missing xml comments
#nullable enable

// modified version preserved under existing license in source:
//
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace ProtoBuf.Internal
{
    public static class PooledList
    {
        public static void Dispose<T>(this PooledList<T> list) => ((IDisposable)list)?.Dispose();

        public static void Dispose<T>(this PooledList<T> list, bool cascade)
            where T : class, IDisposable
        {
            if (list != null)
            {
                if (cascade)
                {
                    var span = list.Span;
                    foreach (var item in span)
                        item.Dispose();
                }
                ((IDisposable)list).Dispose();
            }
        }
    }

    // Implements a variable-size List that uses an array of objects to store the
    // elements. A List has a capacity, which is the allocated length
    // of the internal array. As elements are added to a List, the capacity
    // of the List is automatically increased as required by reallocating the
    // internal array.
    //
    [DebuggerDisplay("Count = {Count}")]
    [Serializable]
    public sealed class PooledList<T> : IList<T>, IList, IReadOnlyList<T>, IDisposable
    {
        void IDisposable.Dispose()
        {
            Clear();
            Pool<PooledList<T>>.Put(this);
        }


        /// <summary>
        /// Obtains a span that is a snapshot over the data at the current instance; if
        /// the collection is modified, this span can become invalid
        /// </summary>
        public Span<T> Span => new Span<T>(_items, 0, _size);

        internal const int MaxArrayLength = 0x7fef_ffff;

        private const int DefaultCapacity = 4;

        private T[] _items; // Do not rename (binary serialization)

        private int _size; // Do not rename (binary serialization)
        private int _version; // Do not rename (binary serialization)

        private static readonly T[] s_emptyArray = Array.Empty<T>();

        // Constructs a List. The list is initially empty and has a capacity
        // of zero. Upon adding the first element to the list the capacity is
        // increased to DefaultCapacity, and then increased in multiples of two
        // as required.
        [Obsolete("Prefer " + nameof(Create))]
        public PooledList()
        {
            _items = s_emptyArray;
        }

        private ArrayPool<T> Pool => ArrayPool<T>.Shared;

        public static PooledList<T> Create()
#pragma warning disable CS0618
            => Pool<PooledList<T>>.TryGet() ?? new PooledList<T>();
#pragma warning restore CS0618

        // Constructs a List with a given initial capacity. The list is
        // initially empty, but will have room for the given number of elements
        // before any reallocations are required.
        [Obsolete("Prefer " + nameof(Create))]
        public PooledList(int capacity) : this()
        {
            EnsureCapacity(capacity);
        }

        public static PooledList<T> Create(int capacity)
        {
            var list = Create();
            list.EnsureCapacity(capacity);
            return list;
        }


        // Constructs a List, copying the contents of the given collection. The
        // size and capacity of the new list will both be equal to the size of the
        // given collection.
        [Obsolete("Prefer " + nameof(Create))]
        public PooledList(IEnumerable<T> collection) : this()
        {
            AddRange(collection);
        }

        public static PooledList<T> Create(IEnumerable<T> collection)
        {
            var list = Create();
            list.AddRange(collection);
            return list;
        }

        // Gets and the capacity of this list.
        public int Capacity
        {
            get => _items.Length;
        }

        // Read-only property describing how many elements are in the List.
        public int Count => _size;

        bool IList.IsFixedSize => false;

        // Is this List read-only?
        bool ICollection<T>.IsReadOnly => false;

        bool IList.IsReadOnly => false;

        // Is this List synchronized (thread-safe)?
        bool ICollection.IsSynchronized => false;

        // Synchronization root for this object.
        object ICollection.SyncRoot => this;

        // Sets or Gets the element at the given index.
        public T this[int index]
        {
            get
            {
                // Following trick can reduce the range check by one
                if ((uint)index >= (uint)_size)
                {
                    ThrowHelper.ThrowIndexOutOfRangeException();
                }
                return _items[index];
            }

            set
            {
                if ((uint)index >= (uint)_size)
                {
                    ThrowHelper.ThrowIndexOutOfRangeException();
                }
                _items[index] = value;
                _version++;
            }
        }

        private static bool IsCompatibleObject(object? value)
        {
            // Non-null values are fine.  Only accept nulls if T is a class or Nullable<U>.
            // Note that default(T) is not equal to null for value types except when T is Nullable<U>.
            return ((value is T) || (value == null && default(T)! == null)); // default(T) == null warning (https://github.com/dotnet/roslyn/issues/34757)
        }

        object? IList.this[int index]
        {
            get => this[index];
            set
            {
                if (!TypeHelper<T>.IsObjectType && value == null) ThrowHelper.ThrowArgumentNullException(nameof(value));

                this[index] = (T)value!;
            }
        }

        // Adds the given object to the end of this list. The size of the list is
        // increased by one. If required, the capacity of the list is doubled
        // before adding the new element.
        //
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(T item)
        {
            _version++;
            T[] array = _items;
            int size = _size;
            if ((uint)size < (uint)array.Length)
            {
                _size = size + 1;
                array[size] = item;
            }
            else
            {
                AddWithResize(item);
            }
        }

        // Non-inline from List.Add to improve its code quality as uncommon path
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void AddWithResize(T item)
        {
            int size = _size;
            EnsureCapacity(size + 1);
            _size = size + 1;
            _items[size] = item;
        }

        int IList.Add(object? item)
        {
            if (!TypeHelper<T>.IsObjectType && item == null) ThrowHelper.ThrowArgumentNullException(nameof(item));

            Add((T)item!);

            return Count - 1;
        }

        // Adds the elements of the given collection to the end of this list. If
        // required, the capacity of the list is increased to twice the previous
        // capacity or the new size, whichever is larger.
        //
        public void AddRange(IEnumerable<T> collection)
            => InsertRange(_size, collection);

        public ReadOnlyCollection<T> AsReadOnly()
            => new ReadOnlyCollection<T>(this);

        // Searches a section of the list for a given element using a binary search
        // algorithm. Elements of the list are compared to the search value using
        // the given IComparer interface. If comparer is null, elements of
        // the list are compared to the search value using the IComparable
        // interface, which in that case must be implemented by all elements of the
        // list and the given search value. This method assumes that the given
        // section of the list is already sorted; if this is not the case, the
        // result will be incorrect.
        //
        // The method returns the index of the given value in the list. If the
        // list does not contain the given value, the method returns a negative
        // integer. The bitwise complement operator (~) can be applied to a
        // negative result to produce the index of the first element (if any) that
        // is larger than the given search value. This is also the index at which
        // the search value should be inserted into the list in order for the list
        // to remain sorted.
        //
        // The method uses the Array.BinarySearch method to perform the
        // search.
        //
        public int BinarySearch(int index, int count, T item, IComparer<T>? comparer)
        {
            if (index < 0)
                ThrowHelper.ThrowArgumentOutOfRangeException(nameof(index));
            if (count < 0)
                ThrowHelper.ThrowArgumentOutOfRangeException(nameof(count));
            if (_size - index < count)
                ThrowHelper.ThrowArgumentOutOfRangeException(nameof(count));

            return Array.BinarySearch<T>(_items, index, count, item, comparer);
        }

        public int BinarySearch(T item)
            => BinarySearch(0, Count, item, null);

        public int BinarySearch(T item, IComparer<T>? comparer)
            => BinarySearch(0, Count, item, comparer);

        // Clears the contents of List.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear()
        {
            _version++;
            Recycle(_items, _size);
            _items = Array.Empty<T>();
            _size = 0;
        }

        private void Recycle(T[] array, int length)
        {
            if (array != null)
            {
                if (TypeHelper<T>.IsReferenceOrContainsReferences)
                    Array.Clear(array, 0, length);
                Pool.Return(array);
            }
        }

        // Contains returns true if the specified element is in the List.
        // It does a linear, O(n) search.  Equality is determined by calling
        // EqualityComparer<T>.Default.Equals().
        //
        public bool Contains(T item)
        {
            // PERF: IndexOf calls Array.IndexOf, which internally
            // calls EqualityComparer<T>.Default.IndexOf, which
            // is specialized for different types. This
            // boosts performance since instead of making a
            // virtual method call each iteration of the loop,
            // via EqualityComparer<T>.Default.Equals, we
            // only make one virtual call to EqualityComparer.IndexOf.

            return _size != 0 && IndexOf(item) != -1;
        }

        bool IList.Contains(object? item)
        {
            if (IsCompatibleObject(item))
            {
                return Contains((T)item!);
            }
            return false;
        }

        public PooledList<TOutput> ConvertAll<TOutput>(Converter<T, TOutput> converter)
        {
            if (converter == null)
            {
                ThrowHelper.ThrowArgumentNullException(nameof(converter));
            }

            PooledList<TOutput> list = PooledList<TOutput>.Create(_size);
            for (int i = 0; i < _size; i++)
            {
                list._items[i] = converter!(_items[i]);
            }
            list._size = _size;
            return list;
        }

        // Copies this List into array, which must be of a
        // compatible array type.
        public void CopyTo(T[] array)
            => CopyTo(array, 0);

        // Copies this List into array, which must be of a
        // compatible array type.
        void ICollection.CopyTo(Array array, int arrayIndex)
        {
            if ((array != null) && (array.Rank != 1))
            {
                ThrowHelper.ThrowArgumentException("multi-dimension arrays are not supported", nameof(array));
            }

            // Array.Copy will check for NULL.
            Array.Copy(_items, 0, array!, arrayIndex, _size);
        }

        // Copies a section of this list to the given array at the given index.
        //
        // The method uses the Array.Copy method to copy the elements.
        //
        public void CopyTo(int index, T[] array, int arrayIndex, int count)
        {
            if (_size - index < count)
            {
                ThrowHelper.ThrowArgumentOutOfRangeException(nameof(count));
            }

            // Delegate rest of error checking to Array.Copy.
            Array.Copy(_items, index, array, arrayIndex, count);
        }

        public void CopyTo(T[] array, int arrayIndex)
        {
            // Delegate rest of error checking to Array.Copy.
            Array.Copy(_items, 0, array, arrayIndex, _size);
        }

        // Ensures that the capacity of this list is at least the given minimum
        // value. If the current capacity of the list is less than min, the
        // capacity is increased to twice the current capacity or to min,
        // whichever is larger.
        //
        private void EnsureCapacity(int min)
        {
            if (_items.Length < min)
            {
                int newCapacity = _items.Length == 0 ? DefaultCapacity : _items.Length * 2;
                // Allow the list to grow to maximum possible capacity (~2G elements) before encountering overflow.
                // Note that this check works even when _items.Length overflowed thanks to the (uint) cast
                if ((uint)newCapacity > MaxArrayLength) newCapacity = MaxArrayLength;

                var oldArr = _items;
                var newArr = Pool.Rent(Math.Max(newCapacity, min));
                Array.Copy(oldArr, 0, newArr, 0, _size);
                _items = newArr;
                Recycle(oldArr, _size);
            }
        }

        public bool Exists(Predicate<T> match)
            => FindIndex(match) != -1;

        // [return: MaybeNull]
        public T Find(Predicate<T> match)
        {
            if (match == null)
            {
                ThrowHelper.ThrowArgumentNullException(nameof(match));
            }

            for (int i = 0; i < _size; i++)
            {
                if (match!(_items[i]))
                {
                    return _items[i];
                }
            }
            return default!;
        }

        public PooledList<T> FindAll(Predicate<T> match)
        {
            if (match == null)
            {
                ThrowHelper.ThrowArgumentNullException(nameof(match));
            }

            PooledList<T> list = Create();
            for (int i = 0; i < _size; i++)
            {
                if (match!(_items[i]))
                {
                    list.Add(_items[i]);
                }
            }
            return list;
        }

        public int FindIndex(Predicate<T> match)
            => FindIndex(0, _size, match);

        public int FindIndex(int startIndex, Predicate<T> match)
            => FindIndex(startIndex, _size - startIndex, match);

        public int FindIndex(int startIndex, int count, Predicate<T> match)
        {
            if ((uint)startIndex > (uint)_size)
            {
                ThrowHelper.ThrowArgumentOutOfRangeException(nameof(startIndex));
            }

            if (count < 0 || startIndex > _size - count)
            {
                ThrowHelper.ThrowArgumentOutOfRangeException(nameof(count));
            }

            if (match == null)
            {
                ThrowHelper.ThrowArgumentNullException(nameof(match));
            }

            int endIndex = startIndex + count;
            for (int i = startIndex; i < endIndex; i++)
            {
                if (match!(_items[i])) return i;
            }
            return -1;
        }

        // [return: MaybeNull]
        public T FindLast(Predicate<T> match)
        {
            if (match == null)
            {
                ThrowHelper.ThrowArgumentNullException(nameof(match));
            }

            for (int i = _size - 1; i >= 0; i--)
            {
                if (match!(_items[i]))
                {
                    return _items[i];
                }
            }
            return default!;
        }

        public int FindLastIndex(Predicate<T> match)
            => FindLastIndex(_size - 1, _size, match);

        public int FindLastIndex(int startIndex, Predicate<T> match)
            => FindLastIndex(startIndex, startIndex + 1, match);

        public int FindLastIndex(int startIndex, int count, Predicate<T> match)
        {
            if (match == null)
            {
                ThrowHelper.ThrowArgumentNullException(nameof(match));
            }

            if (_size == 0)
            {
                // Special case for 0 length List
                if (startIndex != -1)
                {
                    ThrowHelper.ThrowArgumentOutOfRangeException(nameof(startIndex));
                }
            }
            else
            {
                // Make sure we're not out of range
                if ((uint)startIndex >= (uint)_size)
                {
                    ThrowHelper.ThrowArgumentOutOfRangeException(nameof(startIndex));
                }
            }

            // 2nd have of this also catches when startIndex == MAXINT, so MAXINT - 0 + 1 == -1, which is < 0.
            if (count < 0 || startIndex - count + 1 < 0)
            {
                ThrowHelper.ThrowArgumentOutOfRangeException(nameof(count));
            }

            int endIndex = startIndex - count;
            for (int i = startIndex; i > endIndex; i--)
            {
                if (match!(_items[i]))
                {
                    return i;
                }
            }
            return -1;
        }

        public void ForEach(Action<T> action)
        {
            if (action == null)
            {
                ThrowHelper.ThrowArgumentNullException(nameof(action));
            }

            int version = _version;

            for (int i = 0; i < _size; i++)
            {
                if (version != _version)
                {
                    break;
                }
                action!(_items[i]);
            }

            if (version != _version)
                ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumFailedVersion();
        }

        // Returns an enumerator for this list with the given
        // permission for removal of elements. If modifications made to the list
        // while an enumeration is in progress, the MoveNext and
        // GetObject methods of the enumerator will throw an exception.
        //
        public Enumerator GetEnumerator()
            => new Enumerator(this);

        IEnumerator<T> IEnumerable<T>.GetEnumerator()
            => new Enumerator(this);

        IEnumerator IEnumerable.GetEnumerator()
            => new Enumerator(this);

        public PooledList<T> GetRange(int index, int count)
        {
            if (index < 0)
            {
                ThrowHelper.ThrowArgumentOutOfRangeException(nameof(index));
            }

            if (count < 0)
            {
                ThrowHelper.ThrowArgumentOutOfRangeException(nameof(count));
            }

            if (_size - index < count)
            {
                ThrowHelper.ThrowArgumentOutOfRangeException(nameof(count));
            }

            PooledList<T> list = PooledList<T>.Create(count);
            Array.Copy(_items, index, list._items, 0, count);
            list._size = count;
            return list;
        }


        // Returns the index of the first occurrence of a given value in a range of
        // this list. The list is searched forwards from beginning to end.
        // The elements of the list are compared to the given value using the
        // Object.Equals method.
        //
        // This method uses the Array.IndexOf method to perform the
        // search.
        //
        public int IndexOf(T item)
            => Array.IndexOf(_items, item, 0, _size);

        int IList.IndexOf(object? item)
        {
            if (IsCompatibleObject(item))
            {
                return IndexOf((T)item!);
            }
            return -1;
        }

        // Returns the index of the first occurrence of a given value in a range of
        // this list. The list is searched forwards, starting at index
        // index and ending at count number of elements. The
        // elements of the list are compared to the given value using the
        // Object.Equals method.
        //
        // This method uses the Array.IndexOf method to perform the
        // search.
        //
        public int IndexOf(T item, int index)
        {
            if (index > _size)
                ThrowHelper.ThrowIndexOutOfRangeException();
            return Array.IndexOf(_items, item, index, _size - index);
        }

        // Returns the index of the first occurrence of a given value in a range of
        // this list. The list is searched forwards, starting at index
        // index and upto count number of elements. The
        // elements of the list are compared to the given value using the
        // Object.Equals method.
        //
        // This method uses the Array.IndexOf method to perform the
        // search.
        //
        public int IndexOf(T item, int index, int count)
        {
            if (index > _size)
                ThrowHelper.ThrowIndexOutOfRangeException();

            if (count < 0 || index > _size - count)
                ThrowHelper.ThrowArgumentOutOfRangeException(nameof(count));

            return Array.IndexOf(_items, item, index, count);
        }

        // Inserts an element into this list at a given index. The size of the list
        // is increased by one. If required, the capacity of the list is doubled
        // before inserting the new element.
        //
        public void Insert(int index, T item)
        {
            // Note that insertions at the end are legal.
            if ((uint)index > (uint)_size)
            {
                ThrowHelper.ThrowArgumentOutOfRangeException(nameof(index));
            }
            if (_size == _items.Length) EnsureCapacity(_size + 1);
            if (index < _size)
            {
                Array.Copy(_items, index, _items, index + 1, _size - index);
            }
            _items[index] = item;
            _size++;
            _version++;
        }

        void IList.Insert(int index, object? item)
        {
            if (!TypeHelper<T>.IsObjectType && item == null) ThrowHelper.ThrowArgumentNullException(nameof(item));

            Insert(index, (T)item!);
        }

        // Inserts the elements of the given collection at a given index. If
        // required, the capacity of the list is increased to twice the previous
        // capacity or the new size, whichever is larger.  Ranges may be added
        // to the end of the list by setting index to the List's size.
        //
        public void InsertRange(int index, IEnumerable<T> collection)
        {
            if (collection == null)
            {
                ThrowHelper.ThrowArgumentNullException(nameof(collection));
            }

            if ((uint)index > (uint)_size)
            {
                ThrowHelper.ThrowIndexOutOfRangeException();
            }

            if (collection is ICollection<T> c)
            {
                int count = c.Count;
                if (count > 0)
                {
                    EnsureCapacity(_size + count);
                    if (index < _size)
                    {
                        Array.Copy(_items, index, _items, index + count, _size - index);
                    }

                    // If we're inserting a List into itself, we want to be able to deal with that.
                    if (this == c)
                    {
                        // Copy first part of _items to insert location
                        Array.Copy(_items, 0, _items, index, index);
                        // Copy last part of _items back to inserted location
                        Array.Copy(_items, index + count, _items, index * 2, _size - index);
                    }
                    else
                    {
                        c.CopyTo(_items, index);
                    }
                    _size += count;
                }
            }
            else
            {
                using IEnumerator<T> en = collection!.GetEnumerator();
                while (en.MoveNext())
                {
                    Insert(index++, en.Current);
                }
            }
            _version++;
        }

        // Returns the index of the last occurrence of a given value in a range of
        // this list. The list is searched backwards, starting at the end
        // and ending at the first element in the list. The elements of the list
        // are compared to the given value using the Object.Equals method.
        //
        // This method uses the Array.LastIndexOf method to perform the
        // search.
        //
        public int LastIndexOf(T item)
        {
            if (_size == 0)
            {  // Special case for empty list
                return -1;
            }
            else
            {
                return LastIndexOf(item, _size - 1, _size);
            }
        }

        // Returns the index of the last occurrence of a given value in a range of
        // this list. The list is searched backwards, starting at index
        // index and ending at the first element in the list. The
        // elements of the list are compared to the given value using the
        // Object.Equals method.
        //
        // This method uses the Array.LastIndexOf method to perform the
        // search.
        //
        public int LastIndexOf(T item, int index)
        {
            if (index >= _size)
                ThrowHelper.ThrowIndexOutOfRangeException();
            return LastIndexOf(item, index, index + 1);
        }

        // Returns the index of the last occurrence of a given value in a range of
        // this list. The list is searched backwards, starting at index
        // index and upto count elements. The elements of
        // the list are compared to the given value using the Object.Equals
        // method.
        //
        // This method uses the Array.LastIndexOf method to perform the
        // search.
        //
        public int LastIndexOf(T item, int index, int count)
        {
            if ((Count != 0) && (index < 0))
            {
                ThrowHelper.ThrowIndexOutOfRangeException();
            }

            if ((Count != 0) && (count < 0))
            {
                ThrowHelper.ThrowArgumentOutOfRangeException(nameof(count));
            }

            if (_size == 0)
            {  // Special case for empty list
                return -1;
            }

            if (index >= _size)
            {
                ThrowHelper.ThrowArgumentOutOfRangeException(nameof(index));
            }

            if (count > index + 1)
            {
                ThrowHelper.ThrowArgumentOutOfRangeException(nameof(count));
            }

            return Array.LastIndexOf(_items, item, index, count);
        }

        // Removes the element at the given index. The size of the list is
        // decreased by one.
        public bool Remove(T item)
        {
            int index = IndexOf(item);
            if (index >= 0)
            {
                RemoveAt(index);
                return true;
            }

            return false;
        }

        void IList.Remove(object? item)
        {
            if (IsCompatibleObject(item))
            {
                Remove((T)item!);
            }
        }

        // This method removes all items which matches the predicate.
        // The complexity is O(n).
        public int RemoveAll(Predicate<T> match)
        {
            if (match == null)
            {
                ThrowHelper.ThrowArgumentNullException(nameof(match));
            }

            int freeIndex = 0;   // the first free slot in items array

            // Find the first item which needs to be removed.
            while (freeIndex < _size && !match!(_items[freeIndex])) freeIndex++;
            if (freeIndex >= _size) return 0;

            int current = freeIndex + 1;
            while (current < _size)
            {
                // Find the first item which needs to be kept.
                while (current < _size && match!(_items[current])) current++;

                if (current < _size)
                {
                    // copy item to the free slot.
                    _items[freeIndex++] = _items[current++];
                }
            }

            if (TypeHelper<T>.IsReferenceOrContainsReferences)
            {
                Array.Clear(_items, freeIndex, _size - freeIndex); // Clear the elements so that the gc can reclaim the references.
            }

            int result = _size - freeIndex;
            _size = freeIndex;
            _version++;
            return result;
        }

        // Removes the element at the given index. The size of the list is
        // decreased by one.
        public void RemoveAt(int index)
        {
            if ((uint)index >= (uint)_size)
            {
                ThrowHelper.ThrowIndexOutOfRangeException();
            }
            _size--;
            if (index < _size)
            {
                Array.Copy(_items, index + 1, _items, index, _size - index);
            }
            if (TypeHelper<T>.IsReferenceOrContainsReferences)
            {
                _items[_size] = default!;
            }
            _version++;
        }

        // Removes a range of elements from this list.
        public void RemoveRange(int index, int count)
        {
            if (index < 0)
            {
                ThrowHelper.ThrowIndexOutOfRangeException();
            }

            if (count < 0)
            {
                ThrowHelper.ThrowArgumentOutOfRangeException(nameof(count));
            }

            if (_size - index < count)
                ThrowHelper.ThrowArgumentOutOfRangeException(nameof(count));

            if (count > 0)
            {
                _size -= count;
                if (index < _size)
                {
                    Array.Copy(_items, index + count, _items, index, _size - index);
                }

                _version++;
                if (TypeHelper<T>.IsReferenceOrContainsReferences)
                {
                    Array.Clear(_items, _size, count);
                }
            }
        }

        // Reverses the elements in this list.
        public void Reverse()
            => Reverse(0, Count);

        // Reverses the elements in a range of this list. Following a call to this
        // method, an element in the range given by index and count
        // which was previously located at index i will now be located at
        // index index + (index + count - i - 1).
        //
        public void Reverse(int index, int count)
        {
            if (index < 0)
            {
                ThrowHelper.ThrowIndexOutOfRangeException();
            }

            if (count < 0)
            {
                ThrowHelper.ThrowArgumentOutOfRangeException(nameof(count));
            }

            if (_size - index < count)
                ThrowHelper.ThrowArgumentOutOfRangeException(nameof(count));

            if (count > 1)
            {
                Array.Reverse(_items, index, count);
            }
            _version++;
        }

        // Sorts the elements in this list.  Uses the default comparer and
        // Array.Sort.
        public void Sort()
            => Sort(0, Count, null);

        // Sorts the elements in this list.  Uses Array.Sort with the
        // provided comparer.
        public void Sort(IComparer<T>? comparer)
            => Sort(0, Count, comparer);

        // Sorts the elements in a section of this list. The sort compares the
        // elements to each other using the given IComparer interface. If
        // comparer is null, the elements are compared to each other using
        // the IComparable interface, which in that case must be implemented by all
        // elements of the list.
        //
        // This method uses the Array.Sort method to sort the elements.
        //
        public void Sort(int index, int count, IComparer<T>? comparer)
        {
            if (index < 0)
            {
                ThrowHelper.ThrowIndexOutOfRangeException();
            }

            if (count < 0)
            {
                ThrowHelper.ThrowArgumentOutOfRangeException(nameof(count));
            }

            if (_size - index < count)
                ThrowHelper.ThrowArgumentOutOfRangeException(nameof(count));

            if (count > 1)
            {
                Array.Sort<T>(_items, index, count, comparer);
            }
            _version++;
        }

        // ToArray returns an array containing the contents of the List.
        // This requires copying the List, which is an O(n) operation.
        public T[] ToArray()
        {
            if (_size == 0)
            {
                return s_emptyArray;
            }

            T[] array = new T[_size];
            Array.Copy(_items, 0, array, 0, _size);
            return array;
        }

        public bool TrueForAll(Predicate<T> match)
        {
            if (match == null)
            {
                ThrowHelper.ThrowArgumentNullException(nameof(match));
            }

            for (int i = 0; i < _size; i++)
            {
                if (!match!(_items[i]))
                {
                    return false;
                }
            }
            return true;
        }

        public struct Enumerator : IEnumerator<T>, IEnumerator
        {
            private readonly PooledList<T> _list;
            private int _index;
            private readonly int _version;
            //[AllowNull, MaybeNull]
            private T _current;

            internal Enumerator(PooledList<T> list)
            {
                _list = list;
                _index = 0;
                _version = list._version;
                _current = default!;
            }

            public void Dispose()
            {
            }

            public bool MoveNext()
            {
                PooledList<T> localList = _list;

                if (_version == localList._version && ((uint)_index < (uint)localList._size))
                {
                    _current = localList._items[_index];
                    _index++;
                    return true;
                }
                return MoveNextRare();
            }

            private bool MoveNextRare()
            {
                if (_version != _list._version)
                {
                    ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumFailedVersion();
                }

                _index = _list._size + 1;
                _current = default!;
                return false;
            }

            public T Current => _current!;

            object? IEnumerator.Current
            {
                get
                {
                    if (_index == 0 || _index == _list._size + 1)
                    {
                        ThrowHelper.ThrowInvalidOperationException();
                    }
                    return Current;
                }
            }

            void IEnumerator.Reset()
            {
                if (_version != _list._version)
                {
                    ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumFailedVersion();
                }

                _index = 0;
                _current = default!;
            }
        }
    }
}