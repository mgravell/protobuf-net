﻿using System;

using ProtoBuf.Serializers;
using System.Globalization;
using System.Collections.Generic;
using System.Reflection;
using System.Diagnostics;
using ProtoBuf.Internal;
using ProtoBuf.Internal.Serializers;

namespace ProtoBuf.Meta
{
    /// <summary>
    /// Represents a member (property/field) that is mapped to a protobuf field
    /// </summary>
    public class ValueMember
    {
        /// <summary>
        /// The number that identifies this member in a protobuf stream
        /// </summary>
        public int FieldNumber { get; }

        private MemberInfo backingMember;
        /// <summary>
        /// Gets the member (field/property) which this member relates to.
        /// </summary>
        public MemberInfo Member { get; }

        /// <summary>
        /// Gets the backing member (field/property) which this member relates to
        /// </summary>
        public MemberInfo BackingMember
        {
            get { return backingMember; }
            set
            {
                if (backingMember != value)
                {
                    ThrowIfFrozen();
                    backingMember = value;
                }
            }
        }

        private object _defaultValue;

        /// <summary>
        /// Within a list / array / etc, the type of object for each item in the list (especially useful with ArrayList)
        /// </summary>
        public Type ItemType { get; }

        /// <summary>
        /// The underlying type of the member
        /// </summary>
        public Type MemberType { get; }

        /// <summary>
        /// For abstract types (IList etc), the type of concrete object to create (if required)
        /// </summary>
        public Type DefaultType { get; }

        /// <summary>
        /// The type the defines the member
        /// </summary>
        public Type ParentType { get; }

        /// <summary>
        /// The default value of the item (members with this value will not be serialized)
        /// </summary>
        public object DefaultValue
        {
            get { return _defaultValue; }
            set
            {
                if (_defaultValue != value)
                {
                    ThrowIfFrozen();
                    _defaultValue = value;
                }
            }
        }

        private readonly RuntimeTypeModel model;
        /// <summary>
        /// Creates a new ValueMember instance
        /// </summary>
        public ValueMember(RuntimeTypeModel model, Type parentType, int fieldNumber, MemberInfo member, Type memberType, Type itemType, Type defaultType, DataFormat dataFormat, object defaultValue)
            : this(model, fieldNumber, memberType, itemType, defaultType, dataFormat)
        {
            if (parentType == null) throw new ArgumentNullException(nameof(parentType));
            if (fieldNumber < 1 && !parentType.IsEnum) throw new ArgumentOutOfRangeException(nameof(fieldNumber));

            Member = member ?? throw new ArgumentNullException(nameof(member));
            ParentType = parentType;
            if (fieldNumber < 1 && !parentType.IsEnum) throw new ArgumentOutOfRangeException(nameof(fieldNumber));
            
            if (defaultValue != null && (defaultValue.GetType() != memberType))
            {
                defaultValue = ParseDefaultValue(memberType, defaultValue);
            }
            _defaultValue = defaultValue;

            MetaType type = model.FindWithoutAdd(memberType);
#if FEAT_DYNAMIC_REF
            if (type != null)
            {
                AsReference = type.AsReferenceDefault;
            }
            else
            { // we need to scan the hard way; can't risk recursion by fully walking it
                AsReference = MetaType.GetAsReferenceDefault(memberType);
            }
#endif
        }
        /// <summary>
        /// Creates a new ValueMember instance
        /// </summary>
        internal ValueMember(RuntimeTypeModel model, int fieldNumber, Type memberType, Type itemType, Type defaultType, DataFormat dataFormat)
        {
            FieldNumber = fieldNumber;
            MemberType = memberType ?? throw new ArgumentNullException(nameof(memberType));
            ItemType = itemType;
            if (defaultType == null && itemType != null)
            {   // reasonable default
                defaultType = memberType;
            }
            DefaultType = defaultType;

            this.model = model ?? throw new ArgumentNullException(nameof(model));
            this.dataFormat = dataFormat;
        }
        internal object GetRawEnumValue()
        {
            return ((FieldInfo)Member).GetRawConstantValue();
        }
        private static object ParseDefaultValue(Type type, object value)
        {
            {
                Type tmp = Nullable.GetUnderlyingType(type);
                if (tmp != null) type = tmp;
            }
            if (value is string s)
            {
                if (type.IsEnum) return Enum.Parse(type, s, true);

                switch (Helpers.GetTypeCode(type))
                {
                    case ProtoTypeCode.Boolean: return bool.Parse(s);
                    case ProtoTypeCode.Byte: return byte.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);
                    case ProtoTypeCode.Char: // char.Parse missing on CF/phone7
                        if (s.Length == 1) return s[0];
                        throw new FormatException("Single character expected: \"" + s + "\"");
                    case ProtoTypeCode.DateTime: return DateTime.Parse(s, CultureInfo.InvariantCulture);
                    case ProtoTypeCode.Decimal: return decimal.Parse(s, NumberStyles.Any, CultureInfo.InvariantCulture);
                    case ProtoTypeCode.Double: return double.Parse(s, NumberStyles.Any, CultureInfo.InvariantCulture);
                    case ProtoTypeCode.Int16: return short.Parse(s, NumberStyles.Any, CultureInfo.InvariantCulture);
                    case ProtoTypeCode.Int32: return int.Parse(s, NumberStyles.Any, CultureInfo.InvariantCulture);
                    case ProtoTypeCode.Int64: return long.Parse(s, NumberStyles.Any, CultureInfo.InvariantCulture);
                    case ProtoTypeCode.SByte: return sbyte.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);
                    case ProtoTypeCode.Single: return float.Parse(s, NumberStyles.Any, CultureInfo.InvariantCulture);
                    case ProtoTypeCode.String: return s;
                    case ProtoTypeCode.UInt16: return ushort.Parse(s, NumberStyles.Any, CultureInfo.InvariantCulture);
                    case ProtoTypeCode.UInt32: return uint.Parse(s, NumberStyles.Any, CultureInfo.InvariantCulture);
                    case ProtoTypeCode.UInt64: return ulong.Parse(s, NumberStyles.Any, CultureInfo.InvariantCulture);
                    case ProtoTypeCode.TimeSpan: return TimeSpan.Parse(s);
                    case ProtoTypeCode.Uri: return s; // Uri is decorated as string
                    case ProtoTypeCode.Guid: return new Guid(s);
                }
            }

            if (type.IsEnum) return Enum.ToObject(type, value);
            return Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
        }

        private IRuntimeProtoSerializerNode serializer;
        internal IRuntimeProtoSerializerNode Serializer
        {
            get
            {
                return serializer ?? (serializer = BuildSerializer());
            }
        }

        private DataFormat dataFormat;
        /// <summary>
        /// Specifies the rules used to process the field; this is used to determine the most appropriate
        /// wite-type, but also to describe subtypes <i>within</i> that wire-type (such as SignedVariant)
        /// </summary>
        public DataFormat DataFormat
        {
            get { return dataFormat; }
            set
            {
                if (value != dataFormat)
                {
                    ThrowIfFrozen();
                    this.dataFormat = value;
                }
            }
        }

        /// <summary>
        /// Indicates whether this field should follow strict encoding rules; this means (for example) that if a "fixed32"
        /// is encountered when "variant" is defined, then it will fail (throw an exception) when parsing. Note that
        /// when serializing the defined type is always used.
        /// </summary>
        public bool IsStrict
        {
            get { return HasFlag(OPTIONS_IsStrict); }
            set { SetFlag(OPTIONS_IsStrict, value, true); }
        }

        /// <summary>
        /// Indicates whether this field should use packed encoding (which can save lots of space for repeated primitive values).
        /// This option only applies to list/array data of primitive types (int, double, etc).
        /// </summary>
        public bool IsPacked
        {
            get { return HasFlag(OPTIONS_IsPacked); }
            set { SetFlag(OPTIONS_IsPacked, value, true); }
        }

        /// <summary>
        /// Indicates whether this field should *replace* existing values (the default is false, meaning *append*).
        /// This option only applies to list/array data.
        /// </summary>
        public bool OverwriteList
        {
            get { return HasFlag(OPTIONS_OverwriteList); }
            set { SetFlag(OPTIONS_OverwriteList, value, true); }
        }

        /// <summary>
        /// Indicates whether this field is mandatory.
        /// </summary>
        public bool IsRequired
        {
            get { return HasFlag(OPTIONS_IsRequired); }
            set { SetFlag(OPTIONS_IsRequired, value, true); }
        }

        /// <summary>
        /// Enables full object-tracking/full-graph support.
        /// </summary>
        public bool AsReference
        {
#if FEAT_DYNAMIC_REF
            get { return HasFlag(OPTIONS_AsReference); }
            set { SetFlag(OPTIONS_AsReference, value, true); }
#else
            get => false;
            [Obsolete(ProtoContractAttribute.ReferenceDynamicDisabled, true)]
            set { if (value != AsReference) ThrowHelper.ThrowNotSupportedException(); }
#endif
        }

        /// <summary>
        /// Embeds the type information into the stream, allowing usage with types not known in advance.
        /// </summary>
        public bool DynamicType
        {
#if FEAT_DYNAMIC_REF
            get { return HasFlag(OPTIONS_DynamicType); }
            set { SetFlag(OPTIONS_DynamicType, value, true); }
#else
            get => false;
            [Obsolete(ProtoContractAttribute.ReferenceDynamicDisabled, true)]
            set { if (value != DynamicType) ThrowHelper.ThrowNotSupportedException(); }
#endif
        }

        /// <summary>
        /// Indicates that the member should be treated as a protobuf Map
        /// </summary>
        public bool IsMap
        {
            get { return HasFlag(OPTIONS_IsMap); }
            set { SetFlag(OPTIONS_IsMap, value, true); }
        }

        private DataFormat mapKeyFormat, mapValueFormat;
        /// <summary>
        /// Specifies the data-format that should be used for the key, when IsMap is enabled
        /// </summary>
        public DataFormat MapKeyFormat
        {
            get { return mapKeyFormat; }
            set
            {
                if (mapKeyFormat != value)
                {
                    ThrowIfFrozen();
                    mapKeyFormat = value;
                }
            }
        }
        /// <summary>
        /// Specifies the data-format that should be used for the value, when IsMap is enabled
        /// </summary>
        public DataFormat MapValueFormat
        {
            get { return mapValueFormat; }
            set
            {
                if (mapValueFormat != value)
                {
                    ThrowIfFrozen();
                    mapValueFormat = value;
                }
            }
        }

        private MethodInfo getSpecified, setSpecified;
        /// <summary>
        /// Specifies methods for working with optional data members.
        /// </summary>
        /// <param name="getSpecified">Provides a method (null for none) to query whether this member should
        /// be serialized; it must be of the form "bool {Method}()". The member is only serialized if the
        /// method returns true.</param>
        /// <param name="setSpecified">Provides a method (null for none) to indicate that a member was
        /// deserialized; it must be of the form "void {Method}(bool)", and will be called with "true"
        /// when data is found.</param>
        public void SetSpecified(MethodInfo getSpecified, MethodInfo setSpecified)
        {
            if (this.getSpecified != getSpecified || this.setSpecified != setSpecified)
            {
                if (getSpecified != null)
                {
                    if (getSpecified.ReturnType != typeof(bool)
                        || getSpecified.IsStatic
                        || getSpecified.GetParameters().Length != 0)
                    {
                        throw new ArgumentException("Invalid pattern for checking member-specified", nameof(getSpecified));
                    }
                }
                if (setSpecified != null)
                {
                    ParameterInfo[] args;
                    if (setSpecified.ReturnType != typeof(void)
                        || setSpecified.IsStatic
                        || (args = setSpecified.GetParameters()).Length != 1
                        || args[0].ParameterType != typeof(bool))
                    {
                        throw new ArgumentException("Invalid pattern for setting member-specified", nameof(setSpecified));
                    }
                }

                ThrowIfFrozen();
                this.getSpecified = getSpecified;
                this.setSpecified = setSpecified;
            }
        }

        private void ThrowIfFrozen()
        {
            if (serializer != null) throw new InvalidOperationException("The type cannot be changed once a serializer has been generated");
        }

        private IRuntimeProtoSerializerNode BuildSerializer()
        {
            int opaqueToken = 0;
            try
            {
                model.TakeLock(ref opaqueToken);// check nobody is still adding this type
                var member = backingMember ?? Member;
                IRuntimeProtoSerializerNode ser;

                var repeated = model.TryGetRepeatedProvider(MemberType);
                
                if (repeated != null)
                {
                    if (repeated.IsMap)
                    {
#if FEAT_DYNAMIC_REF
                        if (!AsReference)
                        {
                            AsReference = MetaType.GetAsReferenceDefault(valueType);
                        }
#endif
                        repeated.ResolveMapTypes(out var keyType, out var valueType);
                        _ = TryGetCoreSerializer(model, MapKeyFormat, keyType, out var keyWireType, false, false, false, false);
                        _ = TryGetCoreSerializer(model, MapValueFormat, valueType, out var valueWireType, AsReference, DynamicType, false, true);


                        WireType rootWireType = DataFormat == DataFormat.Group ? WireType.StartGroup : WireType.String;
                        SerializerFeatures features = rootWireType.AsFeatures(); // | SerializerFeatures.OptionReturnNothingWhenUnchanged;
                        if (!IsMap) features |= SerializerFeatures.OptionFailOnDuplicateKey;
                        if (OverwriteList) features |= SerializerFeatures.OptionClearCollection;

                        
                        ser = MapDecorator.Create(repeated, keyType, valueType, FieldNumber, features,
                            keyWireType.AsFeatures(), valueWireType.AsFeatures());
                    }
                    else
                    {
                        if (SupportNull)
                        {
#if FEAT_NULL_LIST_ITEMS
                            something new here; old code not even remotely compatible
#else
                            ThrowHelper.ThrowNotSupportedException("null items in lists");
#endif
                        }

                        _ = TryGetCoreSerializer(model, dataFormat, repeated.ItemType, out WireType wireType, AsReference, DynamicType, OverwriteList, true);
                        //Type underlyingItemType = SupportNull ? ItemType : Nullable.GetUnderlyingType(ItemType) ?? ItemType;

                        //Debug.Assert(underlyingItemType == ser.ExpectedType
                        //    || (ser.ExpectedType == typeof(object) && !underlyingItemType.IsValueType)
                        //    , $"Wrong type in the tail; expected {ser.ExpectedType}, received {underlyingItemType}");

                        SerializerFeatures listFeatures = wireType.AsFeatures(); // | SerializerFeatures.OptionReturnNothingWhenUnchanged;
                        if (!IsPacked) listFeatures |= SerializerFeatures.OptionPackedDisabled;
                        if (OverwriteList) listFeatures |= SerializerFeatures.OptionClearCollection;
#if FEAT_NULL_LIST_ITEMS
                        if (SupportNull) listFeatures |= SerializerFeatures.OptionListsSupportNull;
#endif
                        //if (MemberType.IsArray)
                        //{
                        //    ser = ArrayDecorator.Create(ItemType, FieldNumber, listFeatures);
                        //}
                        //else
                        //{
                        ser = RepeatedDecorator.Create(repeated, FieldNumber, listFeatures);
                        //}
                    }
                }
                else
                {
                    ser = TryGetCoreSerializer(model, dataFormat, MemberType, out WireType wireType, AsReference, DynamicType, OverwriteList, true);
                    if (ser == null)
                    {
                        throw new InvalidOperationException("No serializer defined for type: " + MemberType.ToString());
                    }

                    // apply lists if appropriate (note that we don't end up using "ser" in this case, but that's OK)
                    
                    else
                    {
                        ser = new TagDecorator(FieldNumber, wireType, IsStrict, ser);

                        if (_defaultValue != null && !IsRequired && getSpecified == null)
                        {   // note: "ShouldSerialize*" / "*Specified" / etc ^^^^ take precedence over defaultValue,
                            // as does "IsRequired"
                            ser = new DefaultValueDecorator(_defaultValue, ser);
                        }
                    }
                    if (MemberType == typeof(Uri))
                    {
                        ser = new UriDecorator(ser);
                    }
                }
                if (member != null)
                {
                    if (member is PropertyInfo prop)
                    {
                        ser = new PropertyDecorator(ParentType, prop, ser);
                    }
                    else if (member is FieldInfo fld)
                    {
                        ser = new FieldDecorator(ParentType, fld, ser);
                    }
                    else
                    {
                        throw new InvalidOperationException();
                    }

                    if (getSpecified != null || setSpecified != null)
                    {
                        ser = new MemberSpecifiedDecorator(getSpecified, setSpecified, ser);
                    }
                }
                return ser;
            }
            finally
            {
                model.ReleaseLock(opaqueToken);
            }
        }

        private static WireType GetIntWireType(DataFormat format, int width)
        {
            switch (format)
            {
                case DataFormat.ZigZag: return WireType.SignedVarint;
                case DataFormat.FixedSize: return width == 32 ? WireType.Fixed32 : WireType.Fixed64;
                case DataFormat.TwosComplement:
                case DataFormat.Default: return WireType.Varint;
                default: throw new InvalidOperationException();
            }
        }
        private static WireType GetDateTimeWireType(DataFormat format)
        {
            switch (format)
            {
                case DataFormat.Group: return WireType.StartGroup;
                case DataFormat.FixedSize: return WireType.Fixed64;
                case DataFormat.WellKnown:
                case DataFormat.Default:
                    return WireType.String;
                default: throw new InvalidOperationException();
            }
        }

        internal static IRuntimeProtoSerializerNode TryGetCoreSerializer(RuntimeTypeModel model, DataFormat dataFormat, Type type, out WireType defaultWireType,
            bool asReference, bool dynamicType, bool overwriteList, bool allowComplexTypes)
        {
            type = DynamicStub.GetEffectiveType(type);
            if (type.IsEnum)
            {
                if (allowComplexTypes && model != null)
                {
                    // need to do this before checking the typecode; an int enum will report Int32 etc
                    defaultWireType = WireType.Varint;
                    return new EnumMemberSerializer(type);
                }
                else
                { // enum is fine for adding as a meta-type
                    defaultWireType = WireType.None;
                    return null;
                }
            }
            ProtoTypeCode code = Helpers.GetTypeCode(type);
            switch (code)
            {
                case ProtoTypeCode.Int32:
                    defaultWireType = GetIntWireType(dataFormat, 32);
                    return Int32Serializer.Instance;
                case ProtoTypeCode.UInt32:
                    defaultWireType = GetIntWireType(dataFormat, 32);
                    return UInt32Serializer.Instance;
                case ProtoTypeCode.Int64:
                    defaultWireType = GetIntWireType(dataFormat, 64);
                    return Int64Serializer.Instance;
                case ProtoTypeCode.UInt64:
                    defaultWireType = GetIntWireType(dataFormat, 64);
                    return UInt64Serializer.Instance;
                case ProtoTypeCode.String:
                    defaultWireType = WireType.String;
                    if (asReference)
                    {
#if FEAT_DYNAMIC_REF
                        return new NetObjectSerializer(typeof(string), BclHelpers.NetObjectOptions.AsReference);
#else
                        ThrowHelper.ThrowNotSupportedException(ProtoContractAttribute.ReferenceDynamicDisabled);
                        return default;
#endif
                    }
                    return StringSerializer.Instance;
                case ProtoTypeCode.Single:
                    defaultWireType = WireType.Fixed32;
                    return SingleSerializer.Instance;
                case ProtoTypeCode.Double:
                    defaultWireType = WireType.Fixed64;
                    return DoubleSerializer.Instance;
                case ProtoTypeCode.Boolean:
                    defaultWireType = WireType.Varint;
                    return BooleanSerializer.Instance;
                case ProtoTypeCode.DateTime:
                    defaultWireType = GetDateTimeWireType(dataFormat);
                    return new DateTimeSerializer(dataFormat, model);
                case ProtoTypeCode.Decimal:
                    defaultWireType = WireType.String;
                    return DecimalSerializer.Instance;
                case ProtoTypeCode.Byte:
                    defaultWireType = GetIntWireType(dataFormat, 32);
                    return ByteSerializer.Instance;
                case ProtoTypeCode.SByte:
                    defaultWireType = GetIntWireType(dataFormat, 32);
                    return SByteSerializer.Instance;
                case ProtoTypeCode.Char:
                    defaultWireType = WireType.Varint;
                    return CharSerializer.Instance;
                case ProtoTypeCode.Int16:
                    defaultWireType = GetIntWireType(dataFormat, 32);
                    return Int16Serializer.Instance;
                case ProtoTypeCode.UInt16:
                    defaultWireType = GetIntWireType(dataFormat, 32);
                    return UInt16Serializer.Instance;
                case ProtoTypeCode.TimeSpan:
                    defaultWireType = GetDateTimeWireType(dataFormat);
                    return new TimeSpanSerializer(dataFormat);
                case ProtoTypeCode.Guid:
                    defaultWireType = dataFormat == DataFormat.Group ? WireType.StartGroup : WireType.String;
                    return GuidSerializer.Instance;
                case ProtoTypeCode.Uri:
                    defaultWireType = WireType.String;
                    return StringSerializer.Instance;
                case ProtoTypeCode.ByteArray:
                    defaultWireType = WireType.String;
                    return new BlobSerializer(overwriteList);
                case ProtoTypeCode.Type:
                    defaultWireType = WireType.String;
                    return SystemTypeSerializer.Instance;
            }
            IRuntimeProtoSerializerNode parseable = model.AllowParseableTypes ? ParseableSerializer.TryCreate(type) : null;
            if (parseable != null)
            {
                defaultWireType = WireType.String;
                return parseable;
            }
            if (allowComplexTypes && model != null)
            {
                MetaType meta = null;
                if (model.IsDefined(type))
                {
                    meta = model[type];
                    if (dataFormat == DataFormat.Default && meta.IsGroup)
                    {
                        dataFormat = DataFormat.Group;
                    }
                }

                if (asReference || dynamicType)
                {
#if FEAT_DYNAMIC_REF
                    BclHelpers.NetObjectOptions options = BclHelpers.NetObjectOptions.None;
                    if (asReference) options |= BclHelpers.NetObjectOptions.AsReference;
                    if (dynamicType) options |= BclHelpers.NetObjectOptions.DynamicType;

                    if (meta != null)
                    { // exists
                        if (asReference && type.IsValueType)
                        {
                            string message = "AsReference cannot be used with value-types";

                            if (type.Name == "KeyValuePair`2")
                            {
                                message += "; please see https://stackoverflow.com/q/14436606/23354";
                            }
                            else
                            {
                                message += ": " + type.FullName;
                            }
                            throw new InvalidOperationException(message);
                        }

                        if (asReference && (meta.IsAutoTuple || meta.HasSurrogate)) options |= BclHelpers.NetObjectOptions.LateSet;
                        if (meta.UseConstructor) options |= BclHelpers.NetObjectOptions.UseConstructor;
                    }
                    defaultWireType = dataFormat == DataFormat.Group ? WireType.StartGroup : WireType.String;
                    return new NetObjectSerializer(type, options);
#else
                    ThrowHelper.ThrowNotSupportedException(ProtoContractAttribute.ReferenceDynamicDisabled);
                    defaultWireType = default;
                    return default;
#endif
                }
                if (model.IsDefined(type))
                {
                    defaultWireType = dataFormat == DataFormat.Group ? WireType.StartGroup : WireType.String;
                    return SubItemSerializer.Create(type, meta);
                }
            }
            defaultWireType = WireType.None;
            return null;
        }

        private string name;
        internal void SetName(string name)
        {
            if (name != this.name)
            {
                ThrowIfFrozen();
                this.name = name;
            }
        }
        /// <summary>
        /// Gets the logical name for this member in the schema (this is not critical for binary serialization, but may be used
        /// when inferring a schema).
        /// </summary>
        public string Name
        {
            get { return string.IsNullOrEmpty(name) ? Member.Name : name; }
            set { SetName(value); }
        }

        private const byte
           OPTIONS_IsStrict = 1,
           OPTIONS_IsPacked = 2,
           OPTIONS_IsRequired = 4,
           OPTIONS_OverwriteList = 8,
#if FEAT_NULL_LIST_ITEMS
           OPTIONS_SupportNull = 16,
#endif
#if FEAT_DYNAMIC_REF
           OPTIONS_AsReference = 32,
           OPTIONS_DynamicType = 128,
#endif
           OPTIONS_IsMap = 64;

        private byte flags;
        private bool HasFlag(byte flag) { return (flags & flag) == flag; }
        private void SetFlag(byte flag, bool value, bool throwIfFrozen)
        {
            if (throwIfFrozen && HasFlag(flag) != value)
            {
                ThrowIfFrozen();
            }
            if (value)
                flags |= flag;
            else
                flags = (byte)(flags & ~flag);
        }

        /// <summary>
        /// Should lists have extended support for null values? Note this makes the serialization less efficient.
        /// </summary>
        public bool SupportNull
        {
#if FEAT_NULL_LIST_ITEMS
            get { return HasFlag(OPTIONS_SupportNull); }
            set { SetFlag(OPTIONS_SupportNull, value, true); }
#else
            get => false;
            [Obsolete(SupportNullNotImplemented, true)]
            set { if (value != SupportNull) ThrowHelper.ThrowNotSupportedException(); }
#endif
        }

#if !FEAT_NULL_LIST_ITEMS
        internal const string SupportNullNotImplemented = "Nullable list elements are not currently implemented";
#endif

        internal string GetSchemaTypeName(HashSet<Type> callstack, bool applyNetObjectProxy, ref RuntimeTypeModel.CommonImports imports, out string altName)
        {
            Type effectiveType = ItemType ?? MemberType;
            return model.GetSchemaTypeName(callstack, effectiveType, DataFormat, applyNetObjectProxy && AsReference, applyNetObjectProxy && DynamicType, ref imports, out altName);
        }

        internal sealed class Comparer : System.Collections.IComparer, IComparer<ValueMember>
        {
            public static readonly Comparer Default = new Comparer();

            public int Compare(object x, object y)
            {
                return Compare(x as ValueMember, y as ValueMember);
            }

            public int Compare(ValueMember x, ValueMember y)
            {
                if (ReferenceEquals(x, y)) return 0;
                if (x == null) return -1;
                if (y == null) return 1;

                return x.FieldNumber.CompareTo(y.FieldNumber);
            }
        }
    }
}