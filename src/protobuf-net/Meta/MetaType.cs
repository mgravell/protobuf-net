using ProtoBuf.Internal;
using ProtoBuf.Internal.Serializers;
using ProtoBuf.Serializers;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace ProtoBuf.Meta
{
    /// <summary>
    /// Represents a type at runtime for use with protobuf, allowing the field mappings (etc) to be defined
    /// </summary>
    public sealed class MetaType : ISerializerProxy
    {
        internal sealed class Comparer : IComparer, IComparer<MetaType>
        {
            internal Comparer(HashSet<Type> callstack) => _callstack = callstack;
            private readonly HashSet<Type> _callstack;

            public int Compare(object x, object y)
            {
                return Compare(x as MetaType, y as MetaType);
            }
            public int Compare(MetaType x, MetaType y)
            {
                if (ReferenceEquals(x, y)) return 0;
                if (x is null) return -1;
                if (y is null) return 1;

                return string.Compare(
                    x.GetSchemaTypeName(_callstack),
                    y.GetSchemaTypeName(_callstack),
                    StringComparison.Ordinal);
            }
        }
        /// <summary>
        /// Get the name of the type being represented
        /// </summary>
        public override string ToString()
        {
            return Type.ToString();
        }

        IRuntimeProtoSerializerNode ISerializerProxy.Serializer => Serializer;
        private MetaType baseType;

        /// <summary>
        /// Gets the base-type for this type
        /// </summary>
        public MetaType BaseType => baseType;

        internal RuntimeTypeModel Model => model;

        private CompatibilityLevel _compatibilityLevel;

        /// <summary>
        /// Gets or sets the <see cref="MetaType.CompatibilityLevel"/> for this instance
        /// </summary>
        public CompatibilityLevel CompatibilityLevel
        {
            get => _compatibilityLevel;
            set
            {
                if (value != _compatibilityLevel)
                {
                    if (HasFields) ThrowHelper.ThrowInvalidOperationException($"{CompatibilityLevel} cannot be set once fields have been defined");
                    CompatibilityLevelAttribute.AssertValid(value);
                    _compatibilityLevel = value;
                }
            }
        }

        /// <summary>
        /// When used to compile a model, should public serialization/deserialzation methods
        /// be included for this type?
        /// </summary>
        public bool IncludeSerializerMethod
        {   // negated to minimize common-case / initializer
            get { return !HasFlag(TypeOptions.PrivateOnApi); }
            set { SetFlag(TypeOptions.PrivateOnApi, !value, true); }
        }

        /// <summary>
        /// Should this type be treated as a reference by default?
        /// </summary>
        public bool AsReferenceDefault
        {
#if FEAT_DYNAMIC_REF
            get { return HasFlag(OPTIONS_AsReferenceDefault); }
            set { SetFlag(OPTIONS_AsReferenceDefault, value, true); }
#else
            get => false;
            [Obsolete(ProtoContractAttribute.ReferenceDynamicDisabled, true)]
            set { if (value != AsReferenceDefault) ThrowHelper.ThrowNotSupportedException(); }
#endif

        }

        private List<SubType> _subTypes;
        private bool IsValidSubType(Type subType)
        {
            return subType is object && !subType.IsValueType
                && Type.IsAssignableFrom(subType);
        }
        /// <summary>
        /// Adds a known sub-type to the inheritance model
        /// </summary>
        public MetaType AddSubType(int fieldNumber, Type derivedType)
        {
            return AddSubType(fieldNumber, derivedType, DataFormat.Default);
        }

        private static void ThrowSubTypeWithSurrogate(Type type)
        {
            ThrowHelper.ThrowInvalidOperationException(
                $"Types with surrogates cannot be used in inheritance hierarchies: {type.NormalizeName()}");
        }

        /// <summary>
        /// Adds a known sub-type to the inheritance model
        /// </summary>
        public MetaType AddSubType(int fieldNumber, Type derivedType, DataFormat dataFormat)
        {
            if (derivedType is null) throw new ArgumentNullException(nameof(derivedType));
            if (fieldNumber < 1) throw new ArgumentOutOfRangeException(nameof(fieldNumber));
            if (!(Type.IsClass || Type.IsInterface) || Type.IsSealed)
            {
                throw new InvalidOperationException("Sub-types can only be added to non-sealed classes: " + Type.NormalizeName());
            }
            if (!IsValidSubType(derivedType))
            {
                throw new ArgumentException(derivedType.NormalizeName() + " is not a valid sub-type of " + Type.NormalizeName(), nameof(derivedType));
            }

            int opaqueToken = 0;
            try
            {
                model.TakeLock(ref opaqueToken);
                MetaType derivedMeta = model[derivedType];
                ThrowIfFrozen();
                derivedMeta.ThrowIfFrozen();

                if (IsAutoTuple || derivedMeta.IsAutoTuple)
                {
                    ThrowTupleTypeWithInheritance(derivedType);
                }
                if (surrogateType is object) ThrowSubTypeWithSurrogate(Type);
                if (derivedMeta.surrogateType is object) ThrowSubTypeWithSurrogate(derivedType);

                SubType subType = new SubType(fieldNumber, derivedMeta, dataFormat);
                ThrowIfFrozen();

                derivedMeta.SetBaseType(this); // includes ThrowIfFrozen
                (_subTypes ??= new List<SubType>()).Add(subType);
                return this;
            }
            finally
            {
                model.ReleaseLock(opaqueToken);
            }
        }

        private static void ThrowTupleTypeWithInheritance(Type type)
        {
            ThrowHelper.ThrowInvalidOperationException(
                $"Tuple-based types cannot be used in inheritance hierarchies: {type.NormalizeName()}");
        }

        private void SetBaseType(MetaType baseType)
        {
            if (baseType is null) throw new ArgumentNullException(nameof(baseType));
            if (this.baseType == baseType) return;
            if (this.baseType is object) throw new InvalidOperationException($"Type '{this.baseType.Type.FullName}' can only participate in one inheritance hierarchy");

            MetaType type = baseType;
            while (type is object)
            {
                if (ReferenceEquals(type, this)) throw new InvalidOperationException($"Cyclic inheritance of '{this.baseType.Type.FullName}' is not allowed");
                type = type.baseType;
            }
            this.baseType = baseType;
        }

        private CallbackSet callbacks;

        /// <summary>
        /// Indicates whether the current type has defined callbacks 
        /// </summary>
        public bool HasCallbacks => callbacks is object && callbacks.NonTrivial;

        /// <summary>
        /// Indicates whether the current type has defined subtypes
        /// </summary>
        public bool HasSubtypes => _subTypes is object && _subTypes.Count != 0;

        /// <summary>
        /// Returns the set of callbacks defined for this type
        /// </summary>
        public CallbackSet Callbacks => callbacks ??= new CallbackSet(this);

        private bool IsValueType
        {
            get
            {
                return Type.IsValueType;
            }
        }
        /// <summary>
        /// Assigns the callbacks to use during serialiation/deserialization.
        /// </summary>
        /// <param name="beforeSerialize">The method (or null) called before serialization begins.</param>
        /// <param name="afterSerialize">The method (or null) called when serialization is complete.</param>
        /// <param name="beforeDeserialize">The method (or null) called before deserialization begins (or when a new instance is created during deserialization).</param>
        /// <param name="afterDeserialize">The method (or null) called when deserialization is complete.</param>
        /// <returns>The set of callbacks.</returns>
        public MetaType SetCallbacks(MethodInfo beforeSerialize, MethodInfo afterSerialize, MethodInfo beforeDeserialize, MethodInfo afterDeserialize)
        {
            CallbackSet callbacks = Callbacks;
            callbacks.BeforeSerialize = beforeSerialize;
            callbacks.AfterSerialize = afterSerialize;
            callbacks.BeforeDeserialize = beforeDeserialize;
            callbacks.AfterDeserialize = afterDeserialize;
            return this;
        }
        /// <summary>
        /// Assigns the callbacks to use during serialiation/deserialization.
        /// </summary>
        /// <param name="beforeSerialize">The name of the method (or null) called before serialization begins.</param>
        /// <param name="afterSerialize">The name of the method (or null) called when serialization is complete.</param>
        /// <param name="beforeDeserialize">The name of the method (or null) called before deserialization begins (or when a new instance is created during deserialization).</param>
        /// <param name="afterDeserialize">The name of the method (or null) called when deserialization is complete.</param>
        /// <returns>The set of callbacks.</returns>
        public MetaType SetCallbacks(string beforeSerialize, string afterSerialize, string beforeDeserialize, string afterDeserialize)
        {
            if (IsValueType) throw new InvalidOperationException();
            CallbackSet callbacks = Callbacks;
            callbacks.BeforeSerialize = ResolveMethod(beforeSerialize, true);
            callbacks.AfterSerialize = ResolveMethod(afterSerialize, true);
            callbacks.BeforeDeserialize = ResolveMethod(beforeDeserialize, true);
            callbacks.AfterDeserialize = ResolveMethod(afterDeserialize, true);
            return this;
        }
        
        /// <summary>
        /// Returns the public Type name of this Type used in serialization
        /// </summary>
        public string GetSchemaTypeName() => GetSchemaTypeName(null);

        internal string GetSchemaTypeName(HashSet<Type> callstack)
        {
            callstack ??= new HashSet<Type>();
            if (!callstack.Add(Type)) return Type.Name; // recursion detected; give up

            try
            {
                if (surrogateType is object && !callstack.Contains(surrogateType))
                {
                    return model[surrogateType].GetSchemaTypeName(callstack);
                }

                if (!string.IsNullOrEmpty(name)) return name;

                string typeName = Type.Name;

                if (Type.IsArray) return GetArrayName(Type.GetElementType());
                if (Type.IsGenericType)
                {
                    var sb = new StringBuilder(typeName);
                    int split = typeName.IndexOf('`');
                    if (split >= 0) sb.Length = split;
                    foreach (Type arg in Type.GetGenericArguments())
                    {
                        sb.Append('_');
                        Type tmp = arg;
                        bool isKnown = model.IsDefined(tmp);
                        MetaType mt;
                        if (isKnown && (mt = model[tmp]) is object)
                        {
                            sb.Append(LastPart(mt.GetSchemaTypeName(callstack)));
                        }
                        else if (tmp.IsArray)
                        {
                            sb.Append(GetArrayName(tmp.GetElementType()));
                        }
                        else
                        {
                            //try a speculative add
                            mt = null;
                            try { mt = model.Add(tmp); }
                            catch { }
                            if (mt is object) sb.Append(mt.GetSchemaTypeName(callstack));
                            else sb.Append(tmp.Name); // give up
                        }
                    }
                    return sb.ToString();
                }

                return typeName;

                string GetArrayName(Type elementType)
                {
                    // Cannot use Name of array, since that's not a valid protobuf
                    // name.
                    // No need to check for nesting/array rank here. If that's invalid
                    // other parts of the schema generator will throw.
                    MetaType mt;
                    var name = (model.IsDefined(elementType) && (mt = model[elementType]) is object) ? mt.GetSchemaTypeName(callstack) : elementType.Name;
                    return "Array_" + name;
                }
            }
            finally
            {
                callstack.Remove(Type);
            }

            static string LastPart(string value)
            {
                if (string.IsNullOrWhiteSpace(value)) return value;
                var idx = value.LastIndexOf('.');
                return idx < 0 ? value : value.Substring(idx + 1);
            }
        }

        internal string GuessPackage()
        {   // very speculative; turns .Foo.Bar.Blap into Foo.Bar
            var s = Name;
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (s[0] != '.') return null; // not fully qualified
            var idx = s.LastIndexOf('.');
            return s.Substring(0, idx).Trim('.').Trim();
        }

        private string name, origin;

        /// <summary>
        /// Gets or sets the name of this contract.
        /// </summary>
        public string Name
        {
            get => name;
            set
            {
                ThrowIfFrozen();
                name = value;
            }
        }

        /// <summary>
        /// Gets or sets the file that defines this type (as used with <c>import</c> in .proto)
        /// </summary>
        public string Origin
        {
            get => origin;
            set
            {
                ThrowIfFrozen();
                origin = value;
            }
        }

        private MethodInfo factory;
        /// <summary>
        /// Designate a factory-method to use to create instances of this type
        /// </summary>
        public MetaType SetFactory(MethodInfo factory)
        {
            RuntimeTypeModel.VerifyFactory(factory, Type);
            ThrowIfFrozen();
            this.factory = factory;
            return this;
        }

        /// <summary>
        /// Designate a factory-method to use to create instances of this type
        /// </summary>
        public MetaType SetFactory(string factory)
        {
            return SetFactory(ResolveMethod(factory, false));
        }

        private MethodInfo ResolveMethod(string name, bool instance)
        {
            if (string.IsNullOrEmpty(name)) return null;
            return instance ? Helpers.GetInstanceMethod(Type, name) : Helpers.GetStaticMethod(Type, name);
        }

        private readonly RuntimeTypeModel model;

        internal static Exception InbuiltType(Type type, Exception innerException = null)
        {
            var msg = "Data of this type has inbuilt behaviour, and cannot be added to a model in this way: " + type.FullName;
            return innerException is null ? new ArgumentException(msg) : new ArgumentException(msg, innerException);
        }

        internal MetaType(RuntimeTypeModel model, Type type, MethodInfo factory)
        {
            this.factory = factory;
            if (model is null) throw new ArgumentNullException(nameof(model));
            if (type is null) throw new ArgumentNullException(nameof(type));

            IRuntimeProtoSerializerNode coreSerializer = model.TryGetBasicTypeSerializer(type);
            if (coreSerializer is object)
            {
                throw InbuiltType(type);
            }

            Type = type;
            if (type.IsArray)
            {
                // we'all allow add, to allow proxy generation, but
                // don't play with it too much!
                SetFlag(TypeOptions.Frozen, true, false);
            }
            this.model = model;
        }

        /// <summary>
        /// Throws an exception if the type has been made immutable
        /// </summary>
        internal void ThrowIfFrozen()
        {
            if ((flags & TypeOptions.Frozen) != 0) throw new InvalidOperationException("The type cannot be changed once a serializer has been generated for " + Type.FullName);
        }

        // internal void Freeze() { flags |= OPTIONS_Frozen; }

        /// <summary>
        /// The runtime type that the meta-type represents
        /// </summary>
        public Type Type { get; }

        private IProtoTypeSerializer _serializer;
        internal IProtoTypeSerializer Serializer
        {
            get
            {
                if (_serializer is null)
                {
                    int opaqueToken = 0;
                    try
                    {
                        model.TakeLock(ref opaqueToken);
                        if (_serializer is null)
                        { // double-check, but our main purpse with this lock is to ensure thread-safety with
                            // serializers needing to wait until another thread has finished adding the properties
                            SetFlag(TypeOptions.Frozen, true, false);
                            _serializer = BuildSerializer();

                            if (model.AutoCompile) CompileInPlace();
                        }
                    }
                    finally
                    {
                        model.ReleaseLock(opaqueToken);
                    }
                }
                return _serializer;
            }
        }

        internal Type GetInheritanceRoot()
        {
            if (Type.IsValueType) return null;

            var root = GetRootType(this);
            if (!ReferenceEquals(root, this)) return root.Type;
            if (_subTypes is object && _subTypes.Count != 0) return root.Type;

            return null;
        }

        private SerializerFeatures GetFeatures()
        {
            if (Type.IsEnum) return SerializerFeatures.WireTypeVarint | SerializerFeatures.CategoryScalar;

            if (!Type.IsValueType)
            {
                var bt = GetRootType(this);
                if (!ReferenceEquals(bt, this)) return bt.GetFeatures();
            }
            var features = SerializerFeatures.CategoryMessage;
            features |= IsGroup ? SerializerFeatures.WireTypeStartGroup : SerializerFeatures.WireTypeString;
            return features;
        }

        private bool HasRealInheritance()
            => (baseType is object && baseType != this) || (_subTypes?.Count ?? 0) > 0;
        private IProtoTypeSerializer BuildSerializer()
        {
            if (SerializerType is object)
            {
                return ExternalSerializer.Create(Type, SerializerType);
            }
            Validate();
            var repeated = model.TryGetRepeatedProvider(Type);

            if (repeated is object)
            {
                if (surrogateType is object)
                {
                    throw new ArgumentException("Repeated data (a list, collection, etc) has inbuilt behaviour and cannot use a surrogate");
                }
                if (_subTypes is object && _subTypes.Count != 0)
                {
                    throw new ArgumentException("Repeated data (a list, collection, etc) has inbuilt behaviour and cannot be subclassed");
                }

                ValueMember fakeMember = new ValueMember(model, ProtoBuf.Serializer.ListItemTag, Type, repeated.ItemType, null, DataFormat.Default)
                {
                    CompatibilityLevel = CompatibilityLevel
                };
                return TypeSerializer.Create(Type, new int[] { ProtoBuf.Serializer.ListItemTag }, new IRuntimeProtoSerializerNode[] { fakeMember.Serializer },
                    null, true, true, !IgnoreUnknownSubTypes, null,
                    constructType, factory, GetInheritanceRoot(), GetFeatures());
            }

            bool involvedInInheritance = HasRealInheritance();
            if (surrogateType is object)
            {
                if (involvedInInheritance) ThrowSubTypeWithSurrogate(Type);

                SerializerFeatures features;
                // check to see if we can handle that directly without using GetSerializer<surrogateType>()
                var serializer = ValueMember.TryGetCoreSerializer(Model, surrogateDataFormat, CompatibilityLevel, surrogateType, out _, false, false, false, false);
                if (serializer is object)
                {
                    try
                    {
                        features = ExternalSerializer.Create(surrogateType, typeof(PrimaryTypeProvider)).Features;
                    }
                    catch (Exception ex)
                    {
                        throw InbuiltType(surrogateType, ex);
                    }
                }
                else
                {
                    MetaType mt = model[surrogateType], mtBase;
                    while ((mtBase = mt.baseType) is object)
                    {
                        if (mt.HasRealInheritance()) ThrowSubTypeWithSurrogate(mt.Type);
                        mt = mtBase;
                    }
                    var ser = mt.Serializer;
                    features = ser.Features;
                    serializer = ser;
                }
                return (IProtoTypeSerializer)Activator.CreateInstance(typeof(SurrogateSerializer<>).MakeGenericType(Type),
                    args: new object[] { surrogateType, underlyingToSurrogate, surrogateToUnderlying, serializer, features });
            }
            if (IsAutoTuple)
            {
                if (involvedInInheritance) ThrowTupleTypeWithInheritance(Type);
                ConstructorInfo ctor = ResolveTupleConstructor(Type, out MemberInfo[] mapping);
                if (ctor is null) throw new InvalidOperationException();
                return (IProtoTypeSerializer)Activator.CreateInstance(typeof(TupleSerializer<>).MakeGenericType(Type),
                    args: new object[] { model, ctor, mapping, GetFeatures(), CompatibilityLevel });
            }

            if (HasFields) Fields.TrimExcess();
            if (HasEnums) Enums.TrimExcess();

            int fieldCount = _fields?.Count ?? 0;
            int subTypeCount = _subTypes?.Count ?? 0;
            int[] fieldNumbers = new int[fieldCount + subTypeCount];
            IRuntimeProtoSerializerNode[] serializers = new IRuntimeProtoSerializerNode[fieldCount + subTypeCount];
            int i = 0;
            if (subTypeCount != 0)
            {
                foreach (SubType subType in _subTypes)
                {
                    if (!subType.DerivedType.IgnoreListHandling && model.TryGetRepeatedProvider(subType.DerivedType.Type) is object)
                    {
                        ThrowHelper.ThrowArgumentException("Repeated data (a list, collection, etc) has inbuilt behaviour and cannot be used as a subclass");
                    }
                    fieldNumbers[i] = subType.FieldNumber;
                    serializers[i++] = subType.GetSerializer(Type);
                }
            }
            if (fieldCount != 0)
            {
                foreach (ValueMember member in _fields)
                {
                    fieldNumbers[i] = member.FieldNumber;
                    serializers[i++] = member.Serializer;
                }
            }

            List<MethodInfo> baseCtorCallbacks = null;
            MetaType tmp = BaseType;

            while (tmp is object)
            {
                MethodInfo method = tmp.HasCallbacks ? tmp.Callbacks.BeforeDeserialize : null;
                if (method is object)
                {
                    (baseCtorCallbacks ??= new List<MethodInfo>()).Add(method);
                }
                tmp = tmp.BaseType;
            }
            MethodInfo[] arr = null;
            if (baseCtorCallbacks is object)
            {
                arr = new MethodInfo[baseCtorCallbacks.Count];
                baseCtorCallbacks.CopyTo(arr, 0);
                Array.Reverse(arr);
            }
            return TypeSerializer.Create(Type, fieldNumbers, serializers, arr, baseType is null, UseConstructor, !IgnoreUnknownSubTypes,
                callbacks, constructType, factory, GetInheritanceRoot(), GetFeatures());
        }

        [Flags]
        internal enum AttributeFamily
        {
            None = 0, ProtoBuf = 1, DataContractSerialier = 2, XmlSerializer = 4, AutoTuple = 8
        }
        private static Type GetBaseType(MetaType type)
        {
            return type.Type.BaseType;
        }
        internal static bool GetAsReferenceDefault(Type type)
        {
            if (type is null) throw new ArgumentNullException(nameof(type));
            if (type.IsEnum) return false; // never as-ref
            AttributeMap[] typeAttribs = AttributeMap.Create(type, false);
            for (int i = 0; i < typeAttribs.Length; i++)
            {
                if (typeAttribs[i].AttributeType.FullName == "ProtoBuf.ProtoContractAttribute")
                {
                    if (typeAttribs[i].TryGet("AsReferenceDefault", out object tmp)) return (bool)tmp;
                }
            }
            return false;
        }

        internal void ApplyDefaultBehaviour(CompatibilityLevel ambient)
        {
            TypeAddedEventArgs args = null; // allows us to share the event-args between events
            RuntimeTypeModel.OnBeforeApplyDefaultBehaviour(this, ref args);
            if (args is null || args.ApplyDefaultBehaviour) ApplyDefaultBehaviourImpl(ambient);
            RuntimeTypeModel.OnAfterApplyDefaultBehaviour(this, ref args);
        }

        private void ApplyDefaultBehaviourImpl(CompatibilityLevel ambient)
        {
            Type baseType = GetBaseType(this);
            if (baseType is object && model.FindWithoutAdd(baseType) is null
                && GetContractFamily(model, baseType, null) != MetaType.AttributeFamily.None)
            {
                model.FindOrAddAuto(baseType, true, false, false, ambient);
            }

            AttributeMap[] typeAttribs = AttributeMap.Create(Type, false);
            AttributeFamily family = GetContractFamily(model, Type, typeAttribs);
            if (family == AttributeFamily.AutoTuple)
            {
                SetFlag(TypeOptions.AutoTuple, true, true);
            }
            // note this needs to happen *after* the auto-tuple check, for call-site semantics
            var compatLevel = CompatibilityLevel;
            if (compatLevel <= CompatibilityLevel.NotSpecified)
            {
                if (IsAutoTuple)
                {
                    compatLevel = ambient;
                }
                if (compatLevel <= CompatibilityLevel.NotSpecified) compatLevel = Model.DefaultCompatibilityLevel;
                CompatibilityLevel = TypeCompatibilityHelper.GetTypeCompatibilityLevel(Type, compatLevel);
            }
            bool isEnum = Type.IsEnum;
            if (family == AttributeFamily.None && !isEnum) return; // and you'd like me to do what, exactly?

            List<string> partialIgnores = null;
            List<AttributeMap> partialMembers = null;
            int dataMemberOffset = 0, implicitFirstTag = 1;
            bool inferTagByName = model.InferTagFromNameDefault;
            ImplicitFields implicitMode = ImplicitFields.None;
            string name = null, origin = null;
            for (int i = 0; i < typeAttribs.Length; i++)
            {
                AttributeMap item = (AttributeMap)typeAttribs[i];
                object tmp;
                string fullAttributeTypeName = item.AttributeType.FullName;
                if (!isEnum && fullAttributeTypeName == "ProtoBuf.ProtoIncludeAttribute")
                {
                    int tag = 0;
                    if (item.TryGet("tag", out tmp)) tag = (int)tmp;
                    DataFormat dataFormat = DataFormat.Default;
                    if (item.TryGet("DataFormat", out tmp))
                    {
                        dataFormat = (DataFormat)(int)tmp;
                    }
                    Type knownType = null;
                    try
                    {
                        if (item.TryGet("knownTypeName", out tmp))
                        {
                            knownType = TypeModel.ResolveKnownType((string)tmp, Type.Assembly);
                        }
                        else if (item.TryGet("knownType", out tmp))
                        {
                            knownType = (Type)tmp;
                        }
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException("Unable to resolve sub-type of: " + Type.FullName, ex);
                    }
                    if (knownType is null)
                    {
                        throw new InvalidOperationException("Unable to resolve sub-type of: " + Type.FullName);
                    }
                    if (IsValidSubType(knownType)) AddSubType(tag, knownType, dataFormat);
                }

                if (fullAttributeTypeName == "ProtoBuf.ProtoPartialIgnoreAttribute")
                {
                    if (item.TryGet(nameof(ProtoPartialIgnoreAttribute.MemberName), out tmp) && tmp is object)
                    {
                        (partialIgnores ??= new List<string>()).Add((string)tmp);
                    }
                }
                if (!isEnum && fullAttributeTypeName == "ProtoBuf.ProtoPartialMemberAttribute")
                {
                    (partialMembers ??= new List<AttributeMap>()).Add(item);
                }

                if (fullAttributeTypeName == "ProtoBuf.ProtoContractAttribute")
                {
                    if (item.TryGet(nameof(ProtoContractAttribute.Name), out tmp)) name = (string)tmp;
                    if (item.TryGet(nameof(ProtoContractAttribute.Origin), out tmp)) origin = (string)tmp;
                    if (Type.IsEnum)
                    {
                        // there aren't any interesting things to ask about for enums; they are just pass-thru
                    }
                    else
                    {
                        if (item.TryGet(nameof(ProtoContractAttribute.DataMemberOffset), out tmp)) dataMemberOffset = (int)tmp;

                        if (item.TryGet(nameof(ProtoContractAttribute.InferTagFromNameHasValue), false, out tmp) && (bool)tmp)
                        {
                            if (item.TryGet(nameof(ProtoContractAttribute.InferTagFromName), out tmp)) inferTagByName = (bool)tmp;
                        }

                        if (item.TryGet(nameof(ProtoContractAttribute.ImplicitFields), out tmp) && tmp is object)
                        {
                            implicitMode = (ImplicitFields)(int)tmp; // note that this uses the bizarre unboxing rules of enums/underlying-types
                        }

                        if (item.TryGet(nameof(ProtoContractAttribute.SkipConstructor), out tmp)) UseConstructor = !(bool)tmp;
                        if (item.TryGet(nameof(ProtoContractAttribute.IgnoreListHandling), out tmp)) IgnoreListHandling = (bool)tmp;
#if FEAT_DYNAMIC_REF
                        if (item.TryGet(nameof(ProtoContractAttribute.AsReferenceDefault), out tmp)) AsReferenceDefault = (bool)tmp;
#endif
                        if (item.TryGet(nameof(ProtoContractAttribute.ImplicitFirstTag), out tmp) && (int)tmp > 0) implicitFirstTag = (int)tmp;
                        if (item.TryGet(nameof(ProtoContractAttribute.IsGroup), out tmp)) IsGroup = (bool)tmp;
                        if (item.TryGet(nameof(ProtoContractAttribute.IgnoreUnknownSubTypes), out tmp)) IgnoreUnknownSubTypes = (bool)tmp;

                        if (item.TryGet(nameof(ProtoContractAttribute.Surrogate), out tmp)) SetSurrogate((Type)tmp);
                        if (item.TryGet(nameof(ProtoContractAttribute.Serializer), out tmp)) SerializerType = (Type)tmp;
                    }
                }

                if (fullAttributeTypeName == "System.Runtime.Serialization.DataContractAttribute")
                {
                    if (name is null && item.TryGet("Name", out tmp)) name = (string)tmp;
                }
                if (fullAttributeTypeName == "System.Xml.Serialization.XmlTypeAttribute")
                {
                    if (name is null && item.TryGet("TypeName", out tmp)) name = (string)tmp;
                }

                if (fullAttributeTypeName == "ProtoBuf.ProtoReservedAttribute" && item.Target is ProtoReservedAttribute reservation)
                {
                    AddReservation(reservation);
                }
            }

            if (!string.IsNullOrEmpty(name)) Name = name;
            if (origin is object) Origin = origin;
            if (implicitMode != ImplicitFields.None)
            {
                family &= AttributeFamily.ProtoBuf; // with implicit fields, **only** proto attributes are important
            }
            MethodInfo[] callbacks = null;

            var members = new List<ProtoMemberAttribute>();
            var enumMembers = new List<EnumMember>();

            MemberInfo[] foundList = Type.GetMembers(isEnum ? BindingFlags.Public | BindingFlags.Static
                : BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            foreach (MemberInfo member in foundList)
            {
                if (member.DeclaringType != Type) continue;
                if (member.IsDefined(typeof(ProtoIgnoreAttribute), true)) continue;
                if (partialIgnores is object && partialIgnores.Contains(member.Name)) continue;

                bool forced = false, isPublic, isField;
                Type effectiveType;

                if (member is PropertyInfo property)
                {
                    if (isEnum) continue; // wasn't expecting any props!
                    MemberInfo backingField = null;
                    if (!property.CanWrite)
                    {
                        // roslyn automatically implemented properties, in particular for get-only properties: <{Name}>k__BackingField;
                        var backingFieldName = $"<{property.Name}>k__BackingField";
                        foreach (var fieldMemeber in foundList)
                        {
                            if ((fieldMemeber is FieldInfo) && fieldMemeber.Name == backingFieldName)
                            {
                                backingField = fieldMemeber;
                                break;
                            }
                        }
                    }
                    effectiveType = property.PropertyType;
                    isPublic = Helpers.GetGetMethod(property, false, false) is object;
                    isField = false;
                    ApplyDefaultBehaviour_AddMembers(family, isEnum, partialMembers, dataMemberOffset, inferTagByName, implicitMode, members, member, ref forced, isPublic, isField, ref effectiveType, enumMembers, backingField);
                }
                else if (member is FieldInfo field)
                {
                    effectiveType = field.FieldType;
                    isPublic = field.IsPublic;
                    isField = true;
                    if (isEnum && !field.IsStatic)
                    { // only care about static things on enums; WinRT has a __value instance field!
                        continue;
                    }
                    ApplyDefaultBehaviour_AddMembers(family, isEnum, partialMembers, dataMemberOffset, inferTagByName, implicitMode, members, member, ref forced, isPublic, isField, ref effectiveType, enumMembers);
                }
                else if (member is MethodInfo method)
                {
                    if (isEnum) continue;
                    AttributeMap[] memberAttribs = AttributeMap.Create(method, false);
                    if (memberAttribs is object && memberAttribs.Length > 0)
                    {
                        CheckForCallback(method, memberAttribs, "ProtoBuf.ProtoBeforeSerializationAttribute", ref callbacks, 0);
                        CheckForCallback(method, memberAttribs, "ProtoBuf.ProtoAfterSerializationAttribute", ref callbacks, 1);
                        CheckForCallback(method, memberAttribs, "ProtoBuf.ProtoBeforeDeserializationAttribute", ref callbacks, 2);
                        CheckForCallback(method, memberAttribs, "ProtoBuf.ProtoAfterDeserializationAttribute", ref callbacks, 3);
                        CheckForCallback(method, memberAttribs, "System.Runtime.Serialization.OnSerializingAttribute", ref callbacks, 4);
                        CheckForCallback(method, memberAttribs, "System.Runtime.Serialization.OnSerializedAttribute", ref callbacks, 5);
                        CheckForCallback(method, memberAttribs, "System.Runtime.Serialization.OnDeserializingAttribute", ref callbacks, 6);
                        CheckForCallback(method, memberAttribs, "System.Runtime.Serialization.OnDeserializedAttribute", ref callbacks, 7);
                    }
                }
            }

            if (inferTagByName || implicitMode != ImplicitFields.None)
            {
                members.Sort();
                int nextTag = implicitFirstTag;
                foreach (ProtoMemberAttribute normalizedAttribute in members)
                {
                    if (!normalizedAttribute.TagIsPinned) // if ProtoMember etc sets a tag, we'll trust it
                    {
                        normalizedAttribute.Rebase(nextTag++);
                    }
                }
            }

            foreach (ProtoMemberAttribute normalizedAttribute in members)
            {
                ValueMember vm = ApplyDefaultBehaviour(isEnum, normalizedAttribute);
                if (vm is object)
                {
                    Add(vm);
                }
            }

            foreach (EnumMember enumMember in enumMembers)
            {
                Enums.Add(enumMember);
            }

            if (callbacks is object)
            {
                SetCallbacks(Coalesce(callbacks, 0, 4), Coalesce(callbacks, 1, 5),
                    Coalesce(callbacks, 2, 6), Coalesce(callbacks, 3, 7));
            }
        }

        internal void Assert(CompatibilityLevel expected)
        {
            var actual = CompatibilityLevel;
            if (actual == expected) return;

            ThrowHelper.ThrowInvalidOperationException($"The expected ('{expected}') and actual ('{actual}') compatibility level of '{Type.NormalizeName()}' did not match; the same type cannot be used with different compatibility levels in the same model; this is most commonly an issue with tuple-like types in different contexts");
        }

        private static void ApplyDefaultBehaviour_AddMembers(AttributeFamily family, bool isEnum, List<AttributeMap>partialMembers, int dataMemberOffset, bool inferTagByName, ImplicitFields implicitMode, List<ProtoMemberAttribute> members, MemberInfo member, ref bool forced, bool isPublic, bool isField, ref Type effectiveType, List<EnumMember> enumMembers, MemberInfo backingMember = null)
        {
            switch (implicitMode)
            {
                case ImplicitFields.AllFields:
                    if (isField) forced = true;
                    break;
                case ImplicitFields.AllPublic:
                    if (isPublic) forced = true;
                    break;
            }

            // we just don't like delegate types ;p
            if (effectiveType.IsSubclassOf(typeof(Delegate))) effectiveType = null;
            if (effectiveType is object)
            {
                ProtoMemberAttribute normalizedAttribute = NormalizeProtoMember(member, family, forced, isEnum, partialMembers, dataMemberOffset, inferTagByName, out var enumMember, backingMember);
                if (normalizedAttribute is object) members.Add(normalizedAttribute);
                if (enumMember.HasValue) enumMembers.Add(enumMember);
            }
        }

        private static MethodInfo Coalesce(MethodInfo[] arr, int x, int y)
        {
            MethodInfo mi = arr[x] ?? arr[y];
            return mi;
        }

        internal static AttributeFamily GetContractFamily(RuntimeTypeModel model, Type type, AttributeMap[] attributes)
        {
            AttributeFamily family = AttributeFamily.None;

            if (attributes is null) attributes = AttributeMap.Create(type, false);

            for (int i = 0; i < attributes.Length; i++)
            {
                switch (attributes[i].AttributeType.FullName)
                {
                    case "ProtoBuf.ProtoContractAttribute":
                        bool tmp = false;
                        GetFieldBoolean(ref tmp, attributes[i], "UseProtoMembersOnly");
                        if (tmp) return AttributeFamily.ProtoBuf;
                        family |= AttributeFamily.ProtoBuf;
                        break;
                    case "System.Xml.Serialization.XmlTypeAttribute":
                        if (!model.AutoAddProtoContractTypesOnly)
                        {
                            family |= AttributeFamily.XmlSerializer;
                        }
                        break;
                    case "System.Runtime.Serialization.DataContractAttribute":
                        if (!model.AutoAddProtoContractTypesOnly)
                        {
                            family |= AttributeFamily.DataContractSerialier;
                        }
                        break;
                }
            }
            if (family == AttributeFamily.None && model.AllowImplicitTuples)
            { 
                // check for obvious tuples
                if (ResolveTupleConstructor(type, out _) is object)
                {
                    family |= AttributeFamily.AutoTuple;
                }
            }
            return family;
        }
        internal static ConstructorInfo ResolveTupleConstructor(Type type, out MemberInfo[] mappedMembers)
        {
            mappedMembers = null;
            if (type is null) throw new ArgumentNullException(nameof(type));
            if (type.IsAbstract) return null; // as if!
            ConstructorInfo[] ctors = Helpers.GetConstructors(type, false);

            // need to have an interesting constructor to bother even checking this stuff
            if (ctors.Length == 0 || (ctors.Length == 1 && ctors[0].GetParameters().Length == 0)) return null;

            MemberInfo[] fieldsPropsUnfiltered = Helpers.GetInstanceFieldsAndProperties(type, true);
            var memberList = new List<MemberInfo>();
            // for most types we'll enforce that you need readonly, because that is what protobuf-net
            // always did historically; but: if you smell so much like a Tuple that it is *in your name*,
            // we'll let you past that
            bool demandReadOnly = type.Name.IndexOf("Tuple", StringComparison.OrdinalIgnoreCase) < 0;
            for (int i = 0; i < fieldsPropsUnfiltered.Length; i++)
            {
                if (fieldsPropsUnfiltered[i] is PropertyInfo prop)
                {
                    if (!prop.CanRead) return null; // no use if can't read
                    if (demandReadOnly && prop.CanWrite && IsPublicSetter(Helpers.GetSetMethod(prop, false, false)))
                    {
                        // don't allow a public set (need to allow non-public to handle Mono's KeyValuePair<,>)
                        // (unless it is an "init-only" set)
                        return null;
                    }
                    memberList.Add(prop);

                    static bool IsPublicSetter(MethodInfo method)
                    {
                        if (method is null) return false;
                        foreach (Type modreq in method.ReturnParameter?.GetRequiredCustomModifiers() ?? Type.EmptyTypes)
                        {
                            if (modreq?.FullName == "System.Runtime.CompilerServices.IsExternalInit") return false;
                        }
                        return true;
                    }
                }
                else
                {
                    if (fieldsPropsUnfiltered[i] is FieldInfo field)
                    {
                        if (demandReadOnly && !field.IsInitOnly) return null; // all public fields must be readonly to be counted a tuple
                        memberList.Add(field);
                    }
                }
            }
            if (memberList.Count == 0)
            {
                return null;
            }

            MemberInfo[] members = new MemberInfo[memberList.Count];
            memberList.CopyTo(members, 0);

            int[] mapping = new int[members.Length];
            int found = 0;
            ConstructorInfo result = null;
            mappedMembers = new MemberInfo[mapping.Length];
            for (int i = 0; i < ctors.Length; i++)
            {
                ParameterInfo[] parameters = ctors[i].GetParameters();

                if (parameters.Length != members.Length) continue;

                // reset the mappings to test
                for (int j = 0; j < mapping.Length; j++) mapping[j] = -1;

                for (int j = 0; j < parameters.Length; j++)
                {
                    for (int k = 0; k < members.Length; k++)
                    {
                        if (string.Compare(parameters[j].Name, members[k].Name, StringComparison.OrdinalIgnoreCase) != 0) continue;
                        Type memberType = Helpers.GetMemberType(members[k]);
                        if (memberType != parameters[j].ParameterType) continue;

                        mapping[j] = k;
                    }
                }
                // did we map all?
                bool notMapped = false;
                for (int j = 0; j < mapping.Length; j++)
                {
                    if (mapping[j] < 0)
                    {
                        notMapped = true;
                        break;
                    }
                    mappedMembers[j] = members[mapping[j]];
                }

                if (notMapped) continue;
                found++;
                result = ctors[i];
            }
            return found == 1 ? result : null;
        }

        private static void CheckForCallback(MethodInfo method, AttributeMap[] attributes, string callbackTypeName, ref MethodInfo[] callbacks, int index)
        {
            for (int i = 0; i < attributes.Length; i++)
            {
                if (attributes[i].AttributeType.FullName == callbackTypeName)
                {
                    if (callbacks is null) { callbacks = new MethodInfo[8]; }
                    else if (callbacks[index] is object)
                    {
                        Type reflected = method.ReflectedType;
                        throw new ProtoException("Duplicate " + callbackTypeName + " callbacks on " + reflected.FullName);
                    }
                    callbacks[index] = method;
                }
            }
        }
        private static bool HasFamily(AttributeFamily value, AttributeFamily required)
        {
            return (value & required) == required;
        }

        private static ProtoMemberAttribute NormalizeProtoMember(MemberInfo member, AttributeFamily family, bool forced, bool isEnum, List<AttributeMap> partialMembers, int dataMemberOffset, bool inferByTagName, out EnumMember enumMember, MemberInfo backingMember = null)
        {
            enumMember = default;
            if (member is null || (family == AttributeFamily.None && !isEnum)) return null; // nix
            int fieldNumber = int.MinValue, minAcceptFieldNumber = inferByTagName ? -1 : 1;
            string name = null;
            bool isPacked = false, ignore = false, done = false, isRequired = false, asReference = false, asReferenceHasValue = false, dynamicType = false, tagIsPinned = false, overwriteList = false;
            DataFormat dataFormat = DataFormat.Default;
            if (isEnum) forced = true;
            AttributeMap[] attribs = AttributeMap.Create(member, true);
            AttributeMap attrib;

            if (isEnum)
            {
                if (GetAttribute(attribs, "ProtoBuf.ProtoIgnoreAttribute") is null)
                {
                    attrib = GetAttribute(attribs, "ProtoBuf.ProtoEnumAttribute");

                    var value = ((FieldInfo)member).GetRawConstantValue();
                    if (attrib is object) GetFieldName(ref name, attrib, nameof(ProtoEnumAttribute.Name));
                    if (string.IsNullOrWhiteSpace(name)) name = member.Name;

                    enumMember = new EnumMember(value, name);
                }
                return null;
            }
            if (!ignore && !done) // always consider ProtoMember 
            {
                attrib = GetAttribute(attribs, "ProtoBuf.ProtoMemberAttribute");
                GetIgnore(ref ignore, attrib, attribs, "ProtoBuf.ProtoIgnoreAttribute");

                if (!ignore && attrib is object)
                {
                    GetFieldNumber(ref fieldNumber, attrib, "Tag");
                    GetFieldName(ref name, attrib, "Name");
                    GetFieldBoolean(ref isRequired, attrib, "IsRequired");
                    GetFieldBoolean(ref isPacked, attrib, "IsPacked");
                    GetFieldBoolean(ref overwriteList, attrib, "OverwriteList");
                    GetDataFormat(ref dataFormat, attrib, "DataFormat");
                    GetFieldBoolean(ref asReferenceHasValue, attrib, "AsReferenceHasValue", false);

                    if (asReferenceHasValue)
                    {
                        asReferenceHasValue = GetFieldBoolean(ref asReference, attrib, "AsReference", true);
                    }
                    GetFieldBoolean(ref dynamicType, attrib, "DynamicType");
                    done = tagIsPinned = fieldNumber > 0; // note minAcceptFieldNumber only applies to non-proto
                }

                if (!done && partialMembers is object)
                {
                    foreach (AttributeMap ppma in partialMembers)
                    {
                        if (ppma.TryGet("MemberName", out object tmp) && (string)tmp == member.Name)
                        {
                            GetFieldNumber(ref fieldNumber, ppma, "Tag");
                            GetFieldName(ref name, ppma, "Name");
                            GetFieldBoolean(ref isRequired, ppma, "IsRequired");
                            GetFieldBoolean(ref isPacked, ppma, "IsPacked");
                            GetFieldBoolean(ref overwriteList, attrib, "OverwriteList");
                            GetDataFormat(ref dataFormat, ppma, "DataFormat");
                            GetFieldBoolean(ref asReferenceHasValue, attrib, "AsReferenceHasValue", false);

                            if (asReferenceHasValue)
                            {
                                asReferenceHasValue = GetFieldBoolean(ref asReference, ppma, "AsReference", true);
                            }
                            GetFieldBoolean(ref dynamicType, ppma, "DynamicType");
                            if (done = tagIsPinned = fieldNumber > 0) break; // note minAcceptFieldNumber only applies to non-proto
                        }
                    }
                }
            }

            if (!ignore && !done && HasFamily(family, AttributeFamily.DataContractSerialier))
            {
                attrib = GetAttribute(attribs, "System.Runtime.Serialization.DataMemberAttribute");
                if (attrib is object)
                {
                    GetFieldNumber(ref fieldNumber, attrib, "Order");
                    GetFieldName(ref name, attrib, "Name");
                    GetFieldBoolean(ref isRequired, attrib, "IsRequired");
                    done = fieldNumber >= minAcceptFieldNumber;
                    if (done) fieldNumber += dataMemberOffset; // dataMemberOffset only applies to DCS flags, to allow us to "bump" WCF by a notch
                }
            }
            if (!ignore && !done && HasFamily(family, AttributeFamily.XmlSerializer))
            {
                attrib = GetAttribute(attribs, "System.Xml.Serialization.XmlElementAttribute")
                    ?? GetAttribute(attribs, "System.Xml.Serialization.XmlArrayAttribute");
                GetIgnore(ref ignore, attrib, attribs, "System.Xml.Serialization.XmlIgnoreAttribute");
                if (attrib is object && !ignore)
                {
                    GetFieldNumber(ref fieldNumber, attrib, "Order");
                    GetFieldName(ref name, attrib, "ElementName");
                    done = fieldNumber >= minAcceptFieldNumber;
                }
            }
            if (!ignore && !done)
            {
                if (GetAttribute(attribs, "System.NonSerializedAttribute") is object) ignore = true;
            }
            if (ignore || (fieldNumber < minAcceptFieldNumber && !forced)) return null;
            ProtoMemberAttribute result = new ProtoMemberAttribute(fieldNumber, forced || inferByTagName)
            {
#if FEAT_DYNAMIC_REF
                AsReference = asReference,
                AsReferenceHasValue = asReferenceHasValue,
                DynamicType = dynamicType,
#endif
                DataFormat = dataFormat,
                IsPacked = isPacked,
                OverwriteList = overwriteList,
                IsRequired = isRequired,
                Name = string.IsNullOrEmpty(name) ? member.Name : name,
                Member = member,
                BackingMember = backingMember,
                TagIsPinned = tagIsPinned
            };
            return result;
        }

        private ValueMember ApplyDefaultBehaviour(bool isEnum, ProtoMemberAttribute normalizedAttribute)
        {
            MemberInfo member;
            if (normalizedAttribute is null || (member = normalizedAttribute.Member) is null) return null; // nix

            Type effectiveType = Helpers.GetMemberType(member);

            // check for list types
            var memberCompatibility = TypeCompatibilityHelper.GetMemberCompatibilityLevel(member, CompatibilityLevel);
            var repeated = model.TryGetRepeatedProvider(effectiveType, memberCompatibility);
            
            AttributeMap[] attribs = AttributeMap.Create(member, true);
            AttributeMap attrib;

            object defaultValue = null;
            // implicit zero default
            if (model.UseImplicitZeroDefaults)
            {
                switch (Helpers.GetTypeCode(effectiveType))
                {
                    case ProtoTypeCode.Boolean: defaultValue = false; break;
                    case ProtoTypeCode.Decimal: defaultValue = (decimal)0; break;
                    case ProtoTypeCode.Single: defaultValue = (float)0; break;
                    case ProtoTypeCode.Double: defaultValue = (double)0; break;
                    case ProtoTypeCode.Byte: defaultValue = (byte)0; break;
                    case ProtoTypeCode.Char: defaultValue = (char)0; break;
                    case ProtoTypeCode.Int16: defaultValue = (short)0; break;
                    case ProtoTypeCode.Int32: defaultValue = (int)0; break;
                    case ProtoTypeCode.Int64: defaultValue = (long)0; break;
                    case ProtoTypeCode.SByte: defaultValue = (sbyte)0; break;
                    case ProtoTypeCode.UInt16: defaultValue = (ushort)0; break;
                    case ProtoTypeCode.UInt32: defaultValue = (uint)0; break;
                    case ProtoTypeCode.UInt64: defaultValue = (ulong)0; break;
                    case ProtoTypeCode.TimeSpan: defaultValue = TimeSpan.Zero; break;
                    case ProtoTypeCode.Guid: defaultValue = Guid.Empty; break;
                }
            }
            if ((attrib = GetAttribute(attribs, "System.ComponentModel.DefaultValueAttribute")) is object)
            {
                if (attrib.TryGet("Value", out object tmp)) defaultValue = tmp;
            }
            ValueMember vm = (isEnum || normalizedAttribute.Tag > 0)
                ? new ValueMember(model, Type, normalizedAttribute.Tag, member, effectiveType, repeated?.ItemType, null, normalizedAttribute.DataFormat, defaultValue)
                    : null;
            if (vm is object)
            {
                vm.CompatibilityLevel = memberCompatibility;
                vm.BackingMember = normalizedAttribute.BackingMember;
                Type finalType = Type;
                PropertyInfo prop = Helpers.GetProperty(finalType, member.Name + "Specified", true);
                MethodInfo getMethod = Helpers.GetGetMethod(prop, true, true);
                if (getMethod is null || getMethod.IsStatic) prop = null;
                if (prop is object)
                {
                    vm.SetSpecified(getMethod, Helpers.GetSetMethod(prop, true, true));
                }
                else
                {
                    MethodInfo method = Helpers.GetInstanceMethod(finalType, "ShouldSerialize" + member.Name, Type.EmptyTypes);
                    if (method is object && method.ReturnType == typeof(bool))
                    {
                        vm.SetSpecified(method, null);
                    }
                }
                if (!string.IsNullOrEmpty(normalizedAttribute.Name)) vm.SetName(normalizedAttribute.Name);
                vm.IsPacked = normalizedAttribute.IsPacked;
                vm.IsRequired = normalizedAttribute.IsRequired;
                vm.OverwriteList = normalizedAttribute.OverwriteList;
#if FEAT_DYNAMIC_REF
                if (normalizedAttribute.AsReferenceHasValue)
                {
                    vm.AsReference = normalizedAttribute.AsReference;
                }
                vm.DynamicType = normalizedAttribute.DynamicType;
#endif

                if (repeated is object)
                {
                    DataFormat keyFormat = DataFormat.Default, valueFormat = DataFormat.Default;
                    bool mapEnabled = true;
                    if ((attrib = GetAttribute(attribs, "ProtoBuf.ProtoMapAttribute")) is object)
                    {
                        if (attrib.TryGet(nameof(ProtoMapAttribute.DisableMap), out object tmp) && (bool)tmp)
                        {
                            mapEnabled = false;
                        }
                        else
                        {
                            if (attrib.TryGet(nameof(ProtoMapAttribute.KeyFormat), out tmp)) keyFormat = (DataFormat)tmp;
                            if (attrib.TryGet(nameof(ProtoMapAttribute.ValueFormat), out tmp)) valueFormat = (DataFormat)tmp;
                        }
                    }
                    if (mapEnabled && repeated.IsValidProtobufMap(model, vm.CompatibilityLevel, keyFormat))
                    {
                        vm.MapKeyFormat = keyFormat;
                        vm.MapValueFormat = valueFormat;
                        vm.IsMap = true;
                    }
                }
            }
            return vm;
        }

        private static void GetDataFormat(ref DataFormat value, AttributeMap attrib, string memberName)
        {
            if ((attrib is null) || (value != DataFormat.Default)) return;
            if (attrib.TryGet(memberName, out object obj) && obj is object) value = (DataFormat)obj;
        }

        private static void GetIgnore(ref bool ignore, AttributeMap attrib, AttributeMap[] attribs, string fullName)
        {
            if (ignore || attrib is null) return;
            ignore = GetAttribute(attribs, fullName) is object;
            return;
        }

        private static void GetFieldBoolean(ref bool value, AttributeMap attrib, string memberName)
        {
            GetFieldBoolean(ref value, attrib, memberName, true);
        }
        private static bool GetFieldBoolean(ref bool value, AttributeMap attrib, string memberName, bool publicOnly)
        {
            if (attrib is null) return false;
            if (value) return true;
            if (attrib.TryGet(memberName, publicOnly, out object obj) && obj is object)
            {
                value = (bool)obj;
                return true;
            }
            return false;
        }

        private static void GetFieldNumber(ref int value, AttributeMap attrib, string memberName)
        {
            if (attrib is null || value > 0) return;
            if (attrib.TryGet(memberName, out object obj) && obj is object) value = (int)obj;
        }

        private static void GetFieldName(ref string name, AttributeMap attrib, string memberName)
        {
            if (attrib is null || !string.IsNullOrEmpty(name)) return;
            if (attrib.TryGet(memberName, out object obj) && obj is object) name = (string)obj;
        }

        private static AttributeMap GetAttribute(AttributeMap[] attribs, string fullName)
        {
            for (int i = 0; i < attribs.Length; i++)
            {
                AttributeMap attrib = attribs[i];
                if (attrib is object && attrib.AttributeType.FullName == fullName) return attrib;
            }
            return null;
        }

        /// <summary>
        /// Adds a member (by name) to the MetaType
        /// </summary>        
        public MetaType Add(int fieldNumber, string memberName)
        {
            AddField(fieldNumber, memberName, null, null, null);
            return this;
        }

        /// <summary>
        /// Adds a member (by name) to the MetaType, returning the ValueMember rather than the fluent API.
        /// This is otherwise identical to Add.
        /// </summary>
        public ValueMember AddField(int fieldNumber, string memberName)
        {
            return AddField(fieldNumber, memberName, null, null, null);
        }

        /// <summary>
        /// Gets or sets whether the type should use a parameterless constructor (the default),
        /// or whether the type should skip the constructor completely. This option is not supported
        /// on compact-framework.
        /// </summary>
        public bool UseConstructor
        { // negated to have defaults as flat zero
            get { return !HasFlag(TypeOptions.SkipConstructor); }
            set { SetFlag(TypeOptions.SkipConstructor, !value, true); }
        }

        /// <summary>
        /// The concrete type to create when a new instance of this type is needed; this may be useful when dealing
        /// with dynamic proxies, or with interface-based APIs
        /// </summary>
        public Type ConstructType
        {
            get { return constructType; }
            set
            {
                ThrowIfFrozen();
                constructType = value;
            }
        }

        private Type constructType;
        /// <summary>
        /// Adds a member (by name) to the MetaType
        /// </summary>     
        public MetaType Add(string memberName)
        {
            Add(GetNextFieldNumber(), memberName);
            return this;
        }

        internal Type surrogateType;
        internal DataFormat surrogateDataFormat;
        private MethodInfo underlyingToSurrogate, surrogateToUnderlying;

        /// <summary>
        /// Performs serialization of this type via a surrogate; all
        /// other serialization options are ignored and handled
        /// by the surrogate's configuration.
        /// </summary>
        public void SetSurrogate(Type surrogateType)
            => SetSurrogate(surrogateType, null, null, DataFormat.Default);

        internal void SetSurrogate(Type surrogateType, MethodInfo underlyingToSurrogate, MethodInfo surrogateToUnderlying, DataFormat dataFormat)
        {
            if (surrogateType == Type) surrogateType = null;
            if (surrogateType is object)
            {
                // note that BuildSerializer checks the **CURRENT TYPE** is OK to be surrogated
                if (surrogateType != typeof(string) && typeof(IEnumerable).IsAssignableFrom(surrogateType))
                {
                    ThrowHelper.ThrowArgumentException("Repeated data (a list, collection, etc) has inbuilt behaviour and cannot be used as a surrogate");
                }

                if ((BaseType is object && BaseType != this) || (_subTypes?.Count ?? 0) > 0)
                    ThrowSubTypeWithSurrogate(Type);

                if (surrogateType.IsGenericTypeDefinition)
                {
                    if (!Type.IsGenericType)
                    {
                        ThrowHelper.ThrowArgumentException("Cannot use an open generic type as a surrogate for a non generic type");
                    }
                    var genericArguments = Type.GetGenericArguments();
                    if (genericArguments.Length != surrogateType.GetGenericArguments().Length)
                    {
                        ThrowHelper.ThrowArgumentException("The generic type parameters of the surrogate must match the generic arguments of the target type");
                    }
                    surrogateType = surrogateType.MakeGenericType(genericArguments);
                }
            }

            int opaqueToken = default;
            try
            {
                model.TakeLock(ref opaqueToken);
                ThrowIfFrozen();

                this.surrogateType = surrogateType;
                this.underlyingToSurrogate = underlyingToSurrogate; // note: treated as trusted/verified
                this.surrogateToUnderlying = surrogateToUnderlying; // note: treated as trusted/verified
                this.surrogateDataFormat = dataFormat;
                SetFlag(TypeOptions.AutoTuple, false, false); // no longer an auto-tuple

                // no point in offering chaining; no other options are respected
            }
            finally
            {
                model.ReleaseLock(opaqueToken);
            }
            
        }

        internal bool HasSurrogate
        {
            get
            {
                return surrogateType is object;
            }
        }

        internal MetaType GetSurrogateOrSelf()
        {
            if (surrogateType is object) return model[surrogateType];
            return this;
        }

        internal MetaType GetSurrogateOrBaseOrSelf(bool deep)
        {
            if (surrogateType is object) return model[surrogateType];
            MetaType snapshot = this.baseType;
            if (snapshot is object)
            {
                if (deep)
                {
                    MetaType tmp;
                    do
                    {
                        tmp = snapshot;
                        snapshot = snapshot.baseType;
                    } while (snapshot is object);
                    return tmp;
                }
                return snapshot;
            }
            return this;
        }

        private int GetNextFieldNumber()
        {
            int maxField = 0;
            if (HasFields)
            {
                foreach (ValueMember member in Fields)
                {
                    if (member.FieldNumber > maxField) maxField = member.FieldNumber;
                }
            }
            if (_subTypes is object)
            {
                foreach (SubType subType in _subTypes)
                {
                    if (subType.FieldNumber > maxField) maxField = subType.FieldNumber;
                }
            }
            return maxField + 1;
        }

        /// <summary>
        /// Adds a set of members (by name) to the MetaType
        /// </summary>     
        public MetaType Add(params string[] memberNames)
        {
            if (memberNames is null) throw new ArgumentNullException(nameof(memberNames));
            int next = GetNextFieldNumber();
            for (int i = 0; i < memberNames.Length; i++)
            {
                Add(next++, memberNames[i]);
            }
            return this;
        }

        /// <summary>
        /// Adds a member (by name) to the MetaType
        /// </summary>
        public MetaType Add(int fieldNumber, string memberName, object defaultValue)
        {
            AddField(fieldNumber, memberName, null, null, defaultValue);
            return this;
        }

        /// <summary>
        /// Adds a member (by name) to the MetaType, including an itemType and defaultType for representing lists
        /// </summary>
        public MetaType Add(int fieldNumber, string memberName, Type itemType, Type defaultType)
        {
            AddField(fieldNumber, memberName, itemType, defaultType, null);
            return this;
        }

        /// <summary>
        /// Adds a member (by name) to the MetaType, including an itemType and defaultType for representing lists, returning the ValueMember rather than the fluent API.
        /// This is otherwise identical to Add.
        /// </summary>
        public ValueMember AddField(int fieldNumber, string memberName, Type itemType, Type defaultType)
        {
            return AddField(fieldNumber, memberName, itemType, defaultType, null);
        }

        private ValueMember AddField(int fieldNumber, string memberName, Type itemType, Type defaultType, object defaultValue)
        {
            MemberInfo mi = null;
            MemberInfo[] members = Type.GetMember(memberName, Type.IsEnum ? BindingFlags.Static | BindingFlags.Public : BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (members is object && members.Length == 1) mi = members[0];
            if (mi is null) throw new ArgumentException("Unable to determine member: " + memberName, nameof(memberName));

            Type miType;
            PropertyInfo pi = null;
            switch (mi.MemberType)
            {
                case MemberTypes.Field:
                    var fi = (FieldInfo)mi;
                    miType = fi.FieldType; break;
                case MemberTypes.Property:
                    pi = (PropertyInfo)mi;
                    miType = pi.PropertyType; break;
                default:
                    throw new NotSupportedException(mi.MemberType.ToString());
            }
            var repeated = model.TryGetRepeatedProvider(miType);
            if (itemType is object && repeated?.ItemType != itemType) ThrowHelper.ThrowInvalidOperationException("Expected item type of " + repeated?.ItemType.NormalizeName());

            MemberInfo backingField = null;
            if (pi?.CanWrite == false)
            {
                var backingMembers = Type.GetMember($"<{((PropertyInfo)mi).Name}>k__BackingField", Type.IsEnum ? BindingFlags.Static | BindingFlags.Public : BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (backingMembers is object && backingMembers.Length == 1 && backingMembers[0] is FieldInfo)
                    backingField = backingMembers[0];
            }
            if (repeated is object)
            {
                if (defaultType is object && defaultType != repeated.ForType)
                    ThrowHelper.ThrowNotSupportedException("Default types for collections are not currently supported; recommendation: initialize the colleciton in the type");
                defaultType = repeated.ForType;
            }

            ValueMember newField = new ValueMember(model, Type, fieldNumber, backingField ?? mi, miType, repeated?.ItemType, defaultType, DataFormat.Default, defaultValue)
            {
                CompatibilityLevel = CompatibilityLevel // default to inherited
            };
            if (backingField is object)
                newField.SetName(mi.Name);
            Add(newField);
            return newField;
        }

        private void Add(ValueMember member)
        {
            if (Type.IsEnum) ThrowHelper.ThrowInvalidOperationException($"Enums should use {nameof(SetEnumValues)} to customize the enum definitions");
            int opaqueToken = 0;
            try
            {
                model.TakeLock(ref opaqueToken);
                ThrowIfFrozen();
                Fields.Add(member);
            }
            finally
            {
                model.ReleaseLock(opaqueToken);
            }
        }
        /// <summary>
        /// Returns the ValueMember that matchs a given field number, or null if not found
        /// </summary>
        public ValueMember this[int fieldNumber]
        {
            get
            {
                if (HasFields)
                {
                    foreach (ValueMember member in Fields)
                    {
                        if (member.FieldNumber == fieldNumber) return member;
                    }
                }
                return null;
            }
        }
        /// <summary>
        /// Returns the ValueMember that matchs a given member (property/field), or null if not found
        /// </summary>
        public ValueMember this[MemberInfo member]
        {
            get
            {
                if (member is null || !HasFields) return null;
                foreach (ValueMember x in Fields)
                {
                    if (x.Member == member || x.BackingMember == member) return x;
                }
                return null;
            }
        }

        private List<ValueMember> _fields = null;
        internal bool HasFields => _fields is object && _fields.Count != 0;
        internal List<ValueMember> Fields => _fields ??= new List<ValueMember>();

        private List<EnumMember> _enums = new List<EnumMember>();
        internal List<EnumMember> Enums => _enums ??= new List<EnumMember>();
        internal bool HasEnums => _enums is object && _enums.Count != 0;

        /// <summary>
        /// Returns the ValueMember instances associated with this type
        /// </summary>
        public ValueMember[] GetFields()
        {
            if (!HasFields) return Array.Empty<ValueMember>();
            var arr = Fields.ToArray();
            Array.Sort(arr, ValueMember.Comparer.Default);
            return arr;
        }

        /// <summary>
        /// Returns the EnumMember instances associated with this type
        /// </summary>
        public EnumMember[] GetEnumValues()
        {
            if (!HasEnums) return Array.Empty<EnumMember>();
            return Enums.ToArray();
        }

        /// <summary>
        /// Add a new defined name/value pair for an enum
        /// </summary>
        public void SetEnumValues(EnumMember[] values)
        {
            if (!Type.IsEnum) ThrowHelper.ThrowInvalidOperationException($"Only enums should use {nameof(SetEnumValues)}");

            if (values is null) ThrowHelper.ThrowArgumentNullException(nameof(values));

            var typedClone = Array.ConvertAll(values, val => val.Normalize(Type));

            foreach (var val in values)
                val.Validate();

            int opaqueToken = 0;
            try
            {
                model.TakeLock(ref opaqueToken);
                ThrowIfFrozen();
                Enums.Clear();
                Enums.AddRange(typedClone);
            }
            finally
            {
                model.ReleaseLock(opaqueToken);
            }
        }

        internal bool IsValidEnum() => IsValidEnum(_enums);

        internal static bool IsValidEnum(IList<EnumMember> values)
        {
            if (values is null || values.Count == 0) return false;
            foreach(var val in values)
            {
                if (!val.TryGetInt32().HasValue) return false;
            }
            return true;
        }

        /// <summary>
        /// Returns the SubType instances associated with this type
        /// </summary>
        public SubType[] GetSubtypes()
        {
            if (_subTypes is null || _subTypes.Count == 0) return Array.Empty<SubType>();
            var arr = _subTypes.ToArray();
            Array.Sort(arr, SubType.Comparer.Default);
            return arr;
        }

        internal IEnumerable<Type> GetAllGenericArguments()
        {
            return GetAllGenericArguments(Type);
        }

        private static IEnumerable<Type> GetAllGenericArguments(Type type)
        {
            var genericArguments = type.GetGenericArguments();
            foreach (var arg in genericArguments)
            {
                yield return arg;
                foreach (var inner in GetAllGenericArguments(arg))
                {
                    yield return inner;
                }
            }
        }

        /// <summary>
        /// Compiles the serializer for this type; this is *not* a full
        /// standalone compile, but can significantly boost performance
        /// while allowing additional types to be added.
        /// </summary>
        /// <remarks>An in-place compile can access non-public types / members</remarks>
        public void CompileInPlace()
        {
            var original = Serializer; // might lazily create
            if (original is ICompiledSerializer || original.ExpectedType.IsEnum || model.TryGetRepeatedProvider(Type) is object)
                return; // nothing to do
            
            var wrapped = CompiledSerializer.Wrap(original, model);
            if (!ReferenceEquals(original, wrapped))
            {
                _serializer = (IProtoTypeSerializer) wrapped;
                Model.ResetServiceCache(Type);
            }
            
        }

        internal bool IsDefined(int fieldNumber)
        {
            if (HasFields)
            {
                foreach (ValueMember field in Fields)
                {
                    if (field.FieldNumber == fieldNumber) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Gets or sets a value indicating that an enum should be treated directly as an int/short/etc, rather
        /// than enforcing .proto enum rules. This is useful *in particul* for [Flags] enums.
        /// </summary>
        public bool EnumPassthru
        {
            [Obsolete(ProtoEnumAttribute.EnumValueDeprecated, false)]
            get => Type.IsEnum;
            [Obsolete(ProtoEnumAttribute.EnumValueDeprecated, true)]
            set { if (value != EnumPassthru) ThrowHelper.ThrowNotSupportedException(); }
        }

        /// <summary>
        /// Gets or sets a value indicating that this type should NOT be treated as a list, even if it has
        /// familiar list-like characteristics (enumerable, add, etc)
        /// </summary>
        public bool IgnoreListHandling
        {
            get { return HasFlag(TypeOptions.IgnoreListHandling); }
            set
            {
                SetFlag(TypeOptions.IgnoreListHandling, value, true);
                model.ResetServiceCache(Type); // changes how collections are handled
            }
        }

        /// <summary>
        /// Gets or sets a value indicating whether unknown sub-types should cause serialization failure
        /// </summary>
        public bool IgnoreUnknownSubTypes
        {
            get => HasFlag(TypeOptions.IgnoreUnknownSubTypes);
            set => SetFlag(TypeOptions.IgnoreUnknownSubTypes, value, true);
        }

        internal bool Pending
        {
            get { return HasFlag(TypeOptions.Pending); }
            set { SetFlag(TypeOptions.Pending, value, false); }
        }

        private enum TypeOptions : ushort
        {
            None = 0,
            Pending = 1,
            // EnumPassThru = 2,
            Frozen = 4,
            PrivateOnApi = 8,
            SkipConstructor = 16,
#if FEAT_DYNAMIC_REF
            AsReferenceDefault = 32,
#endif
            AutoTuple = 64,
            IgnoreListHandling = 128,
            IsGroup = 256,
            IgnoreUnknownSubTypes = 512,
        }

        private volatile TypeOptions flags;
        private bool HasFlag(TypeOptions flag) { return (flags & flag) == flag; }
        private void SetFlag(TypeOptions flag, bool value, bool throwIfFrozen)
        {
            if (throwIfFrozen && HasFlag(flag) != value)
            {
                ThrowIfFrozen();
            }
            if (value)
                flags |= flag;
            else
                flags &= ~flag;
        }

        private Type _serializerType;

        /// <summary>
        /// Specify a custom serializer for this type
        /// </summary>
        public Type SerializerType
        {
            get => _serializerType;
            set
            {
                if (value != _serializerType)
                {
                    if (!value.IsClass)
                        ThrowHelper.ThrowArgumentException("Custom serializer providers must be classes", nameof(SerializerType));
                    ThrowIfFrozen();
                    _serializerType = value;
                }
            }
        }

        internal static MetaType GetRootType(MetaType source)
        {
            while (source._serializer is object)
            {
                MetaType tmp = source.baseType;
                if (tmp is null) return source;
                source = tmp; // else loop until we reach something that isn't generated, or is the root
            }

            // now we get into uncertain territory
            RuntimeTypeModel model = source.model;
            int opaqueToken = 0;
            try
            {
                model.TakeLock(ref opaqueToken);

                MetaType tmp;
                while ((tmp = source.baseType) is object) source = tmp;
                return source;
            }
            finally
            {
                model.ReleaseLock(opaqueToken);
            }
        }

        internal bool IsPrepared()
        {
            return _serializer is CompiledSerializer;
        }

        internal static StringBuilder NewLine(StringBuilder builder, int indent)
        {
            return builder.AppendLine().Append(' ', indent * 3);
        }

        internal bool IsAutoTuple => HasFlag(TypeOptions.AutoTuple);

        /// <summary>
        /// Indicates whether this type should always be treated as a "group" (rather than a string-prefixed sub-message)
        /// </summary>
        public bool IsGroup
        {
            get { return HasFlag(TypeOptions.IsGroup); }
            set { SetFlag(TypeOptions.IsGroup, value, true); }
        }

        internal void WriteSchema(HashSet<Type> callstack, StringBuilder builder, int indent, HashSet<string> imports, ProtoSyntax syntax,
            string package, SchemaGenerationFlags flags)
        {
            if (surrogateType is object) return; // nothing to write

            bool multipleNamespaceSupport = (flags & SchemaGenerationFlags.MultipleNamespaceSupport) != 0;
            var repeated = model.TryGetRepeatedProvider(Type);

            if (repeated is object)
            {
                NewLine(builder, indent).Append("message ").Append(GetSchemaTypeName(callstack)).Append(" {");

                if (repeated.IsValidProtobufMap(model, CompatibilityLevel, DataFormat.Default))
                {
                    repeated.ResolveMapTypes(out var key, out var value);

                    NewLine(builder, indent + 1).Append("map<")
                        .Append(model.GetSchemaTypeName(callstack, key, DataFormat.Default, CompatibilityLevel, false, false, imports))
                        .Append(", ")
                        .Append(model.GetSchemaTypeName(callstack, value, DataFormat.Default, CompatibilityLevel, false, false, imports))
                        .Append("> items = 1;");
                }
                else
                {
                    NewLine(builder, indent + 1).Append("repeated ")
                        .Append(model.GetSchemaTypeName(callstack, repeated.ItemType, DataFormat.Default, CompatibilityLevel, false, false, imports))
                        .Append(" items = 1;");
                }
                NewLine(builder, indent).Append('}');
            }
            else if (IsAutoTuple)
            { // key-value-pair etc
                if (ResolveTupleConstructor(Type, out MemberInfo[] mapping) is object)
                {
                    NewLine(builder, indent).Append("message ").Append(GetSchemaTypeName(callstack)).Append(" {");
                    AddNamespace(imports);

                    for (int i = 0; i < mapping.Length; i++)
                    {
                        Type effectiveType;
                        if (mapping[i] is PropertyInfo property)
                        {
                            effectiveType = property.PropertyType;
                        }
                        else if (mapping[i] is FieldInfo field)
                        {
                            effectiveType = field.FieldType;
                        }
                        else
                        {
                            throw new NotSupportedException("Unknown member type: " + mapping[i].GetType().Name);
                        }
                        NewLine(builder, indent + 1).Append(syntax == ProtoSyntax.Proto2 ? "optional " : "")
                            .Append(model.GetSchemaTypeName(callstack, effectiveType, DataFormat.Default, CompatibilityLevel, false, false, imports))
                            .Append(' ').Append(mapping[i].Name).Append(" = ").Append(i + 1).Append(';');
                    }
                    NewLine(builder, indent).Append('}');
                }
            }
            else if (Type.IsEnum)
            {
                var enums = GetEnumValues();


                bool allValid = IsValidEnum(enums);
                if (!allValid) NewLine(builder, indent).Append("/* for context only");
                NewLine(builder, indent).Append("enum ").Append(GetSchemaTypeName(callstack)).Append(" {");
                AddNamespace(imports);

                if (Type.IsDefined(typeof(FlagsAttribute), true))
                {
                    NewLine(builder, indent + 1).Append("// this is a composite/flags enumeration");
                }                

                bool needsAlias = false; // check whether we need to allow duplicate names
                var uniqueFields = new HashSet<int>();
                foreach (var field in enums)
                {
                    var parsed = field.TryGetInt32();
                    if (parsed.HasValue && !uniqueFields.Add(parsed.Value))
                    {
                        needsAlias = true;
                        break;
                    }
                }

                if (needsAlias)
                {   // duplicated value requires allow_alias
                    NewLine(builder, indent + 1).Append("option allow_alias = true;");
                }

                bool haveWrittenZero = false;
                // write zero values **first**
                foreach (var member in enums)
                {
                    var parsed = member.TryGetInt32();
                    if (parsed.HasValue && parsed.Value == 0)
                    {
                        NewLine(builder, indent + 1).Append(member.Name).Append(" = 0;");
                        haveWrittenZero = true;
                    }
                }

                if (syntax == ProtoSyntax.Proto3 && !haveWrittenZero)
                {
                    NewLine(builder, indent + 1).Append("ZERO = 0; // proto3 requires a zero value as the first item (it can be named anything)");
                }

                // note array is already sorted, so zero would already be first
                foreach (var member in enums)
                {
                    var parsed = member.TryGetInt32();
                    if (parsed.HasValue)
                    {
                        if (parsed.Value == 0) continue;
                        NewLine(builder, indent + 1).Append(member.Name).Append(" = ").Append(parsed.Value).Append(';');
                    }
                    else
                    {
                        NewLine(builder, indent + 1).Append("// ").Append(member.Name).Append(" = ").Append(member.Value).Append(';').Append(" // note: enums should be valid 32-bit integers");
                    }
                }
                if (HasReservations) AppendReservations();
                NewLine(builder, indent).Append('}');
                if (!allValid) NewLine(builder, indent).Append("*/");
            }
            else
            {
                ValueMember[] fieldsArr = GetFields();
                NewLine(builder, indent).Append("message ").Append(GetSchemaTypeName(callstack)).Append(" {");
                AddNamespace(imports);
                foreach (ValueMember member in fieldsArr)
                {
                    string schemaTypeName;
                    bool hasOption = false;
                    if (member.IsMap)
                    {
                        repeated = model.TryGetRepeatedProvider(member.MemberType);
                        repeated.ResolveMapTypes(out var keyType, out var valueType);

                        var keyTypeName = model.GetSchemaTypeName(callstack, keyType, member.MapKeyFormat, CompatibilityLevel, false, false, imports);
                        schemaTypeName = model.GetSchemaTypeName(callstack, valueType, member.MapValueFormat, CompatibilityLevel, member.AsReference, member.DynamicType, imports);
                        NewLine(builder, indent + 1).Append("map<").Append(keyTypeName).Append(',').Append(schemaTypeName).Append("> ")
                            .Append(member.Name).Append(" = ").Append(member.FieldNumber).Append(';');
                    }
                    else
                    {
                        string ordinality = member.ItemType is object ? "repeated " : (syntax == ProtoSyntax.Proto2 ? (member.IsRequired ? "required " : "optional ") : "");
                        NewLine(builder, indent + 1).Append(ordinality);
                        if (member.DataFormat == DataFormat.Group) builder.Append("group ");

                        schemaTypeName = member.GetSchemaTypeName(callstack, true, imports, out var altName);
                        builder.Append(schemaTypeName).Append(' ')
                             .Append(member.Name).Append(" = ").Append(member.FieldNumber);

                        if (syntax == ProtoSyntax.Proto2 && member.DefaultValue is object && !member.IsRequired)
                        {
                            if (member.DefaultValue is string)
                            {
                                AddOption(builder, ref hasOption).Append("default = \"").Append(member.DefaultValue).Append('\"');
                            }
                            else if (member.DefaultValue is TimeSpan)
                            {
                                // ignore
                            }
                            else if (member.DefaultValue is bool boolValue)
                            {   // need to be lower case (issue 304)
                                AddOption(builder, ref hasOption).Append(boolValue ? "default = true" : "default = false");
                            }
                            else
                            {
                                object effectiveValue = member.DefaultValue;
                                if (effectiveValue is Enum &&effectiveValue.GetType() == member.MemberType
                                    && model.IsDefined(member.MemberType, member.CompatibilityLevel))
                                {
                                    // lookup the enum name
                                    var enumType = model[member.MemberType];
                                    foreach (var val in enumType.Enums)
                                    {
                                        if (!string.IsNullOrWhiteSpace(val.Name) && effectiveValue.Equals(val.Value))
                                        {
                                            effectiveValue = val.Name;
                                            break;
                                        }
                                    }
                                }
                                AddOption(builder, ref hasOption).Append("default = ").Append(member.DefaultValue);
                            }
                        }
                        if (CanPack(member.ItemType))
                        {
                            if (syntax == ProtoSyntax.Proto2)
                            {
                                if (member.IsPacked) AddOption(builder, ref hasOption).Append("packed = true"); // disabled by default
                            }
                            else
                            {
                                if (!member.IsPacked) AddOption(builder, ref hasOption).Append("packed = false"); // enabled by default
                            }
                        }
                        if (member.AsReference)
                        {
                            imports.Add(RuntimeTypeModel.CommonImports.Protogen);
                            AddOption(builder, ref hasOption).Append("(.protobuf_net.fieldopt).asRef = true");
                        }
                        if (member.DynamicType)
                        {
                            imports.Add(RuntimeTypeModel.CommonImports.Protogen);
                            AddOption(builder, ref hasOption).Append("(.protobuf_net.fieldopt).dynamicType = true");
                        }
                        CloseOption(builder, ref hasOption).Append(';');
                        if (syntax != ProtoSyntax.Proto2 && member.DefaultValue is object && !member.IsRequired)
                        {
                            if (IsImplicitDefault(member.DefaultValue))
                            {
                                // don't emit; we're good
                            }
                            else
                            {
                                builder.Append(" // default value could not be applied: ").Append(member.DefaultValue);
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(altName))
                            builder.Append(" // declared as invalid enum: ").Append(altName);
                    }
                    if (schemaTypeName == ".bcl.NetObjectProxy" && member.AsReference && !member.DynamicType) // we know what it is; tell the user
                    {
                        builder.Append(" // reference-tracked ").Append(member.GetSchemaTypeName(callstack, false, imports, out _));
                    }
                }
                if (_subTypes is object && _subTypes.Count != 0)
                {
                    SubType[] subTypeArr = _subTypes.ToArray();
                    Array.Sort(subTypeArr, SubType.Comparer.Default);
                    string[] fieldNames = new string[subTypeArr.Length];
                    for (int i = 0; i < subTypeArr.Length; i++)
                        fieldNames[i] = subTypeArr[i].DerivedType.GetSchemaTypeName(callstack);

                    string fieldName = "subtype";
                    while (Array.IndexOf(fieldNames, fieldName) >= 0)
                        fieldName = "_" + fieldName;


                    NewLine(builder, indent + 1).Append("oneof ").Append(fieldName).Append(" {");

                    if ((flags & SchemaGenerationFlags.PreserveSubType) != 0)
                    {
                        imports.Add(RuntimeTypeModel.CommonImports.Protogen);
                        NewLine(builder, indent + 2).Append("option (.protobuf_net.oneofopt).isSubType = true;");
                    }
                    for (int i = 0; i < subTypeArr.Length; i++)
                    {
                        var subTypeName = fieldNames[i];
                        NewLine(builder, indent + 2).Append(subTypeName)
                               .Append(' ').Append(subTypeName).Append(" = ").Append(subTypeArr[i].FieldNumber).Append(';');
                    }
                    NewLine(builder, indent + 1).Append('}');
                }
                if (HasReservations) AppendReservations();
                NewLine(builder, indent).Append('}');
            }

            void AddNamespace(HashSet<string> imports)
            {
                if (!multipleNamespaceSupport || IsAutoTuple || string.IsNullOrWhiteSpace(Type.Namespace) || Type.Namespace == package)
                    return;

                imports.Add(RuntimeTypeModel.CommonImports.Protogen);
                NewLine(builder, indent + 1).Append("option (.protobuf_net.");
                if (Type.IsEnum)
                {
                    builder.Append("enumopt");
                }
                else
                {
                    builder.Append("msgopt");
                }
                builder.Append(").namespace = \"" + Type.Namespace + "\";");

            }
            void AppendReservations()
            {
                foreach (var reservation in _reservations)
                {
                    NewLine(builder, indent + 1).Append("reserved ");
                    if (reservation.From != 0)
                    {
                        builder.Append(reservation.From);
                        if (reservation.To != reservation.From) builder.Append(" to ").Append(reservation.To);

                    }
                    else
                    {
                        builder.Append('\"').Append(reservation.Name).Append('\"');
                    }
                    builder.Append(';');
                    if (!string.IsNullOrWhiteSpace(reservation.Comment))
                        builder.Append(" /* ").Append(reservation.Comment).Append(" */");
                }
            }
        }

        private static StringBuilder AddOption(StringBuilder builder, ref bool hasOption)
        {
            if (hasOption)
                return builder.Append(", ");
            hasOption = true;
            return builder.Append(" [");
        }

        private static StringBuilder CloseOption(StringBuilder builder, ref bool hasOption)
        {
            if (hasOption)
            {
                hasOption = false;
                return builder.Append(']');
            }
            return builder;
        }

        private static bool IsImplicitDefault(object value)
        {
            try
            {
                if (value is null) return false;
                switch (Helpers.GetTypeCode(value.GetType()))
                {
                    case ProtoTypeCode.Boolean: return !(bool)value;
                    case ProtoTypeCode.Byte: return ((byte)value) == 0;
                    case ProtoTypeCode.Char: return ((char)value) == '\0';
                    case ProtoTypeCode.DateTime: return ((DateTime)value) == default;
                    case ProtoTypeCode.Decimal: return ((decimal)value) == 0M;
                    case ProtoTypeCode.Double: return ((double)value) == 0;
                    case ProtoTypeCode.Int16: return ((short)value) == 0;
                    case ProtoTypeCode.Int32: return ((int)value) == 0;
                    case ProtoTypeCode.Int64: return ((long)value) == 0;
                    case ProtoTypeCode.SByte: return ((sbyte)value) == 0;
                    case ProtoTypeCode.Single: return ((float)value) == 0;
                    case ProtoTypeCode.String: return value is object && ((string)value).Length == 0;
                    case ProtoTypeCode.TimeSpan: return ((TimeSpan)value) == TimeSpan.Zero;
                    case ProtoTypeCode.UInt16: return ((ushort)value) == 0;
                    case ProtoTypeCode.UInt32: return ((uint)value) == 0;
                    case ProtoTypeCode.UInt64: return ((ulong)value) == 0;
                }
            }
            catch { }
            return false;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0066:Convert switch statement to expression", Justification = "Readability")]
        private static bool CanPack(Type type)
        {
            if (type is null) return false;
            switch (Helpers.GetTypeCode(type))
            {
                case ProtoTypeCode.Boolean:
                case ProtoTypeCode.Byte:
                case ProtoTypeCode.Char:
                case ProtoTypeCode.Double:
                case ProtoTypeCode.Int16:
                case ProtoTypeCode.Int32:
                case ProtoTypeCode.Int64:
                case ProtoTypeCode.SByte:
                case ProtoTypeCode.Single:
                case ProtoTypeCode.UInt16:
                case ProtoTypeCode.UInt32:
                case ProtoTypeCode.UInt64:
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Apply a shift to all fields (and sub-types) on this type
        /// </summary>
        /// <param name="offset">The change in field number to apply</param>
        /// <remarks>The resultant field numbers must still all be considered valid</remarks>
        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
        public void ApplyFieldOffset(int offset)
        {
            if (Type.IsEnum) throw new InvalidOperationException("Cannot apply field-offset to an enum");
            if (offset == 0) return; // nothing to do
            int opaqueToken = 0;
            try
            {
                model.TakeLock(ref opaqueToken);
                ThrowIfFrozen();

                var fields = _fields;
                var subTypes = _subTypes;
                if (fields is object)
                {
                    foreach (ValueMember field in fields)
                        AssertValidFieldNumber(field.FieldNumber + offset);
                }
                if (subTypes is object)
                {
                    foreach (SubType subType in subTypes)
                        AssertValidFieldNumber(subType.FieldNumber + offset);
                }

                // we've checked the ranges are all OK; since we're moving everything, we can't overlap ourselves
                // so: we can just move
                if (fields is object)
                {
                    foreach (ValueMember field in fields)
                        field.FieldNumber += offset;
                }
                if (subTypes is object)
                {
                    foreach (SubType subType in subTypes)
                        subType.FieldNumber += offset;
                }
            }
            finally
            {
                model.ReleaseLock(opaqueToken);
            }
        }

        internal static void AssertValidFieldNumber(int fieldNumber)
        {
            if (fieldNumber < 1) throw new ArgumentOutOfRangeException(nameof(fieldNumber));
        }

        /// <summary>
        /// Adds a single number field reservation
        /// </summary>
        public MetaType AddReservation(int field, string comment = null)
            => AddReservation(new ProtoReservedAttribute(field, comment));
        /// <summary>
        /// Adds range number field reservation
        /// </summary>
        public MetaType AddReservation(int from, int to, string comment = null)
            => AddReservation(new ProtoReservedAttribute(from, to, comment));
        /// <summary>
        /// Adds a named field reservation
        /// </summary>
        public MetaType AddReservation(string field, string comment = null)
            => AddReservation(new ProtoReservedAttribute(field, comment));
        private MetaType AddReservation(ProtoReservedAttribute reservation)
        {
            reservation.Verify();
            int opaqueToken = default;
            try
            {
                model.TakeLock(ref opaqueToken);
                ThrowIfFrozen();
                _reservations ??= new List<ProtoReservedAttribute>();
                _reservations.Add(reservation);
            }
            finally
            {
                model.ReleaseLock(opaqueToken);
            }
            return this;
        }

        private List<ProtoReservedAttribute> _reservations;

        internal bool HasReservations => (_reservations?.Count ?? 0) != 0;

        internal void Validate() => ValidateReservations(); // just this for now, but: in case we need more later

        internal void ValidateReservations()
        {
            if (!(HasReservations && (HasFields || HasSubtypes || HasEnums))) return;

            foreach (var reservation in _reservations)
            {
                if (reservation.From != 0)
                {
                    if (_fields is object)
                    {
                        foreach (var field in _fields)
                        {
                            if (field.FieldNumber >= reservation.From && field.FieldNumber <= reservation.To)
                            {
                                throw new InvalidOperationException($"Field {field.FieldNumber} is reserved and cannot be used for data member '{field.Name}'{CommentSuffix(reservation)}.");
                            }
                        }
                    }
                    if (_enums is object)
                    {
                        foreach (var @enum in _enums)
                        {
                            var val = @enum.TryGetInt32();
                            if (val.HasValue && val.Value >= reservation.From && val.Value <= reservation.To)
                            {
                                throw new InvalidOperationException($"Field {val.Value} is reserved and cannot be used for enum value '{@enum.Name}'{CommentSuffix(reservation)}.");
                            }
                        }
                    }
                    if (_subTypes is object)
                    {
                        foreach (var subType in _subTypes)
                        {
                            if (subType.FieldNumber >= reservation.From && subType.FieldNumber <= reservation.To)
                            {
                                throw new InvalidOperationException($"Field {subType.FieldNumber} is reserved and cannot be used for sub-type '{subType.DerivedType.Type.NormalizeName()}'{CommentSuffix(reservation)}.");
                            }
                        }
                    }
                }
                else
                {
                    if (_fields is object)
                    {
                        foreach (var field in _fields)
                        {
                            if (field.Name == reservation.Name)
                            {
                                throw new InvalidOperationException($"Field '{field.Name}' is reserved and cannot be used for data member {field.FieldNumber}{CommentSuffix(reservation)}.");
                            }
                        }
                    }
                    if (_enums is object)
                    {
                        foreach (var @enum in _enums)
                        {
                            if (@enum.Name == reservation.Name)
                            {
                                throw new InvalidOperationException($"Field '{@enum.Name}' is reserved and cannot be used for enum value {@enum.Value}{CommentSuffix(reservation)}.");
                            }
                        }
                    }
                    if (_subTypes is object)
                    {
                        foreach (var subType in _subTypes)
                        {
                            var name = subType.DerivedType.Name;
                            if (string.IsNullOrWhiteSpace(name)) name = subType.DerivedType.Type.Name;
                            if (name == reservation.Name)
                            {
                                throw new InvalidOperationException($"Field '{name}' is reserved and cannot be used for sub-type {subType.FieldNumber}{CommentSuffix(reservation)}.");
                            }
                        }
                    }
                }
            }

            static string CommentSuffix(ProtoReservedAttribute reservation)
            {
                var comment = reservation.Comment;
                if (string.IsNullOrWhiteSpace(comment)) return "";
                return " (" + comment.Trim() + ")";
            }
        }
    }
}
