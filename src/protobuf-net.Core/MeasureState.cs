﻿using ProtoBuf.Internal;
using ProtoBuf.Meta;
using System;
using System.Buffers;
using System.IO;

namespace ProtoBuf
{
    /// <summary>
    /// Represents the outcome of computing the length of an object; since this may have required computing lengths
    /// for multiple objects, some metadata is retained so that a subsequent serialize operation using
    /// this instance can re-use the previously calculated lengths. If the object state changes between the
    /// measure and serialize operations, the behavior is undefined.
    /// </summary>
    public struct MeasureState<T> : IDisposable
    {
        private readonly TypeModel _model;
        private readonly T _value;
        private readonly object _userState;

#pragma warning disable IDE0069 // "not disposed" yes it is - see Dispose (it just isn't a trivial case)
        private ProtoWriter _writer;
#pragma warning restore IDE0069

        internal MeasureState(TypeModel model, in T value, object userState)
        {
            _model = model;
            _value = value;
            _userState = userState;
            var nullState = ProtoWriter.NullProtoWriter.CreateNullProtoWriter(_model, userState);
            try
            {
                Length = TypeModel.SerializeImpl<T>(ref nullState, _value);
                _writer = nullState.GetWriter();
            }
            catch
            {
                nullState.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Releases all resources associated with this value
        /// </summary>
        public void Dispose()
        {
            var writer = _writer;
            _writer = null;
            writer?.Dispose();
        }

        /// <summary>
        /// Gets the calculated length of this serialize operation, im bytes
        /// </summary>
        public long Length { get; }

        /// <summary>
        /// Returns the calculated length, disposing the value as a side-effect
        /// </summary>
        public long LengthOnly()
        {
            var len = Length;
            Dispose();
            return len;
        }

        private void SerializeCore(ProtoWriter.State state)
        {
            try
            {
                var writer = _writer;
                if (writer == null) throw new ObjectDisposedException(nameof(MeasureState<T>));

                var targetWriter = state.GetWriter();
                targetWriter.InitializeFrom(writer);
                long actual = TypeModel.SerializeImpl<T>(ref state, _value);
                targetWriter.CopyBack(writer);

                if (actual != Length) ThrowHelper.ThrowInvalidOperationException($"Invalid length; expected {Length}, actual: {actual}");
            }
            finally
            {
                state.Dispose();
            }
        }

        internal int GetLengthHits(out int misses) => _writer.GetLengthHits(out misses);

        /// <summary>
        /// Perform the calculated serialization operation against the provided target stream. If the object state changes between the
        /// measure and serialize operations, the behavior is undefined.
        /// </summary>
        public void Serialize(Stream stream) => SerializeCore(ProtoWriter.State.Create(stream, _model, _userState));

        /// <summary>
        /// Perform the calculated serialization operation against the provided target writer. If the object state changes between the
        /// measure and serialize operations, the behavior is undefined.
        /// </summary>
        public void Serialize(IBufferWriter<byte> writer) => SerializeCore(ProtoWriter.State.Create(writer, _model, _userState));
    }
}
