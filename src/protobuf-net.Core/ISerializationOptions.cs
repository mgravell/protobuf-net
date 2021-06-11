using System;

namespace ProtoBuf
{
    /// <summary>
    /// Describes additional optional serialization behaviours
    /// </summary>
    public interface ISerializationOptions
    {
        /// <summary>
        /// Gets the additional options to consider for this operation
        /// </summary>
        SerializationOptions Options { get; }
    }

    /// <summary>
    /// Describes additional optional serialization behaviours
    /// </summary>
    [Flags]
    public enum SerializationOptions
    {
        /// <summary>
        /// No additional options
        /// </summary>
        None = 0,

        /// <summary>
        /// Interpret trailing zeros in data as EOF, rather than throwing an error
        /// </summary>
        AllowZeroPadding = 1 << 0,
    }
}
