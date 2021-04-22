using ProtoBuf.Internal;
using System;

namespace ProtoBuf
{
    /// <summary>
    /// Used to define protocol-buffer specific behavior for
    /// enumerated values.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    public sealed class ProtoEnumAttribute : Attribute
    {
        internal const string EnumValueDeprecated = "Enum value maps have been deprecated and are no longer supported; all enums are now effectively pass-thru; custom maps should be applied via shadow properties; in C#, lambda-based 'switch expressions' make for very convenient shadow properties";


        /// <summary>
        /// Gets or sets the specific value to use for this enum during serialization.
        /// </summary>
        public int Value
        {
            [Obsolete(EnumValueDeprecated, false)]
            get { return enumValue; }
#if DEBUG
            [Obsolete(EnumValueDeprecated, false)]
#else
            [Obsolete(EnumValueDeprecated, true)]
#endif
            set { this.enumValue = value; hasValue = true; }
        }

        /// <summary>
        /// Indicates whether this instance has a customised value mapping
        /// </summary>
        /// <returns>true if a specific value is set</returns>
        [Obsolete(EnumValueDeprecated, false)]
        public bool HasValue() => hasValue;

        private bool hasValue;
        private int enumValue;

        /// <summary>
        /// Gets or sets the defined name of the enum, as used in .proto
        /// (this name is not used during serialization).
        /// </summary>
        public string Name { get; set; }
    }
}
