using ProtoBuf.Compiler;
using ProtoBuf.Internal;
using ProtoBuf.Internal.Serializers;
using ProtoBuf.Serializers;
using ProtoBuf.WellKnownTypes;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

#pragma warning disable IDE0079 // sorry IDE, you're wrong

namespace ProtoBuf.Meta
{
    /// <summary>
    /// Provides protobuf serialization support for a number of types that can be defined at runtime
    /// </summary>
    public sealed class RuntimeTypeModel : TypeModel
    {
        /// <summary>
        /// Ensures that RuntimeTypeModel has been initialized, in advance of using methods on <see cref="Serializer"/>.
        /// </summary>
        public static void Initialize() => _ = Default;

        private RuntimeTypeModelOptions _options;

        private enum RuntimeTypeModelOptions
        {
            None = 0,
            InternStrings = TypeModelOptions.InternStrings,
            IncludeDateTimeKind = TypeModelOptions.IncludeDateTimeKind,
            SkipZeroLengthPackedArrays = TypeModelOptions.SkipZeroLengthPackedArrays,
            AllowPackedEncodingAtRoot = TypeModelOptions.AllowPackedEncodingAtRoot,

            TypeModelMask = InternStrings | IncludeDateTimeKind | SkipZeroLengthPackedArrays | AllowPackedEncodingAtRoot,

            // stuff specific to RuntimeTypeModel
            InferTagFromNameDefault = 1 << 10,
            IsDefaultModel = 1 << 11,
            Frozen = 1 << 12,
            AutoAddMissingTypes = 1 << 13,
            AutoCompile = 1 << 14,
            UseImplicitZeroDefaults = 1 << 15,
            AllowParseableTypes = 1 << 16,
            AutoAddProtoContractTypesOnly = 1 << 17,
            AllowImplicitTuples = 1 << 18,
        }

        /// <summary>
        /// Specifies optional behaviors associated with this model
        /// </summary>
        public override TypeModelOptions Options => (TypeModelOptions)(_options & RuntimeTypeModelOptions.TypeModelMask);

        private bool GetOption(RuntimeTypeModelOptions option) => (_options & option) != 0;

        private void SetOption(RuntimeTypeModelOptions option, bool value)
        {
            if (value) _options |= option;
            else _options &= ~option;
        }

        internal CompilerContextScope Scope { get; } = CompilerContextScope.CreateInProcess();

        /// <summary>
        /// Global default that
        /// enables/disables automatic tag generation based on the existing name / order
        /// of the defined members. See <seealso cref="ProtoContractAttribute.InferTagFromName"/>
        /// for usage and <b>important warning</b> / explanation.
        /// You must set the global default before attempting to serialize/deserialize any
        /// impacted type.
        /// </summary>
        public bool InferTagFromNameDefault
        {
            get { return GetOption(RuntimeTypeModelOptions.InferTagFromNameDefault); }
            set { SetOption(RuntimeTypeModelOptions.InferTagFromNameDefault, value); }
        }

        /// <summary>
        /// Global default that determines whether types are considered serializable
        /// if they have [DataContract] / [XmlType]. With this enabled, <b>ONLY</b>
        /// types marked as [ProtoContract] are added automatically.
        /// </summary>
        public bool AutoAddProtoContractTypesOnly
        {
            get { return GetOption(RuntimeTypeModelOptions.AutoAddProtoContractTypesOnly); }
            set { SetOption(RuntimeTypeModelOptions.AutoAddProtoContractTypesOnly, value); }
        }

        /// <summary>
        /// <para>
        /// Global switch that enables or disables the implicit
        /// handling of "zero defaults"; meanning: if no other default is specified,
        /// it assumes bools always default to false, integers to zero, etc.
        /// </para>
        /// <para>
        /// If this is disabled, no such assumptions are made and only *explicit*
        /// default values are processed. This is enabled by default to 
        /// preserve similar logic to v1.
        /// </para>
        /// </summary>
        public bool UseImplicitZeroDefaults
        {
            get { return GetOption(RuntimeTypeModelOptions.UseImplicitZeroDefaults); }
            set
            {
                if (!value && GetOption(RuntimeTypeModelOptions.IsDefaultModel))
                    ThrowDefaultUseImplicitZeroDefaults();
                SetOption(RuntimeTypeModelOptions.UseImplicitZeroDefaults, value);
            }
        }

        /// <summary>
        /// Global switch that determines whether types with a <c>.ToString()</c> and a <c>Parse(string)</c>
        /// should be serialized as strings.
        /// </summary>
        public bool AllowParseableTypes
        {
            get { return GetOption(RuntimeTypeModelOptions.AllowParseableTypes); }
            set { SetOption(RuntimeTypeModelOptions.AllowParseableTypes, value); }
        }

        /// <summary>
        /// Global switch that determines whether DateTime serialization should include the <c>Kind</c> of the date/time.
        /// </summary>
        public bool IncludeDateTimeKind
        {
            get { return GetOption(RuntimeTypeModelOptions.IncludeDateTimeKind); }
            set { SetOption(RuntimeTypeModelOptions.IncludeDateTimeKind, value); }
        }

        /// <summary>
        /// Should zero-length packed arrays be serialized? (this is the v2 behavior, but skipping them is more efficient)
        /// </summary>
        public bool SkipZeroLengthPackedArrays
        {
            get { return GetOption(RuntimeTypeModelOptions.SkipZeroLengthPackedArrays); }
            set { SetOption(RuntimeTypeModelOptions.SkipZeroLengthPackedArrays, value); }
        }

        /// <summary>
        /// Should root-values allow "packed" encoding? (v2 does not support this)
        /// </summary>
        public bool AllowPackedEncodingAtRoot
        {
            get { return GetOption(RuntimeTypeModelOptions.AllowPackedEncodingAtRoot); }
            set { SetOption(RuntimeTypeModelOptions.AllowPackedEncodingAtRoot, value); }
        }

        /// <summary>
        /// Global switch that determines whether a single instance of the same string should be used during deserialization.
        /// </summary>
        /// <remarks>Note this does not use the global .NET string interner</remarks>
        public bool InternStrings
        {
            get { return GetOption(RuntimeTypeModelOptions.InternStrings); }
            set { SetOption(RuntimeTypeModelOptions.InternStrings, value); }
        }

        /// <summary>
        /// Global switch that enables or disables the implicit handling of tuple-like types.
        /// With this enabled, types that
        ///     - have a constructor with parameters that are equivalent to all its public members 
        ///     - has only read-only properties, or whose name includes Tuple
        /// are serialized even if they are not attributed.
        /// </summary>
        public bool AllowImplicitTuples
        {
            get { return GetOption(RuntimeTypeModelOptions.AllowImplicitTuples); }
            set { SetOption(RuntimeTypeModelOptions.AllowImplicitTuples, value); }
        }

        /// <summary>
        /// The default model, used to support ProtoBuf.Serializer
        /// </summary>
        public static RuntimeTypeModel Default
            => (DefaultModel as RuntimeTypeModel) ?? CreateDefaultModelInstance();

        /// <summary>
        /// Returns a sequence of the Type instances that can be
        /// processed by this model.
        /// </summary>
        public IEnumerable GetTypes() => types;

        /// <summary>
        /// Gets or sets the default <see cref="CompatibilityLevel"/> for this model.
        /// </summary>
        public CompatibilityLevel DefaultCompatibilityLevel
        {
            get => _defaultCompatibilityLevel;
            set
            {
                if (value != _defaultCompatibilityLevel)
                {
                    CompatibilityLevelAttribute.AssertValid(value);
                    ThrowIfFrozen();
                    if (GetOption(RuntimeTypeModelOptions.IsDefaultModel)) ThrowHelper.ThrowInvalidOperationException("The default compatibility level of the default model cannot be changed");
                    if (types.Any()) ThrowHelper.ThrowInvalidOperationException("The default compatibility level of cannot be changed once types have been added");
                    _defaultCompatibilityLevel = value;
                }
            }
        }

        private CompatibilityLevel _defaultCompatibilityLevel = CompatibilityLevel.Level200;


        /// <inheritdoc/>
        public override string GetSchema(SchemaGenerationOptions options)
        {
            if (options is null) throw new ArgumentNullException(nameof(options));
            var syntax = Serializer.GlobalOptions.Normalize(options.Syntax);
            var requiredTypes = new List<MetaType>();
            List<Type> inbuiltTypes = default;
            HashSet<Type> forceGenerationTypes = null;
            bool IsOutputForcedFor(Type type)
                => forceGenerationTypes?.Contains(type) ?? false;

            string package = options.Package, origin = options.Origin;
            var imports = new HashSet<string>(StringComparer.Ordinal);
            MetaType AddType(Type type, bool forceOutput, bool inferPackageAndOrigin)
            {
                if (forceOutput && type is object) (forceGenerationTypes ??= new HashSet<Type>()).Add(type);
                // generate just relative to the supplied type
                int index = FindOrAddAuto(type, false, false, false, DefaultCompatibilityLevel);
                if (index < 0) throw new ArgumentException($"The type specified is not a contract-type: '{type.NormalizeName()}'", nameof(type));

                // get the required types
                var mt = ((MetaType)types[index]).GetSurrogateOrBaseOrSelf(false);
                if (inferPackageAndOrigin)
                {
                    if (origin is null && !string.IsNullOrWhiteSpace(mt.Origin))
                    {
                        origin = mt.Origin;
                    }
                    string tmp;
                    if (package is null && !string.IsNullOrWhiteSpace(tmp = mt.GuessPackage()))
                    {
                        package = tmp;
                    }
                }
                AddMetaType(mt);
                return mt;
            }
            void AddMetaType(MetaType toAdd)
            {
                if (!string.IsNullOrWhiteSpace(toAdd.Origin) && toAdd.Origin != origin)
                {
                    imports.Add(toAdd.Origin);
                    return; // external type; not our problem!
                }

                if (!requiredTypes.Contains(toAdd))
                { // ^^^ note that the type might have been added as a descendent
                    requiredTypes.Add(toAdd);
                    CascadeDependents(requiredTypes, toAdd, imports, origin);
                }
            }

            if (!options.HasTypes && !options.HasServices)
            {
                // generate for the entire model
                foreach (MetaType meta in types)
                {
                    MetaType tmp = meta.GetSurrogateOrBaseOrSelf(false);
                    AddMetaType(tmp);
                }
            }
            else
            {
                if (options.HasTypes)
                {
                    foreach (var type in options.Types)
                    {
                        Type effectiveType = Nullable.GetUnderlyingType(type) ?? type;

                        var isInbuiltType = (ValueMember.TryGetCoreSerializer(this, DataFormat.Default, DefaultCompatibilityLevel, effectiveType, out var _, false, false, false, false) is object);
                        if (isInbuiltType)
                        {
                            (inbuiltTypes ??= new List<Type>()).Add(effectiveType);
                        }
                        else
                        {
                            bool isSingleInput = options.Types.Count == 1;
                            var mt = AddType(effectiveType, isSingleInput, isSingleInput);
                            
                        }
                    }
                }
                if (options.HasServices)
                {
                    foreach (var service in options.Services)
                    {
                        foreach (var method in service.Methods)
                        {
                            AddType(method.InputType, true, false);
                            AddType(method.OutputType, true, false);
                        }
                    }
                }
            }

            // use the provided type's namespace for the "package"
            StringBuilder headerBuilder = new StringBuilder();

            if (package is null)
            {
                IEnumerable<MetaType> typesForNamespace = (options.HasTypes || options.HasServices) ? requiredTypes : types.Cast<MetaType>();
                foreach (MetaType meta in typesForNamespace)
                {
                    if (TryGetRepeatedProvider(meta.Type) is object) continue;

                    string tmp = meta.Type.Namespace;
                    if (!string.IsNullOrEmpty(tmp))
                    {
                        if (tmp.StartsWith("System.")) continue;
                        if (package is null)
                        { // haven't seen any suggestions yet
                            package = tmp;
                        }
                        else if (package == tmp)
                        { // that's fine; a repeat of the one we already saw
                        }
                        else
                        { // something else; have confliucting suggestions; abort
                            package = null;
                            break;
                        }
                    }
                }
            }

            switch (syntax)
            {
                case ProtoSyntax.Proto2:
                    headerBuilder.AppendLine(@"syntax = ""proto2"";");
                    break;
                case ProtoSyntax.Proto3:
                    headerBuilder.AppendLine(@"syntax = ""proto3"";");
                    break;
                default:
#pragma warning disable CA2208 // param name - for clarity
                    throw new ArgumentOutOfRangeException(nameof(syntax));
#pragma warning restore CA2208 // param name - for clarity
            }

            if (!string.IsNullOrEmpty(package))
            {
                headerBuilder.Append("package ").Append(package).Append(';').AppendLine();
            }

            // check for validity
            foreach (var mt in requiredTypes)
            {
                _ = mt.Serializer; // force errors to happen if there's problems
            }

            StringBuilder bodyBuilder = new StringBuilder();
            // sort them by schema-name
            var callstack = new HashSet<Type>(); // for recursion detection
            MetaType[] metaTypesArr = new MetaType[requiredTypes.Count];
            requiredTypes.CopyTo(metaTypesArr, 0);
            Array.Sort(metaTypesArr, new MetaType.Comparer(callstack));

            // write the messages
            if (inbuiltTypes is object)
            {
                foreach (var type in inbuiltTypes)
                {
                    bodyBuilder.AppendLine().Append("message ").Append(type.Name).Append(" {");
                    MetaType.NewLine(bodyBuilder, 1).Append(syntax == ProtoSyntax.Proto2 ? "optional " : "").Append(GetSchemaTypeName(callstack, type, DataFormat.Default, DefaultCompatibilityLevel, false, false, imports))
                        .Append(" value = 1;").AppendLine().Append('}');
                }
            }
            for (int i = 0; i < metaTypesArr.Length; i++)
            {
                MetaType tmp = metaTypesArr[i];
                if (tmp.SerializerType is object)
                {
                    continue; // not our concern
                }
                if (!IsOutputForcedFor(tmp.Type) && TryGetRepeatedProvider(tmp.Type) is object) continue;
                tmp.WriteSchema(callstack, bodyBuilder, 0, imports, syntax, package, options.Flags);
            }

            // write the services
            if (options.HasServices)
            {
                foreach (var service in options.Services)
                {
                    MetaType.NewLine(bodyBuilder, 0).Append("service ").Append(service.Name).Append(" {");
                    foreach (var method in service.Methods)
                    {
                        var inputName = GetSchemaTypeName(callstack, method.InputType, DataFormat.Default, DefaultCompatibilityLevel, false, false, imports);
                        var replyName = GetSchemaTypeName(callstack, method.OutputType, DataFormat.Default, DefaultCompatibilityLevel, false, false, imports);
                        MetaType.NewLine(bodyBuilder, 1).Append("rpc ").Append(method.Name).Append(" (")
                            .Append(method.ClientStreaming ? "stream " : "")
                            .Append(inputName).Append(") returns (")
                            .Append(method.ServerStreaming ? "stream " : "")
                            .Append(replyName).Append(");");
                    }
                    MetaType.NewLine(bodyBuilder, 0).Append('}');
                }
            }

            foreach (var import in imports.OrderBy(_ => _))
            {
                if (!string.IsNullOrWhiteSpace(import))
                {
                    headerBuilder.Append("import \"").Append(import).Append("\";");
                    switch (import)
                    {
                        case CommonImports.Bcl:
                            headerBuilder.Append(" // schema for protobuf-net's handling of core .NET types");
                            break;
                        case CommonImports.Protogen:
                            headerBuilder.Append(" // custom protobuf-net options");
                            break;
                    }
                    headerBuilder.AppendLine();
                }
            }
            return headerBuilder.Append(bodyBuilder).AppendLine().ToString();
        }

        internal static class CommonImports
        {
            public const string
                Bcl = "protobuf-net/bcl.proto",
                Timestamp = "google/protobuf/timestamp.proto",
                Duration = "google/protobuf/duration.proto",
                Protogen = "protobuf-net/protogen.proto",
                Empty = "google/protobuf/empty.proto";
        }

        private void CascadeRepeated(List<MetaType> list, RepeatedSerializerStub provider, CompatibilityLevel ambient, DataFormat keyFormat, HashSet<string> imports, string origin)
        {
            if (provider.IsMap)
            {
                provider.ResolveMapTypes(out var key, out var value);
                TryGetCoreSerializer(list, key, ambient, imports, origin);
                TryGetCoreSerializer(list, value, ambient, imports, origin);

                if (!provider.IsValidProtobufMap(this, ambient, keyFormat)) // add the KVP
                    TryGetCoreSerializer(list, provider.ItemType, ambient, imports, origin);
            }
            else
            {
                TryGetCoreSerializer(list, provider.ItemType, ambient, imports, origin);
            }
        }
        private void CascadeDependents(List<MetaType> list, MetaType metaType, HashSet<string> imports, string origin)
        {
            MetaType tmp;
            var repeated = TryGetRepeatedProvider(metaType.Type);
            if (repeated is object)
            {
                CascadeRepeated(list, repeated, metaType.CompatibilityLevel, DataFormat.Default, imports, origin);
            }
            else
            {
                if (metaType.IsAutoTuple)
                {
                    if (MetaType.ResolveTupleConstructor(metaType.Type, out var mapping) is object)
                    {
                        for (int i = 0; i < mapping.Length; i++)
                        {
                            Type type = null;
                            if (mapping[i] is PropertyInfo propertyInfo) type = propertyInfo.PropertyType;
                            else if (mapping[i] is FieldInfo fieldInfo) type = fieldInfo.FieldType;
                            TryGetCoreSerializer(list, type, metaType.CompatibilityLevel, imports, origin);
                        }
                    }
                }
                else
                {
                    foreach (ValueMember member in metaType.Fields)
                    {
                        repeated = TryGetRepeatedProvider(member.MemberType);
                        if (repeated is object)
                        {
                            CascadeRepeated(list, repeated, member.CompatibilityLevel, member.MapKeyFormat, imports, origin);
                            if (repeated.IsMap && !member.IsMap) // include the KVP, then
                                TryGetCoreSerializer(list, repeated.ItemType, member.CompatibilityLevel, imports, origin);
                        }
                        else
                        {
                            TryGetCoreSerializer(list, member.MemberType, member.CompatibilityLevel, imports, origin);
                        }
                    }
                }
                foreach (var genericArgument in metaType.GetAllGenericArguments())
                {
                    repeated = TryGetRepeatedProvider(genericArgument);
                    if (repeated is object)
                    {
                        CascadeRepeated(list, repeated, metaType.CompatibilityLevel, DataFormat.Default, imports, origin);
                    }
                    else
                    {
                        TryGetCoreSerializer(list, genericArgument, metaType.CompatibilityLevel, imports, origin);
                    }
                }
                if (metaType.HasSubtypes)
                {
                    foreach (SubType subType in metaType.GetSubtypes())
                    {
                        tmp = subType.DerivedType.GetSurrogateOrSelf(); // note: exclude base-types!
                        if (!list.Contains(tmp))
                        {
                            list.Add(tmp);
                            CascadeDependents(list, tmp, imports, origin);
                        }
                    }
                }
                tmp = metaType.BaseType;
                if (tmp is object) tmp = tmp.GetSurrogateOrSelf(); // note: already walking base-types; exclude base
                if (tmp is object && !list.Contains(tmp))
                {
                    list.Add(tmp);
                    CascadeDependents(list, tmp, imports, origin);
                }
            }
        }

        private void TryGetCoreSerializer(List<MetaType> list, Type itemType, CompatibilityLevel ambient, HashSet<string> imports, string origin)
        {
            var coreSerializer = ValueMember.TryGetCoreSerializer(this, DataFormat.Default, CompatibilityLevel.NotSpecified, itemType, out _, false, false, false, false);
            if (coreSerializer is object)
            {
                return;
            }
            int index = FindOrAddAuto(itemType, false, false, false, ambient);
            if (index < 0)
            {
                return;
            }

            var mt = (MetaType)types[index];
            if (mt.HasSurrogate)
            {
                coreSerializer = ValueMember.TryGetCoreSerializer(this, mt.surrogateDataFormat, mt.CompatibilityLevel, mt.surrogateType, out _, false, false, false, false);
                if (coreSerializer is object)
                {   // inbuilt basic surrogate
                    return;
                }
            }
            var temp = mt.GetSurrogateOrBaseOrSelf(false);
            if (!string.IsNullOrWhiteSpace(temp.Origin) && temp.Origin != origin)
            {
                imports.Add(temp.Origin);
                return; // external type; not our problem!
            }
            if (list.Contains(temp))
            {
                return;
            }
            // could perhaps also implement as a queue, but this should work OK for sane models
            list.Add(temp);
            CascadeDependents(list, temp, imports, origin);
        }

        internal RuntimeTypeModel(bool isDefault, string name)
        {
            AutoAddMissingTypes = true;
            UseImplicitZeroDefaults = true;
            AllowImplicitTuples = true;
            SetOption(RuntimeTypeModelOptions.IsDefaultModel, isDefault);
#if !DEBUG
            try
            {
                AutoCompile = EnableAutoCompile();
            }
            catch { } // this is all kinds of brittle on things like UWP
#endif
            if (!string.IsNullOrWhiteSpace(name)) _name = name;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool EnableAutoCompile()
        {
            try
            {
                var dm = new DynamicMethod("CheckCompilerAvailable", typeof(bool), new Type[] { typeof(int) });
                var il = dm.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldc_I4, 42);
                il.Emit(OpCodes.Ceq);
                il.Emit(OpCodes.Ret);
                var func = (Predicate<int>)dm.CreateDelegate(typeof(Predicate<int>));
                return func(42);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                return false;
            }
        }

        /// <summary>
        /// Obtains the MetaType associated with a given Type for the current model,
        /// allowing additional configuration.
        /// </summary>
        public MetaType this[Type type] { get { return (MetaType)types[FindOrAddAuto(type, true, false, false, DefaultCompatibilityLevel)]; } }

        internal MetaType FindWithAmbientCompatibility(Type type, CompatibilityLevel ambient)
        {
            var found = (MetaType)types[FindOrAddAuto(type, true, false, false, ambient)];
            if (found is object && found.IsAutoTuple && found.CompatibilityLevel != ambient)
            {
                throw new InvalidOperationException($"The tuple-like type {type.NormalizeName()} must use a single compatiblity level, but '{ambient}' and '{found.CompatibilityLevel}' are both observed; this usually means it is being used in different contexts in the same model.");
            }
            return found;
        }

        internal MetaType FindWithoutAdd(Type type)
        {
            // this list is thread-safe for reading
            type = DynamicStub.GetEffectiveType(type);
            foreach (MetaType metaType in types)
            {
                if (metaType.Type == type)
                {
                    if (metaType.Pending) WaitOnLock();
                    return metaType;
                }
            }
            return null;
        }

        private static readonly BasicList.MatchPredicate BasicTypeFinder = (value, ctx)
            => ((BasicType)value).Type == (Type)ctx;

        private static readonly BasicList.MatchPredicate MetaTypeFinder = (value, ctx)
            => ((MetaType)value).Type == (Type)ctx;

        private void WaitOnLock()
        {
            int opaqueToken = 0;
            try
            {
                TakeLock(ref opaqueToken);
            }
            finally
            {
                ReleaseLock(opaqueToken);
            }
        }

        private readonly BasicList types = new BasicList(), basicTypes = new BasicList();

        private sealed class BasicType
        {
            public Type Type { get; }

            public IRuntimeProtoSerializerNode Serializer { get; }

            public BasicType(Type type, IRuntimeProtoSerializerNode serializer)
            {
                Type = type;
                Serializer = serializer;
            }
        }

        internal IRuntimeProtoSerializerNode TryGetBasicTypeSerializer(Type type)
        {
            int idx = basicTypes.IndexOf(BasicTypeFinder, type);

            if (idx >= 0) return ((BasicType)basicTypes[idx]).Serializer;

            lock (basicTypes)
            { // don't need a full model lock for this
                // double-checked
                idx = basicTypes.IndexOf(BasicTypeFinder, type);
                if (idx >= 0) return ((BasicType)basicTypes[idx]).Serializer;

                MetaType.AttributeFamily family = MetaType.GetContractFamily(this, type, null);
                IRuntimeProtoSerializerNode ser = family == MetaType.AttributeFamily.None
                    ? ValueMember.TryGetCoreSerializer(this, DataFormat.Default, CompatibilityLevel.NotSpecified, type, out _, false, false, false, false)
                    : null;

                if (ser is object) basicTypes.Add(new BasicType(type, ser));
                return ser;
            }
        }

        internal int FindOrAddAuto(Type type, bool demand, bool addWithContractOnly, bool addEvenIfAutoDisabled, CompatibilityLevel ambient)
        {
            type = DynamicStub.GetEffectiveType(type);
            int key = types.IndexOf(MetaTypeFinder, type);
            MetaType metaType;

            // the fast happy path: meta-types we've already seen
            if (key >= 0)
            {
                metaType = (MetaType)types[key];
                if (metaType.Pending)
                {
                    WaitOnLock();
                }
                return key;
            }

            // the fast fail path: types that will never have a meta-type
            bool shouldAdd = AutoAddMissingTypes || addEvenIfAutoDisabled;

            if (!type.IsEnum && TryGetBasicTypeSerializer(type) is object)
            {
                if (shouldAdd && !addWithContractOnly) throw MetaType.InbuiltType(type);
                return -1; // this will never be a meta-type
            }

            // otherwise: we don't yet know

            if (key < 0)
            {
                int opaqueToken = 0;
                bool weAdded = false;
                try
                {
                    TakeLock(ref opaqueToken);
                    // try to recognise a few familiar patterns...
                    if ((metaType = RecogniseCommonTypes(type)) is null)
                    { // otherwise, check if it is a contract
                        MetaType.AttributeFamily family = MetaType.GetContractFamily(this, type, null);
                        if (family == MetaType.AttributeFamily.AutoTuple)
                        {
                            shouldAdd = addEvenIfAutoDisabled = true; // always add basic tuples, such as KeyValuePair
                        }

                        if (!shouldAdd || (
                            !type.IsEnum && addWithContractOnly && family == MetaType.AttributeFamily.None)
                            )
                        {
                            if (demand) ThrowUnexpectedType(type, this);
                            return key;
                        }

                        metaType = Create(type);
                    }

                    metaType.Pending = true;

                    // double-checked
                    int winner = types.IndexOf(MetaTypeFinder, type);
                    if (winner < 0)
                    {
                        ThrowIfFrozen();
                        key = types.Add(metaType);
                        weAdded = true;
                    }
                    else
                    {
                        key = winner;
                    }
                    if (weAdded)
                    {
                        metaType.ApplyDefaultBehaviour(ambient);
                        metaType.Pending = false;
                    }
                }
                finally
                {
                    ReleaseLock(opaqueToken);
                }
            }
            return key;
        }

#pragma warning disable RCS1163, IDE0060, CA1822 // Unused parameter, static
        private MetaType RecogniseCommonTypes(Type type)
#pragma warning restore RCS1163, IDE0060, CA1822 // Unused parameter, static
        {
            //            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(System.Collections.Generic.KeyValuePair<,>))
            //            {
            //                MetaType mt = new MetaType(this, type);

            //                Type surrogate = typeof (KeyValuePairSurrogate<,>).MakeGenericType(type.GetGenericArguments());

            //                mt.SetSurrogate(surrogate);
            //                mt.IncludeSerializerMethod = false;
            //                mt.Freeze();

            //                MetaType surrogateMeta = (MetaType)types[FindOrAddAuto(surrogate, true, true, true)]; // this forcibly adds it if needed
            //                if(surrogateMeta.IncludeSerializerMethod)
            //                { // don't blindly set - it might be frozen
            //                    surrogateMeta.IncludeSerializerMethod = false;
            //                }
            //                surrogateMeta.Freeze();
            //                return mt;
            //            }
            return null;
        }
        private MetaType Create(Type type)
        {
            ThrowIfFrozen();
            return new MetaType(this, type, defaultFactory);
        }

        /// <summary>
        /// Like the non-generic Add(Type); for convenience
        /// </summary>
        public MetaType Add<T>(bool applyDefaultBehaviour = true, CompatibilityLevel compatibilityLevel = default)
            => Add(typeof(T), applyDefaultBehaviour, compatibilityLevel);

        /// <summary>
        /// Adds support for an additional type in this model, optionally
        /// applying inbuilt patterns. If the type is already known to the
        /// model, the existing type is returned **without** applying
        /// any additional behaviour.
        /// </summary>
        /// <remarks>Inbuilt patterns include:
        /// [ProtoContract]/[ProtoMember(n)]
        /// [DataContract]/[DataMember(Order=n)]
        /// [XmlType]/[XmlElement(Order=n)]
        /// [On{Des|S}erializ{ing|ed}]
        /// ShouldSerialize*/*Specified
        /// </remarks>
        /// <param name="type">The type to be supported</param>
        /// <param name="applyDefaultBehaviour">Whether to apply the inbuilt configuration patterns (via attributes etc), or
        /// just add the type with no additional configuration (the type must then be manually configured).</param>
        /// <returns>The MetaType representing this type, allowing
        /// further configuration.</returns>
        public MetaType Add(Type type, bool applyDefaultBehaviour)
            => Add(type, applyDefaultBehaviour, default);

        /// <summary>
        /// Adds support for an additional type in this model, optionally
        /// applying inbuilt patterns. If the type is already known to the
        /// model, the existing type is returned **without** applying
        /// any additional behaviour.
        /// </summary>
        /// <remarks>Inbuilt patterns include:
        /// [ProtoContract]/[ProtoMember(n)]
        /// [DataContract]/[DataMember(Order=n)]
        /// [XmlType]/[XmlElement(Order=n)]
        /// [On{Des|S}erializ{ing|ed}]
        /// ShouldSerialize*/*Specified
        /// </remarks>
        /// <param name="type">The type to be supported</param>
        /// <param name="applyDefaultBehaviour">Whether to apply the inbuilt configuration patterns (via attributes etc), or
        /// just add the type with no additional configuration (the type must then be manually configured).</param>
        /// <param name="compatibilityLevel">The <see cref="CompatibilityLevel"/> to assume for this type; this should usually be omitted</param>
        /// <returns>The MetaType representing this type, allowing
        /// further configuration.</returns>
        public MetaType Add(Type type, bool applyDefaultBehaviour = true, CompatibilityLevel compatibilityLevel = default)
        {
            if (type is null) throw new ArgumentNullException(nameof(type));
            if (type == typeof(object))
                throw new ArgumentException("You cannot reconfigure " + type.FullName);
            type = DynamicStub.GetEffectiveType(type);
            MetaType newType = FindWithoutAdd(type);
            if (newType is object)
            {
                if (compatibilityLevel != default)
                {
                    newType.Assert(compatibilityLevel);
                }
                return newType; // return existing
            }
            int opaqueToken = 0;

            try
            {
                newType = RecogniseCommonTypes(type);
                if (newType is object)
                {
                    if (!applyDefaultBehaviour)
                    {
                        throw new ArgumentException(
                            "Default behaviour must be observed for certain types with special handling; " + type.FullName,
                            nameof(applyDefaultBehaviour));
                    }
                    // we should assume that type is fully configured, though; no need to re-run:
                    applyDefaultBehaviour = false;
                }
                if (newType is null) newType = Create(type);
                newType.CompatibilityLevel = compatibilityLevel; // usually this will be setting it to "unspecified", which is fine
                newType.Pending = true;
                TakeLock(ref opaqueToken);
                // double checked
                if (FindWithoutAdd(type) is object) throw new ArgumentException("Duplicate type", nameof(type));
                ThrowIfFrozen();
                types.Add(newType);
                if (applyDefaultBehaviour) { newType.ApplyDefaultBehaviour(default); }
                newType.Pending = false;
            }
            finally
            {
                ReleaseLock(opaqueToken);
            }

            return newType;
        }

        /// <summary>
        /// Raised before a type is auto-configured; this allows the auto-configuration to be electively suppressed
        /// </summary>
        /// <remarks>This callback should be fast and not involve complex external calls, as it may block the model</remarks>
        public event EventHandler<TypeAddedEventArgs> BeforeApplyDefaultBehaviour;

        /// <summary>
        /// Raised after a type is auto-configured; this allows additional external customizations
        /// </summary>
        /// <remarks>This callback should be fast and not involve complex external calls, as it may block the model</remarks>
        public event EventHandler<TypeAddedEventArgs> AfterApplyDefaultBehaviour;

        internal static void OnBeforeApplyDefaultBehaviour(MetaType metaType, ref TypeAddedEventArgs args)
            => OnApplyDefaultBehaviour(metaType?.Model?.BeforeApplyDefaultBehaviour, metaType, ref args);

        internal static void OnAfterApplyDefaultBehaviour(MetaType metaType, ref TypeAddedEventArgs args)
            => OnApplyDefaultBehaviour(metaType?.Model?.AfterApplyDefaultBehaviour, metaType, ref args);

        private static void OnApplyDefaultBehaviour(
            EventHandler<TypeAddedEventArgs> handler, MetaType metaType, ref TypeAddedEventArgs args)
        {
            if (handler is object)
            {
                if (args is null) args = new TypeAddedEventArgs(metaType);
                handler(metaType.Model, args);
            }
        }

        /// <summary>
        /// Should serializers be compiled on demand? It may be useful
        /// to disable this for debugging purposes.
        /// </summary>
        public bool AutoCompile
        {
            get { return GetOption(RuntimeTypeModelOptions.AutoCompile); }
            set { SetOption(RuntimeTypeModelOptions.AutoCompile, value); }
        }

        /// <summary>
        /// Should support for unexpected types be added automatically?
        /// If false, an exception is thrown when unexpected types
        /// are encountered.
        /// </summary>
        public bool AutoAddMissingTypes
        {
            get { return GetOption(RuntimeTypeModelOptions.AutoAddMissingTypes); }
            set
            {
                if (!value && GetOption(RuntimeTypeModelOptions.IsDefaultModel))
                    ThrowDefaultAutoAddMissingTypes();
                ThrowIfFrozen();
                SetOption(RuntimeTypeModelOptions.AutoAddMissingTypes, value);
            }
        }

        /// <summary>
        /// Verifies that the model is still open to changes; if not, an exception is thrown
        /// </summary>
        private void ThrowIfFrozen()
        {
            if (GetOption(RuntimeTypeModelOptions.Frozen)) throw new InvalidOperationException("The model cannot be changed once frozen");
        }

        /// <summary>
        /// Prevents further changes to this model
        /// </summary>
        public void Freeze()
        {
            if (GetOption(RuntimeTypeModelOptions.IsDefaultModel)) ThrowDefaultFrozen();
            SetOption(RuntimeTypeModelOptions.Frozen, true);
        }

        /// <summary>Resolve a service relative to T</summary>
        protected override ISerializer<T> GetSerializer<T>()
            => GetServices<T>(default) as ISerializer<T>;

        internal override ISerializer<T> GetSerializerCore<T>(CompatibilityLevel ambient)
            => GetServices<T>(ambient) as ISerializer<T>;

        /// <summary>Indicates whether a type is known to the model</summary>
        internal override bool IsKnownType<T>(CompatibilityLevel ambient) // the point of this override is to avoid loops
                                                                          // when trying to *build* a model; we don't actually need the service (which may not exist yet);
                                                                          // we just need to know whether we should *expect one*
            => _serviceCache[typeof(T)] is object || FindOrAddAuto(typeof(T), false, true, false, ambient) >= 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private object GetServices<T>(CompatibilityLevel ambient)
            => (_serviceCache[typeof(T)] ?? GetServicesSlow(typeof(T), ambient));


        private readonly Hashtable _serviceCache = new Hashtable();
        internal void ResetServiceCache(Type type)
        {
            if (type is object)
            {
                lock (_serviceCache)
                {
                    _serviceCache.Remove(type);
                }
            }
        }

        private object GetServicesSlow(Type type, CompatibilityLevel ambient)
        {
            if (type is null) return null; // GIGO
            object service;
            lock (_serviceCache)
            {   // once more, with feeling
                service = _serviceCache[type];
                if (service is object) return service;
            }
            service = GetServicesImpl(this, type, ambient);
            if (service is object)
            {
                try
                {
                    _ = this[type]; // if possible, make sure that we've registered it, so we export a proxy if needed
                }
                catch { }
                lock (_serviceCache)
                {
                    _serviceCache[type] = service;
                }
            }
            return service;

            static object GetServicesImpl(RuntimeTypeModel model, Type type, CompatibilityLevel ambient)
            {
                if (type.IsEnum) return EnumSerializers.GetSerializer(type);

                var nt = Nullable.GetUnderlyingType(type);
                if (nt is object)
                {
                    // rely on the fact that we always do double-duty with nullables
                    return model.GetServicesSlow(nt, ambient);
                }

                // rule out repeated (this has an internal cache etc)
                var repeated = model.TryGetRepeatedProvider(type); // this handles ignores, etc
                if (repeated is object) return repeated.Serializer;

                int typeIndex = model.FindOrAddAuto(type, false, true, false, ambient);
                if (typeIndex >= 0)
                {
                    var mt = (MetaType)model.types[typeIndex];
                    var serializer = mt.Serializer;
                    if (serializer is IExternalSerializer external)
                    {
                        return external.Service;
                    }
                    return serializer;
                }

                return null;
            }

        }

        /// <summary>
        /// See Object.ToString
        /// </summary>
        public override string ToString() => _name ?? base.ToString();

        // this is used by some unit-tests; do not remove
        internal Compiler.ProtoSerializer<TActual> GetSerializer<TActual>(IRuntimeProtoSerializerNode serializer, bool compiled)
        {
            if (serializer is null) throw new ArgumentNullException(nameof(serializer));

            if (compiled) return Compiler.CompilerContext.BuildSerializer<TActual>(Scope, serializer, this);

            return new Compiler.ProtoSerializer<TActual>(
                (ref ProtoWriter.State state, TActual val) => serializer.Write(ref state, val));
        }

        /// <summary>
        /// Compiles the serializers individually; this is *not* a full
        /// standalone compile, but can significantly boost performance
        /// while allowing additional types to be added.
        /// </summary>
        /// <remarks>An in-place compile can access non-public types / members</remarks>
        public void CompileInPlace()
        {
            foreach (MetaType type in types)
            {
                type.CompileInPlace();
            }
        }

        private void BuildAllSerializers()
        {
            // note that types.Count may increase during this operation, as some serializers
            // bring other types into play
            for (int i = 0; i < types.Count; i++)
            {
                // the primary purpose of this is to force the creation of the Serializer
                MetaType mt = (MetaType)types[i];

                if (GetServicesSlow(mt.Type, mt.CompatibilityLevel) is null) // respects enums, repeated, etc
                    throw new InvalidOperationException("No serializer available for " + mt.Type.NormalizeName());
            }
        }

        internal sealed class SerializerPair : IComparable
        {
            int IComparable.CompareTo(object obj)
            {
                if (obj is null) throw new ArgumentNullException(nameof(obj));
                SerializerPair other = (SerializerPair)obj;

                // we want to bunch all the items with the same base-type together, but we need the items with a
                // different base **first**.
                if (this.BaseKey == this.MetaKey)
                {
                    if (other.BaseKey == other.MetaKey)
                    { // neither is a subclass
                        return this.MetaKey.CompareTo(other.MetaKey);
                    }
                    else
                    { // "other" (only) is involved in inheritance; "other" should be first
                        return 1;
                    }
                }
                else
                {
                    if (other.BaseKey == other.MetaKey)
                    { // "this" (only) is involved in inheritance; "this" should be first
                        return -1;
                    }
                    else
                    { // both are involved in inheritance
                        int result = this.BaseKey.CompareTo(other.BaseKey);
                        if (result == 0) result = this.MetaKey.CompareTo(other.MetaKey);
                        return result;
                    }
                }
            }
            public readonly int MetaKey, BaseKey;
            public readonly MetaType Type;
            public readonly MethodBuilder Serialize, Deserialize;
            public readonly ILGenerator SerializeBody, DeserializeBody;
            public SerializerPair(int metaKey, int baseKey, MetaType type, MethodBuilder serialize, MethodBuilder deserialize,
                ILGenerator serializeBody, ILGenerator deserializeBody)
            {
                this.MetaKey = metaKey;
                this.BaseKey = baseKey;
                this.Serialize = serialize;
                this.Deserialize = deserialize;
                this.SerializeBody = serializeBody;
                this.DeserializeBody = deserializeBody;
                this.Type = type;
            }
        }

        internal static ILGenerator Override(TypeBuilder type, string name)
            => Override(type, name, out _);
        internal static ILGenerator Override(TypeBuilder type, string name, out Type[] genericArgs)
        {
            MethodInfo baseMethod;
            try
            {
                baseMethod = type.BaseType.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (baseMethod is null)
                    throw new ArgumentException($"Unable to resolve '{name}'");
            }
            catch (Exception ex)
            {
                throw new ArgumentException($"Unable to resolve '{name}': {ex.Message}", nameof(name), ex);
            }

            var parameters = baseMethod.GetParameters();
            var paramTypes = new Type[parameters.Length];
            for (int i = 0; i < paramTypes.Length; i++)
            {
                paramTypes[i] = parameters[i].ParameterType;
            }
            MethodBuilder newMethod = type.DefineMethod(baseMethod.Name,
                (baseMethod.Attributes & ~MethodAttributes.Abstract) | MethodAttributes.Final, baseMethod.CallingConvention, baseMethod.ReturnType, paramTypes);
            if (baseMethod.IsGenericMethodDefinition)
            {
                genericArgs = baseMethod.GetGenericArguments();
                string[] names = Array.ConvertAll(genericArgs, x => x.Name);
                newMethod.DefineGenericParameters(names);
            }
            else
                genericArgs = Type.EmptyTypes;
            for (int i = 0; i < parameters.Length; i++)
            {
                newMethod.DefineParameter(i + 1, parameters[i].Attributes, parameters[i].Name);
            }
            ILGenerator il = newMethod.GetILGenerator();
            type.DefineMethodOverride(newMethod, baseMethod);
            return il;
        }

        /// <summary>
        /// Represents configuration options for compiling a model to 
        /// a standalone assembly.
        /// </summary>
        public sealed class CompilerOptions
        {
            /// <summary>
            /// Import framework options from an existing type
            /// </summary>
            public void SetFrameworkOptions(MetaType from)
            {
                if (from is null) throw new ArgumentNullException(nameof(from));
                AttributeMap[] attribs = AttributeMap.Create(from.Type.Assembly);
                foreach (AttributeMap attrib in attribs)
                {
                    if (attrib.AttributeType.FullName == "System.Runtime.Versioning.TargetFrameworkAttribute")
                    {
                        if (attrib.TryGet("FrameworkName", out var tmp)) TargetFrameworkName = (string)tmp;
                        if (attrib.TryGet("FrameworkDisplayName", out tmp)) TargetFrameworkDisplayName = (string)tmp;
                        break;
                    }
                }
            }

            /// <summary>
            /// The TargetFrameworkAttribute FrameworkName value to burn into the generated assembly
            /// </summary>
            public string TargetFrameworkName { get; set; }

            /// <summary>
            /// The TargetFrameworkAttribute FrameworkDisplayName value to burn into the generated assembly
            /// </summary>
            public string TargetFrameworkDisplayName { get; set; }
            /// <summary>
            /// The name of the TypeModel class to create
            /// </summary>
            public string TypeName { get; set; }

#if PLAT_NO_EMITDLL
            internal const string NoPersistence = "Assembly persistence not supported on this runtime";
#endif
            /// <summary>
            /// The path for the new dll
            /// </summary>
#if PLAT_NO_EMITDLL
            [Obsolete(NoPersistence)]
#endif
            public string OutputPath { get; set; }
            /// <summary>
            /// The runtime version for the generated assembly
            /// </summary>
            public string ImageRuntimeVersion { get; set; }
            /// <summary>
            /// The runtime version for the generated assembly
            /// </summary>
            public int MetaDataVersion { get; set; }

            /// <summary>
            /// The acecssibility of the generated serializer
            /// </summary>
            public Accessibility Accessibility { get; set; }

            /// <summary>
            /// Implements a filter for use when generating models from assemblies
            /// </summary>
            public event Func<Type, bool> IncludeType;

            internal bool OnIncludeType(Type type)
            {
                var evt = IncludeType;
                return evt is null || evt(type);
            }
        }

        /// <summary>
        /// Type accessibility
        /// </summary>
        public enum Accessibility
        {
            /// <summary>
            /// Available to all callers
            /// </summary>
            Public,
            /// <summary>
            /// Available to all callers in the same assembly, or assemblies specified via [InternalsVisibleTo(...)]
            /// </summary>
            Internal
        }

#if !PLAT_NO_EMITDLL
        /// <summary>
        /// Fully compiles the current model into a static-compiled serialization dll
        /// (the serialization dll still requires protobuf-net for support services).
        /// </summary>
        /// <remarks>A full compilation is restricted to accessing public types / members</remarks>
        /// <param name="name">The name of the TypeModel class to create</param>
        /// <param name="path">The path for the new dll</param>
        /// <returns>An instance of the newly created compiled type-model</returns>
        public TypeModel Compile(string name, string path)
        {
            var options = new CompilerOptions()
            {
                TypeName = name,
#pragma warning disable CS0618
                OutputPath = path,
#pragma warning restore CS0618
            };
            return Compile(options);
        }
#endif
        /// <summary>
        /// Fully compiles the current model into a static-compiled serialization dll
        /// (the serialization dll still requires protobuf-net for support services).
        /// </summary>
        /// <remarks>A full compilation is restricted to accessing public types / members</remarks>
        /// <returns>An instance of the newly created compiled type-model</returns>
        public TypeModel Compile(CompilerOptions options = null)
        {
            options ??= new CompilerOptions();
            string typeName = options.TypeName;
#pragma warning disable 0618
            string path = options.OutputPath;
#pragma warning restore 0618
            BuildAllSerializers();
            Freeze();
            bool save = !string.IsNullOrEmpty(path);
            if (string.IsNullOrEmpty(typeName))
            {
#pragma warning disable CA2208 // param name - for clarity
                if (save) throw new ArgumentNullException("typeName");
#pragma warning restore CA2208 // param name - for clarity
                typeName = "CompiledModel_" + Guid.NewGuid().ToString();
            }

            string assemblyName, moduleName;
            if (path is null)
            {
                assemblyName = typeName;
                moduleName = assemblyName + ".dll";
            }
            else
            {
                assemblyName = new System.IO.FileInfo(System.IO.Path.GetFileNameWithoutExtension(path)).Name;
                moduleName = assemblyName + System.IO.Path.GetExtension(path);
            }

#if PLAT_NO_EMITDLL
            AssemblyName an = new AssemblyName { Name = assemblyName };
            AssemblyBuilder asm = AssemblyBuilder.DefineDynamicAssembly(an,
                AssemblyBuilderAccess.RunAndCollect);
            ModuleBuilder module = asm.DefineDynamicModule(moduleName);
#else
            AssemblyName an = new AssemblyName { Name = assemblyName };
            AssemblyBuilder asm = AppDomain.CurrentDomain.DefineDynamicAssembly(an,
                save ? AssemblyBuilderAccess.RunAndSave : AssemblyBuilderAccess.RunAndCollect);
            ModuleBuilder module = save ? asm.DefineDynamicModule(moduleName, path)
                                        : asm.DefineDynamicModule(moduleName);
#endif
            var scope = CompilerContextScope.CreateForModule(this, module, true, assemblyName);
            WriteAssemblyAttributes(options, assemblyName, asm);


            var serviceType = WriteBasicTypeModel("<Services>" + typeName, module, typeof(object), true);
            // note: the service could benefit from [DynamicallyAccessedMembers(DynamicAccess.Serializer)], but: that only exists
            // (on the public API) in net5+, and those platforms don't allow full dll emit (which is when the linker matters)
            WriteSerializers(scope, serviceType);
            WriteEnumsAndProxies(serviceType);

#if PLAT_NO_EMITDLL
            var finalServiceType = serviceType.CreateTypeInfo().AsType();
#else
            var finalServiceType = serviceType.CreateType();
#endif

            var modelType = WriteBasicTypeModel(typeName, module, typeof(TypeModel),
                options.Accessibility == Accessibility.Internal);

            WriteConstructorsAndOverrides(modelType, finalServiceType);

#if PLAT_NO_EMITDLL
            Type finalType = modelType.CreateTypeInfo().AsType();
#else
            Type finalType = modelType.CreateType();
#endif
            if (!string.IsNullOrEmpty(path))
            {
#if PLAT_NO_EMITDLL
                throw new NotSupportedException(CompilerOptions.NoPersistence);
#else
                try
                {
                    asm.Save(path);
                }
                catch (IOException ex)
                {
                    // advertise the file info
                    throw new IOException(path + ", " + ex.Message, ex);
                }
                Debug.WriteLine("Wrote dll:" + path);
#endif
            }
            return (TypeModel)Activator.CreateInstance(finalType, nonPublic: true);
        }

        private void WriteConstructorsAndOverrides(TypeBuilder type, Type serviceType)
        {
            ILGenerator il;
            var options = Options;
            if (options != TypeModel.DefaultOptions)
            {
                il = Override(type, "get_" + nameof(TypeModel.Options));
                CompilerContext.LoadValue(il, (int)options);
                il.Emit(OpCodes.Ret);
            }

            il = Override(type, nameof(TypeModel.GetSerializer), out var genericArgs);
            var genericT = genericArgs.Single();
            var method = typeof(SerializerCache).GetMethod(nameof(SerializerCache.Get)).MakeGenericMethod(serviceType, genericT);
            il.EmitCall(OpCodes.Call, method, null);
            il.Emit(OpCodes.Ret);

            type.DefineDefaultConstructor(MethodAttributes.Public);
        }

        private void WriteEnumsAndProxies(TypeBuilder type)
        {
            for (int index = 0; index < types.Count; index++)
            {
                var metaType = (MetaType)types[index];
                var runtimeType = metaType.Type;
                RepeatedSerializerStub repeated;
                if (runtimeType.IsEnum)
                {
                    var member = EnumSerializers.GetProvider(runtimeType);
                    AddProxy(type, runtimeType, member, true);
                }
                else if (ShouldEmitCustomSerializerProxy(metaType.SerializerType))
                {
                    AddProxy(type, runtimeType, metaType.SerializerType, false);
                }
                else if ((repeated = TryGetRepeatedProvider(runtimeType)) is object)
                {
                    AddProxy(type, runtimeType, repeated.Provider, false);
                }
            }
            static bool ShouldEmitCustomSerializerProxy(Type serializerType)
            {
                if (serializerType is null) return false; // nothing to do
                if (IsFullyPublic(serializerType)) return true; // fine, just do it

                // so: non-public; don't emit for anything inbuilt
                return serializerType.Assembly != typeof(PrimaryTypeProvider).Assembly;
            }
        }

        internal static MemberInfo GetUnderlyingProvider(MemberInfo provider, Type forType)
        {
            switch (provider)
            {   // properties are really a special-case of methods, via the getter
                case PropertyInfo property:
                    provider = property.GetGetMethod(true);
                    break;
                // types are really a short-hand for the singleton API
                case Type type when type.IsClass && !type.IsAbstract && type.GetConstructor(Type.EmptyTypes) is object:
                    provider = typeof(SerializerCache).GetMethod(nameof(SerializerCache.Get), BindingFlags.Public | BindingFlags.Static)
                        .MakeGenericMethod(type, forType);
                    break;
            }
            return provider;
        }

        internal static void EmitProvider(MemberInfo provider, ILGenerator il)
        {
            // after GetUnderlyingProvider, all we *actually* need to implement is fields and methods
            switch (provider)
            {
                case FieldInfo field when field.IsStatic:
                    il.Emit(OpCodes.Ldsfld, field);
                    break;
                case MethodInfo method when method.IsStatic:
                    il.EmitCall(OpCodes.Call, method, null);
                    break;
                default:
                    ThrowHelper.ThrowInvalidOperationException($"Invalid provider: {provider}");
                    break;
            }
        }

        internal RepeatedSerializerStub TryGetRepeatedProvider(Type type, CompatibilityLevel ambient = default)
        {
            if (type is null) return null;
            var repeated = RepeatedSerializers.TryGetRepeatedProvider(type);
            // but take it back if it is explicitly excluded
            if (repeated is object)
            { // looks like a list, but double check for IgnoreListHandling
                int idx = this.FindOrAddAuto(type, false, true, false, ambient);
                if (idx >= 0 && ((MetaType)types[idx]).IgnoreListHandling)
                {
                    return null;
                }
            }
            return repeated;
        }

        private static void AddProxy(TypeBuilder building, Type proxying, MemberInfo provider, bool includeNullable)
        {
            provider = GetUnderlyingProvider(provider, proxying);
            if (provider is object)
            {
                var iType = typeof(ISerializerProxy<>).MakeGenericType(proxying);
                building.AddInterfaceImplementation(iType);
                var il = CompilerContextScope.Implement(building, iType, "get_" + nameof(ISerializerProxy<string>.Serializer));
                EmitProvider(provider, il);
                il.Emit(OpCodes.Ret);

                if (includeNullable)
                {
                    iType = typeof(ISerializerProxy<>).MakeGenericType(typeof(Nullable<>).MakeGenericType(proxying));
                    building.AddInterfaceImplementation(iType);
                    il = CompilerContextScope.Implement(building, iType, "get_" + nameof(ISerializerProxy<string>.Serializer));
                    EmitProvider(provider, il);
                    il.Emit(OpCodes.Ret);
                }
            }
        }

        private void WriteSerializers(CompilerContextScope scope, TypeBuilder type)
        {
            // there are only a few permutations of "features" you want; share them between like-minded types
            var featuresLookup = new Dictionary<SerializerFeatures, MethodInfo>();

            MethodInfo GetFeaturesMethod(SerializerFeatures features)
            {
                if (!featuresLookup.TryGetValue(features, out var method))
                {
                    var name = nameof(ISerializer<int>.Features) + "_" + ((int)features).ToString(CultureInfo.InvariantCulture);
                    var newMethod = type.DefineMethod(name, MethodAttributes.Private | MethodAttributes.Virtual,
                        typeof(SerializerFeatures), Type.EmptyTypes);
                    ILGenerator il = newMethod.GetILGenerator();
                    CompilerContext.LoadValue(il, (int)features);
                    il.Emit(OpCodes.Ret);
                    method = featuresLookup[features] = newMethod;
                }
                return method;
            }

            for (int index = 0; index < types.Count; index++)
            {
                var metaType = (MetaType)types[index];
                var serializer = metaType.Serializer;
                var runtimeType = metaType.Type;

                metaType.Validate();
                if (runtimeType.IsEnum || metaType.SerializerType is object || TryGetRepeatedProvider(metaType.Type) is object)
                {   // we don't implement these
                    continue;
                }
                if (!IsFullyPublic(runtimeType, out var problem))
                {
                    ThrowHelper.ThrowInvalidOperationException("Non-public type cannot be used with full dll compilation: " + problem.NormalizeName());
                }

                Type inheritanceRoot = metaType.GetInheritanceRoot();

                // we always emit the serializer API
                var serType = typeof(ISerializer<>).MakeGenericType(runtimeType);
                type.AddInterfaceImplementation(serType);

                var il = CompilerContextScope.Implement(type, serType, nameof(ISerializer<string>.Read));
                using (var ctx = new CompilerContext(scope, il, false, CompilerContext.SignatureType.ReaderScope_Input, this, runtimeType, nameof(ISerializer<string>.Read)))
                {
                    if (serializer.HasInheritance)
                    {
                        serializer.EmitReadRoot(ctx, ctx.InputValue);
                    }
                    else
                    {
                        serializer.EmitRead(ctx, ctx.InputValue);
                        ctx.LoadValue(ctx.InputValue);
                    }
                    ctx.Return();
                }

                il = CompilerContextScope.Implement(type, serType, nameof(ISerializer<string>.Write));
                using (var ctx = new CompilerContext(scope, il, false, CompilerContext.SignatureType.WriterScope_Input, this, runtimeType, nameof(ISerializer<string>.Write)))
                {
                    if (serializer.HasInheritance) serializer.EmitWriteRoot(ctx, ctx.InputValue);
                    else serializer.EmitWrite(ctx, ctx.InputValue);
                    ctx.Return();
                }

                var featuresGetter = serType.GetProperty(nameof(ISerializer<string>.Features)).GetGetMethod();
                type.DefineMethodOverride(GetFeaturesMethod(serializer.Features), featuresGetter);

                // and we emit the sub-type serializer whenever inheritance is involved
                if (serializer.HasInheritance)
                {
                    serType = typeof(ISubTypeSerializer<>).MakeGenericType(runtimeType);
                    type.AddInterfaceImplementation(serType);

                    il = CompilerContextScope.Implement(type, serType, nameof(ISubTypeSerializer<string>.WriteSubType));
                    using (var ctx = new CompilerContext(scope, il, false, CompilerContext.SignatureType.WriterScope_Input, this,
                         runtimeType, nameof(ISubTypeSerializer<string>.WriteSubType)))
                    {
                        serializer.EmitWrite(ctx, ctx.InputValue);
                        ctx.Return();
                    }

                    il = CompilerContextScope.Implement(type, serType, nameof(ISubTypeSerializer<string>.ReadSubType));
                    using (var ctx = new CompilerContext(scope, il, false, CompilerContext.SignatureType.ReaderScope_Input, this,
                        typeof(SubTypeState<>).MakeGenericType(runtimeType),
                        nameof(ISubTypeSerializer<string>.ReadSubType)))
                    {
                        serializer.EmitRead(ctx, ctx.InputValue);
                        // note that EmitRead will unwrap the T for us on the stack
                        ctx.Return();
                    }
                }

                // if we're constructor skipping, provide a factory for that
                if (serializer.ShouldEmitCreateInstance)
                {
                    serType = typeof(IFactory<>).MakeGenericType(runtimeType);
                    type.AddInterfaceImplementation(serType);

                    il = CompilerContextScope.Implement(type, serType, nameof(IFactory<string>.Create));
                    using var ctx = new CompilerContext(scope, il, false, CompilerContext.SignatureType.Context, this,
                         typeof(ISerializationContext), nameof(IFactory<string>.Create));
                    serializer.EmitCreateInstance(ctx, false);
                    ctx.Return();
                }
            }
        }

        private static TypeBuilder WriteBasicTypeModel(string typeName, ModuleBuilder module,
            Type baseType, bool @internal)
        {
            TypeAttributes typeAttributes = (baseType.Attributes & ~(TypeAttributes.Abstract | TypeAttributes.Serializable)) | TypeAttributes.Sealed;
            if (@internal) typeAttributes &= ~TypeAttributes.Public;

            return module.DefineType(typeName, typeAttributes, baseType);
        }

        private void WriteAssemblyAttributes(CompilerOptions options, string assemblyName, AssemblyBuilder asm)
        {
            if (!string.IsNullOrEmpty(options.TargetFrameworkName))
            {
                // get [TargetFramework] from mscorlib/equivalent and burn into the new assembly
                Type versionAttribType = null;
                try
                { // this is best-endeavours only
                    versionAttribType = TypeModel.ResolveKnownType("System.Runtime.Versioning.TargetFrameworkAttribute", typeof(string).Assembly);
                }
                catch { /* don't stress */ }
                if (versionAttribType is object)
                {
                    PropertyInfo[] props;
                    object[] propValues;
                    if (string.IsNullOrEmpty(options.TargetFrameworkDisplayName))
                    {
                        props = Array.Empty<PropertyInfo>();
                        propValues = Array.Empty<object>();
                    }
                    else
                    {
                        props = new PropertyInfo[1] { versionAttribType.GetProperty("FrameworkDisplayName") };
                        propValues = new object[1] { options.TargetFrameworkDisplayName };
                    }
                    CustomAttributeBuilder builder = new CustomAttributeBuilder(
                        versionAttribType.GetConstructor(new Type[] { typeof(string) }),
                        new object[] { options.TargetFrameworkName },
                        props,
                        propValues);
                    asm.SetCustomAttribute(builder);
                }
            }

            // copy assembly:InternalsVisibleTo
            Type internalsVisibleToAttribType = null;

            try
            {
                internalsVisibleToAttribType = typeof(System.Runtime.CompilerServices.InternalsVisibleToAttribute);
            }
            catch { /* best endeavors only */ }

            if (internalsVisibleToAttribType is object)
            {
                List<string> internalAssemblies = new List<string>();
                List<Assembly> consideredAssemblies = new List<Assembly>();
                foreach (MetaType metaType in types)
                {
                    Assembly assembly = metaType.Type.Assembly;
                    if (consideredAssemblies.IndexOf(assembly) >= 0) continue;
                    consideredAssemblies.Add(assembly);

                    AttributeMap[] assemblyAttribsMap = AttributeMap.Create(assembly);
                    for (int i = 0; i < assemblyAttribsMap.Length; i++)
                    {
                        if (assemblyAttribsMap[i].AttributeType != internalsVisibleToAttribType) continue;

                        assemblyAttribsMap[i].TryGet("AssemblyName", out var privelegedAssemblyObj);
                        string privelegedAssemblyName = privelegedAssemblyObj as string;
                        if (privelegedAssemblyName == assemblyName || string.IsNullOrEmpty(privelegedAssemblyName)) continue; // ignore

                        if (internalAssemblies.IndexOf(privelegedAssemblyName) >= 0) continue; // seen it before
                        internalAssemblies.Add(privelegedAssemblyName);

                        CustomAttributeBuilder builder = new CustomAttributeBuilder(
                            internalsVisibleToAttribType.GetConstructor(new Type[] { typeof(string) }),
                            new object[] { privelegedAssemblyName });
                        asm.SetCustomAttribute(builder);
                    }
                }
            }
        }

        // note that this is used by some of the unit tests
        internal bool IsPrepared(Type type)
        {
            MetaType meta = FindWithoutAdd(type);
            return meta is object && meta.IsPrepared();
        }

        private int metadataTimeoutMilliseconds = 5000;
        /// <summary>
        /// The amount of time to wait if there are concurrent metadata access operations
        /// </summary>
        public int MetadataTimeoutMilliseconds
        {
            get { return metadataTimeoutMilliseconds; }
            set
            {
                if (value <= 0) throw new ArgumentOutOfRangeException("MetadataTimeoutMilliseconds");
                metadataTimeoutMilliseconds = value;
            }
        }

#if DEBUG
        private int lockCount;
        /// <summary>
        /// Gets how many times a model lock was taken
        /// </summary>
        public int LockCount { get { return lockCount; } }
#endif
        internal void TakeLock(ref int opaqueToken)
        {
            const string message = "Timeout while inspecting metadata; this may indicate a deadlock. This can often be avoided by preparing necessary serializers during application initialization, rather than allowing multiple threads to perform the initial metadata inspection; please also see the LockContended event";
            opaqueToken = 0;
            if (Monitor.TryEnter(types, metadataTimeoutMilliseconds))
            {
                opaqueToken = GetContention(); // just fetch current value (starts at 1)
            }
            else
            {
                AddContention();

                throw new TimeoutException(message);
            }

#if DEBUG // note that here, through all code-paths: we have the lock
            lockCount++;
#endif
        }

        private int contentionCounter = 1;

        private int GetContention()
        {
            return Interlocked.CompareExchange(ref contentionCounter, 0, 0);
        }
        private void AddContention()
        {
            Interlocked.Increment(ref contentionCounter);
        }

        internal void ReleaseLock(int opaqueToken)
        {
            if (opaqueToken != 0)
            {
                Monitor.Exit(types);
                if (opaqueToken != GetContention()) // contention-count changes since we looked!
                {
                    LockContentedEventHandler handler = LockContended;
                    if (handler is object)
                    {
                        // not hugely elegant, but this is such a far-corner-case that it doesn't need to be slick - I'll settle for cross-platform
                        string stackTrace;
                        try
                        {
                            throw new ProtoException();
                        }
                        catch (Exception ex)
                        {
                            stackTrace = ex.StackTrace;
                        }

                        handler(this, new LockContentedEventArgs(stackTrace));
                    }
                }
            }
        }

#pragma warning disable RCS1159 // Use EventHandler<T>.
        /// <summary>
        /// If a lock-contention is detected, this event signals the *owner* of the lock responsible for the blockage, indicating
        /// what caused the problem; this is only raised if the lock-owning code successfully completes.
        /// </summary>
        public event LockContentedEventHandler LockContended;
#pragma warning restore RCS1159 // Use EventHandler<T>.

        internal string GetSchemaTypeName(HashSet<Type> callstack, Type effectiveType, DataFormat dataFormat, CompatibilityLevel compatibilityLevel, bool asReference, bool dynamicType, HashSet<string> imports)
            => GetSchemaTypeName(callstack, effectiveType, dataFormat, compatibilityLevel, asReference, dynamicType, imports, out _);

        static bool IsWellKnownType(Type type, out string name, HashSet<string> imports)
        {
            if (type == typeof(byte[]))
            {
                name = "bytes";
                return true;
            }
            else if (type == typeof(Timestamp))
            {
                imports.Add(CommonImports.Timestamp);
                name = ".google.protobuf.Timestamp";
                return true;
            }
            else if (type == typeof(Duration))
            {
                imports.Add(CommonImports.Duration);
                name = ".google.protobuf.Duration";
                return true;
            }
            else if (type == typeof(Empty))
            {
                imports.Add(CommonImports.Empty);
                name = ".google.protobuf.Empty";
                return true;
            }
            name = default;
            return false;
        }
        internal string GetSchemaTypeName(HashSet<Type> callstack, Type effectiveType, DataFormat dataFormat, CompatibilityLevel compatibilityLevel, bool asReference, bool dynamicType, HashSet<string> imports, out string altName)
        {
            altName = null;
            compatibilityLevel = ValueMember.GetEffectiveCompatibilityLevel(compatibilityLevel, dataFormat);
            effectiveType = DynamicStub.GetEffectiveType(effectiveType);

            if (IsWellKnownType(effectiveType, out var wellKnownName, imports))
            {
                return wellKnownName;
            }

            IRuntimeProtoSerializerNode ser = ValueMember.TryGetCoreSerializer(this, dataFormat, compatibilityLevel, effectiveType, out _, false, false, false, false);
            if (ser is null)
            {   // model type
                if (asReference || dynamicType)
                {
                    imports.Add(CommonImports.Bcl);
                    return ".bcl.NetObjectProxy";
                }

                var mt = this[effectiveType];
                if (mt.HasSurrogate && ValueMember.TryGetCoreSerializer(this, mt.surrogateDataFormat, mt.CompatibilityLevel, mt.surrogateType, out _, false, false, false ,false) is object)
                {   // inbuilt basic surrogate
                    return GetSchemaTypeName(callstack, mt.surrogateType, mt.surrogateDataFormat, mt.CompatibilityLevel, false, false, imports);
                }
                var actualMeta = mt.GetSurrogateOrBaseOrSelf(true);
                if (IsWellKnownType(actualMeta.Type, out wellKnownName, imports))
                {
                    return wellKnownName;
                }
                var actual = actualMeta.GetSchemaTypeName(callstack);

                if (mt.Type.IsEnum && !mt.IsValidEnum())
                {
                    altName = actual;
                    actual = GetSchemaTypeName(callstack, Enum.GetUnderlyingType(mt.Type), dataFormat, CompatibilityLevel.NotSpecified, asReference, dynamicType, imports);
                }
                return actual;
            }
            else
            {
                if (ser is ParseableSerializer)
                {
                    if (asReference) imports.Add(CommonImports.Bcl);
                    return asReference ? ".bcl.NetObjectProxy" : "string";
                }

                switch (Helpers.GetTypeCode(effectiveType))
                {
                    case ProtoTypeCode.Boolean: return "bool";
                    case ProtoTypeCode.Single: return "float";
                    case ProtoTypeCode.Double: return "double";
                    case ProtoTypeCode.String:
                        if (asReference) imports.Add(CommonImports.Bcl);
                        return asReference ? ".bcl.NetObjectProxy" : "string";
                    case ProtoTypeCode.Byte:
                    case ProtoTypeCode.Char:
                    case ProtoTypeCode.UInt16:
                    case ProtoTypeCode.UInt32:
                        return dataFormat switch
                        {
                            DataFormat.FixedSize => "fixed32",
                            _ => "uint32",
                        };
                    case ProtoTypeCode.SByte:
                    case ProtoTypeCode.Int16:
                    case ProtoTypeCode.Int32:
                        return dataFormat switch
                        {
                            DataFormat.ZigZag => "sint32",
                            DataFormat.FixedSize => "sfixed32",
                            _ => "int32",
                        };
                    case ProtoTypeCode.UInt64:
                        return dataFormat switch
                        {
                            DataFormat.FixedSize => "fixed64",
                            _ => "uint64",
                        };
                    case ProtoTypeCode.Int64:
                        return dataFormat switch
                        {
                            DataFormat.ZigZag => "sint64",
                            DataFormat.FixedSize => "sfixed64",
                            _ => "int64",
                        };
                    case ProtoTypeCode.DateTime:
                        switch (dataFormat)
                        {
                            case DataFormat.FixedSize: return "sint64";
                            default:
                                if (compatibilityLevel >= CompatibilityLevel.Level240)
                                {
                                    imports.Add(CommonImports.Timestamp);
                                    return ".google.protobuf.Timestamp";
                                }
                                else
                                {
                                    imports.Add(CommonImports.Bcl);
                                    return ".bcl.DateTime";
                                }
                        }
                    case ProtoTypeCode.TimeSpan:
                        switch (dataFormat)
                        {
                            case DataFormat.FixedSize: return "sint64";
                            default:
                                if (compatibilityLevel >= CompatibilityLevel.Level240)
                                {
                                    imports.Add(CommonImports.Duration);
                                    return ".google.protobuf.Duration";
                                }
                                else
                                {
                                    imports.Add(CommonImports.Bcl);
                                    return ".bcl.TimeSpan";
                                }
                        }
                    case ProtoTypeCode.Decimal:
                        if (compatibilityLevel < CompatibilityLevel.Level300)
                        {
                            imports.Add(CommonImports.Bcl);
                            return ".bcl.Decimal";
                        }
                        return "string";
                    case ProtoTypeCode.Guid:
                        if (compatibilityLevel < CompatibilityLevel.Level300)
                        {
                            imports.Add(CommonImports.Bcl);
                            return ".bcl.Guid";
                        }
                        return dataFormat == DataFormat.FixedSize ? "bytes" : "string";
                    case ProtoTypeCode.Type: return "string";
                    default: throw new NotSupportedException("No .proto map found for: " + effectiveType.FullName);
                }
            }
        }

        /// <summary>
        /// Designate a factory-method to use to create instances of any type; note that this only affect types seen by the serializer *after* setting the factory.
        /// </summary>
        public void SetDefaultFactory(MethodInfo methodInfo)
        {
            VerifyFactory(methodInfo, null);
            defaultFactory = methodInfo;
        }
        private MethodInfo defaultFactory;

        internal static void VerifyFactory(MethodInfo factory, Type type)
        {
            if (factory is object)
            {
                if (type is object && type.IsValueType) throw new InvalidOperationException();
                if (!factory.IsStatic) throw new ArgumentException("A factory-method must be static", nameof(factory));
                if (type is object && factory.ReturnType != type && factory.ReturnType != typeof(object)) throw new ArgumentException("The factory-method must return object" + (type is null ? "" : (" or " + type.FullName)), nameof(factory));

                if (!CallbackSet.CheckCallbackParameters(factory)) throw new ArgumentException("Invalid factory signature in " + factory.DeclaringType.FullName + "." + factory.Name, nameof(factory));
            }
        }

        /// <summary>
        /// Creates a new runtime model, to which the caller
        /// can add support for a range of types. A model
        /// can be used "as is", or can be compiled for
        /// optimal performance.
        /// </summary>
        /// <param name="name">The logical name of this model</param>
        public static RuntimeTypeModel Create([CallerMemberName] string name = null)
        {
            return new RuntimeTypeModel(false, name);
        }

        private readonly string _name;

        internal static bool IsFullyPublic(Type type) => IsFullyPublic(type, out _);

        internal static bool IsFullyPublic(Type type, out Type cause)
        {
            Type originalType = type;
            while (type is object)
            {
                if (type.IsGenericType)
                {
                    var args = type.GetGenericArguments();
                    foreach (var arg in args)
                    {
                        if (!IsFullyPublic(arg))
                        {
                            cause = arg;
                            return false;
                        }
                    }
                }
                cause = type;
                if (type.IsNestedPublic)
                {
                    type = type.DeclaringType;
                }
                else
                {
                    return type.IsPublic;
                }
            }
            cause = originalType;
            return false;
        }

        /// <summary>
        /// Create a model that serializes all types from an
        /// assembly specified by type
        /// </summary>
        public static new TypeModel CreateForAssembly<T>()
            => AutoCompileTypeModel.CreateForAssembly<T>();

        /// <summary>
        /// Create a model that serializes all types from an
        /// assembly specified by type
        /// </summary>
        public static new TypeModel CreateForAssembly(Type type)
            => AutoCompileTypeModel.CreateForAssembly(type);

        /// <summary>
        /// Create a model that serializes all types from an assembly
        /// </summary>
        public static new TypeModel CreateForAssembly(Assembly assembly)
            => AutoCompileTypeModel.CreateForAssembly(assembly);

        /// <summary>
        /// Promotes this model instance to be the default model; the default model is used by <see cref="Serializer"/> and <see cref="Serializer.NonGeneric"/>.
        /// </summary>
        public void MakeDefault()
        {
            lock (s_ModelSyncLock)
            {
                var oldModel = DefaultModel as RuntimeTypeModel;

                if (ReferenceEquals(this, oldModel)) return; // we're already the default

                try
                {
                    // pre-emptively set the IsDefaultModel flag on the current model
                    SetOption(RuntimeTypeModelOptions.IsDefaultModel, true);

                    // check invariants (no race condition here, because of ^^^)
                    if (!UseImplicitZeroDefaults) ThrowDefaultUseImplicitZeroDefaults();
                    if (!AutoAddMissingTypes) ThrowDefaultAutoAddMissingTypes();
                    if (GetOption(RuntimeTypeModelOptions.Frozen)) ThrowDefaultFrozen();

                    // actually flip the reference
                    SetDefaultModel(this);
                }
                finally
                {
                    // clear the IsDefaultModel flag on anything that is not, in fact, the default
                    var currentDefault = DefaultModel;
                    if (!ReferenceEquals(this, currentDefault))
                        SetOption(RuntimeTypeModelOptions.IsDefaultModel, false);

                    if (oldModel is object && !ReferenceEquals(oldModel, currentDefault))
                        oldModel.SetOption(RuntimeTypeModelOptions.IsDefaultModel, false);
                }
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowDefaultAutoAddMissingTypes()
            => throw new InvalidOperationException("The default model must allow missing types");

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowDefaultUseImplicitZeroDefaults()
            => throw new InvalidOperationException("UseImplicitZeroDefaults cannot be disabled on the default model");

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowDefaultFrozen()
            => throw new InvalidOperationException("The default model cannot be frozen");

        private static readonly object s_ModelSyncLock = new object();

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static RuntimeTypeModel CreateDefaultModelInstance()
        {
            lock (s_ModelSyncLock)
            {
                if (DefaultModel is not RuntimeTypeModel model)
                {
                    model = new RuntimeTypeModel(true, "(default)");
                    SetDefaultModel(model);
                }
                return model;
            }
        }

        /// <summary>
        /// Treat all values of <typeparamref name="TUnderlying"/> (non-serializable)
        /// as though they were the surrogate <typeparamref name="TSurrogate"/> (serializable);
        /// if custom conversion operators are provided, they are used in place of implicit
        /// or explicit conversion operators.
        /// </summary>
        /// <typeparam name="TUnderlying">The non-serializable type to provide custom support for</typeparam>
        /// <typeparam name="TSurrogate">The serializable type that should be used instead</typeparam>
        /// <param name="underlyingToSurrogate">Custom conversion operation</param>
        /// <param name="surrogateToUnderlying">Custom conversion operation</param>
        /// <param name="dataFormat">The <see cref="DataFormat"/> to use</param>
        /// <param name="compatibilityLevel">The <see cref="CompatibilityLevel"/> to assume for this type</param>
        /// <returns>The original model (for chaining).</returns>
        public RuntimeTypeModel SetSurrogate<TUnderlying, TSurrogate>(
            Func<TUnderlying, TSurrogate> underlyingToSurrogate = null, Func<TSurrogate, TUnderlying> surrogateToUnderlying = null,
            DataFormat dataFormat = DataFormat.Default, CompatibilityLevel compatibilityLevel = CompatibilityLevel.NotSpecified)
        {
            Add<TUnderlying>(compatibilityLevel: compatibilityLevel).SetSurrogate(typeof(TSurrogate),
                GetMethod(underlyingToSurrogate, nameof(underlyingToSurrogate)),
                GetMethod(surrogateToUnderlying, nameof(surrogateToUnderlying)), dataFormat);
            return this;

            static MethodInfo GetMethod(Delegate value, string paramName)
            {
                if (value is null) return null;
                var handlers = value.GetInvocationList();
                if (handlers.Length != 1) ThrowHelper.ThrowArgumentException("A unicast delegate was expected.", paramName);
                value = handlers[0];
                if (value.Target is object target)
                {
                    var msg = "A delegate to a static method was expected.";
                    if (target.GetType().IsDefined(typeof(CompilerGeneratedAttribute)))
                    {
                        msg += $" The conversion '{target.GetType().NormalizeName()}.{value.Method.Name}' is compiler-generated (possibly a lambda); an explicit static method should be used instead.";
                    }
                    ThrowHelper.ThrowArgumentException(msg, paramName);
                }
                return value.Method;
            }
        }
    }

    /// <summary>
    /// Contains the stack-trace of the owning code when a lock-contention scenario is detected
    /// </summary>
    public sealed class LockContentedEventArgs : EventArgs
    {
        internal LockContentedEventArgs(string ownerStackTrace)
        {
            OwnerStackTrace = ownerStackTrace;
        }

        /// <summary>
        /// The stack-trace of the code that owned the lock when a lock-contention scenario occurred
        /// </summary>
        public string OwnerStackTrace { get; }
    }
    /// <summary>
    /// Event-type that is raised when a lock-contention scenario is detected
    /// </summary>
    public delegate void LockContentedEventHandler(object sender, LockContentedEventArgs args);
}