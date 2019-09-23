﻿using System;
using System.Collections;
using System.Text;
using System.Reflection;
using System.Reflection.Emit;

using ProtoBuf.Serializers;
using System.Threading;
using System.IO;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using ProtoBuf.Compiler;
using ProtoBuf.Internal;

namespace ProtoBuf.Meta
{
    /// <summary>
    /// Provides protobuf serialization support for a number of types that can be defined at runtime
    /// </summary>
    public sealed class RuntimeTypeModel : TypeModel
    {
        private ushort options;
        private const ushort
           OPTIONS_InferTagFromNameDefault = 1,
           OPTIONS_IsDefaultModel = 2,
           OPTIONS_Frozen = 4,
           OPTIONS_AutoAddMissingTypes = 8,
           OPTIONS_AutoCompile = 16,
           OPTIONS_UseImplicitZeroDefaults = 32,
           OPTIONS_AllowParseableTypes = 64,
           OPTIONS_AutoAddProtoContractTypesOnly = 128,
           OPTIONS_IncludeDateTimeKind = 256,
           OPTIONS_InternStrings = 512;

        private bool GetOption(ushort option)
        {
            return (options & option) == option;
        }

        private void SetOption(ushort option, bool value)
        {
            if (value) options |= option;
            else options &= (ushort)~option;
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
            get { return GetOption(OPTIONS_InferTagFromNameDefault); }
            set { SetOption(OPTIONS_InferTagFromNameDefault, value); }
        }

        /// <summary>
        /// Global default that determines whether types are considered serializable
        /// if they have [DataContract] / [XmlType]. With this enabled, <b>ONLY</b>
        /// types marked as [ProtoContract] are added automatically.
        /// </summary>
        public bool AutoAddProtoContractTypesOnly
        {
            get { return GetOption(OPTIONS_AutoAddProtoContractTypesOnly); }
            set { SetOption(OPTIONS_AutoAddProtoContractTypesOnly, value); }
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
            get { return GetOption(OPTIONS_UseImplicitZeroDefaults); }
            set
            {
                if (!value && GetOption(OPTIONS_IsDefaultModel))
                {
                    throw new InvalidOperationException("UseImplicitZeroDefaults cannot be disabled on the default model");
                }
                SetOption(OPTIONS_UseImplicitZeroDefaults, value);
            }
        }

        /// <summary>
        /// Global switch that determines whether types with a <c>.ToString()</c> and a <c>Parse(string)</c>
        /// should be serialized as strings.
        /// </summary>
        public bool AllowParseableTypes
        {
            get { return GetOption(OPTIONS_AllowParseableTypes); }
            set { SetOption(OPTIONS_AllowParseableTypes, value); }
        }

        /// <summary>
        /// Global switch that determines whether DateTime serialization should include the <c>Kind</c> of the date/time.
        /// </summary>
        public bool IncludeDateTimeKind
        {
            get { return GetOption(OPTIONS_IncludeDateTimeKind); }
            set { SetOption(OPTIONS_IncludeDateTimeKind, value); }
        }

        /// <summary>
        /// Global switch that determines whether a single instance of the same string should be used during deserialization.
        /// </summary>
        /// <remarks>Note this does not use the global .NET string interner</remarks>
        public new bool InternStrings
        {
            get { return GetOption(OPTIONS_InternStrings); }
            set { SetOption(OPTIONS_InternStrings, value); }
        }

        /// <summary>
        /// Global switch that determines whether a single instance of the same string should be used during deserialization.
        /// </summary>
        protected internal override bool GetInternStrings() => InternStrings;

        /// <summary>
        /// Should the <c>Kind</c> be included on date/time values?
        /// </summary>
        protected internal override bool SerializeDateTimeKind()
        {
            return GetOption(OPTIONS_IncludeDateTimeKind);
        }

        private sealed class Singleton
        {
            private Singleton() { }
            internal static readonly RuntimeTypeModel Value = new RuntimeTypeModel(true);
        }

        /// <summary>
        /// The default model, used to support ProtoBuf.Serializer
        /// </summary>
        public static RuntimeTypeModel Default => Singleton.Value;

        /// <summary>
        /// Returns a sequence of the Type instances that can be
        /// processed by this model.
        /// </summary>
        public IEnumerable GetTypes() => types;

        /// <summary>
        /// Suggest a .proto definition for the given type
        /// </summary>
        /// <param name="type">The type to generate a .proto definition for, or <c>null</c> to generate a .proto that represents the entire model</param>
        /// <returns>The .proto definition as a string</returns>
        /// <param name="syntax">The .proto syntax to use</param>
        public override string GetSchema(Type type, ProtoSyntax syntax)
        {
            BasicList requiredTypes = new BasicList();
            MetaType primaryType = null;
            bool isInbuiltType = false;
            if (type == null)
            { // generate for the entire model
                foreach (MetaType meta in types)
                {
                    MetaType tmp = meta.GetSurrogateOrBaseOrSelf(false);
                    if (!requiredTypes.Contains(tmp))
                    { // ^^^ note that the type might have been added as a descendent
                        requiredTypes.Add(tmp);
                        CascadeDependents(requiredTypes, tmp);
                    }
                }
            }
            else
            {
                Type tmp = Nullable.GetUnderlyingType(type);
                if (tmp != null) type = tmp;

                isInbuiltType = (ValueMember.TryGetCoreSerializer(this, DataFormat.Default, type, out var _, false, false, false, false) != null);
                if (!isInbuiltType)
                {
                    //Agenerate just relative to the supplied type
                    int index = FindOrAddAuto(type, false, false, false);
                    if (index < 0) throw new ArgumentException("The type specified is not a contract-type", nameof(type));

                    // get the required types
                    primaryType = ((MetaType)types[index]).GetSurrogateOrBaseOrSelf(false);
                    requiredTypes.Add(primaryType);
                    CascadeDependents(requiredTypes, primaryType);
                }
            }

            // use the provided type's namespace for the "package"
            StringBuilder headerBuilder = new StringBuilder();
            string package = null;

            if (!isInbuiltType)
            {
                IEnumerable typesForNamespace = primaryType == null ? types : requiredTypes;
                foreach (MetaType meta in typesForNamespace)
                {
                    if (meta.IsList) continue;
                    string tmp = meta.Type.Namespace;
                    if (!string.IsNullOrEmpty(tmp))
                    {
                        if (tmp.StartsWith("System.")) continue;
                        if (package == null)
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
                    throw new ArgumentOutOfRangeException(nameof(syntax));
            }

            if (!string.IsNullOrEmpty(package))
            {
                headerBuilder.Append("package ").Append(package).Append(';').AppendLine();
            }

            var imports = CommonImports.None;
            StringBuilder bodyBuilder = new StringBuilder();
            // sort them by schema-name
            MetaType[] metaTypesArr = new MetaType[requiredTypes.Count];
            requiredTypes.CopyTo(metaTypesArr, 0);
            Array.Sort(metaTypesArr, MetaType.Comparer.Default);

            // write the messages
            if (isInbuiltType)
            {
                bodyBuilder.AppendLine().Append("message ").Append(type.Name).Append(" {");
                MetaType.NewLine(bodyBuilder, 1).Append(syntax == ProtoSyntax.Proto2 ? "optional " : "").Append(GetSchemaTypeName(type, DataFormat.Default, false, false, ref imports))
                    .Append(" value = 1;").AppendLine().Append('}');
            }
            else
            {
                for (int i = 0; i < metaTypesArr.Length; i++)
                {
                    MetaType tmp = metaTypesArr[i];
                    if (tmp.IsList && tmp != primaryType) continue;
                    tmp.WriteSchema(bodyBuilder, 0, ref imports, syntax);
                }
            }
            if ((imports & CommonImports.Bcl) != 0)
            {
                headerBuilder.Append("import \"protobuf-net/bcl.proto\"; // schema for protobuf-net's handling of core .NET types").AppendLine();
            }
            if ((imports & CommonImports.Protogen) != 0)
            {
                headerBuilder.Append("import \"protobuf-net/protogen.proto\"; // custom protobuf-net options").AppendLine();
            }
            if ((imports & CommonImports.Timestamp) != 0)
            {
                headerBuilder.Append("import \"google/protobuf/timestamp.proto\";").AppendLine();
            }
            if ((imports & CommonImports.Duration) != 0)
            {
                headerBuilder.Append("import \"google/protobuf/duration.proto\";").AppendLine();
            }
            return headerBuilder.Append(bodyBuilder).AppendLine().ToString();
        }
        [Flags]
        internal enum CommonImports
        {
            None = 0,
            Bcl = 1,
            Timestamp = 2,
            Duration = 4,
            Protogen = 8
        }
        private void CascadeDependents(BasicList list, MetaType metaType)
        {
            MetaType tmp;
            if (metaType.IsList)
            {
                Type itemType = TypeModel.GetListItemType(metaType.Type);
                TryGetCoreSerializer(list, itemType);
            }
            else
            {
                if (metaType.IsAutoTuple)
                {
                    if (MetaType.ResolveTupleConstructor(metaType.Type, out var mapping) != null)
                    {
                        for (int i = 0; i < mapping.Length; i++)
                        {
                            Type type = null;
                            if (mapping[i] is PropertyInfo) type = ((PropertyInfo)mapping[i]).PropertyType;
                            else if (mapping[i] is FieldInfo) type = ((FieldInfo)mapping[i]).FieldType;
                            TryGetCoreSerializer(list, type);
                        }
                    }
                }
                else
                {
                    foreach (ValueMember member in metaType.Fields)
                    {
                        Type type = member.ItemType;
                        if (member.IsMap)
                        {
                            member.ResolveMapTypes(out _, out _, out type); // don't need key-type
                        }
                        if (type == null) type = member.MemberType;
                        TryGetCoreSerializer(list, type);
                    }
                }
                foreach (var genericArgument in metaType.GetAllGenericArguments())
                {
                    if (genericArgument.IsArray)
                    {
                        RetrieveArrayListTypes(genericArgument, out var itemType, out var _);
                        VerifyNotNested(genericArgument, itemType);
                        if (itemType != null)
                        {
                            TryGetCoreSerializer(list, itemType);
                        }
                    }
                    else
                    {
                        TryGetCoreSerializer(list, genericArgument);
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
                            CascadeDependents(list, tmp);
                        }
                    }
                }
                tmp = metaType.BaseType;
                if (tmp != null) tmp = tmp.GetSurrogateOrSelf(); // note: already walking base-types; exclude base
                if (tmp != null && !list.Contains(tmp))
                {
                    list.Add(tmp);
                    CascadeDependents(list, tmp);
                }
            }
        }

        private void TryGetCoreSerializer(BasicList list, Type itemType)
        {
            var coreSerializer = ValueMember.TryGetCoreSerializer(this, DataFormat.Default, itemType, out _, false, false, false, false);
            if (coreSerializer != null)
            {
                return;
            }
            int index = FindOrAddAuto(itemType, false, false, false);
            if (index < 0)
            {
                return;
            }
            var temp = ((MetaType)types[index]).GetSurrogateOrBaseOrSelf(false);
            if (list.Contains(temp))
            {
                return;
            }
            // could perhaps also implement as a queue, but this should work OK for sane models
            list.Add(temp);
            CascadeDependents(list, temp);
        }

        internal RuntimeTypeModel(bool isDefault)
        {
            AutoAddMissingTypes = true;
            UseImplicitZeroDefaults = true;
            SetOption(OPTIONS_IsDefaultModel, isDefault);
#if !DEBUG
            try
            {
                AutoCompile = EnableAutoCompile();
            }
            catch { } // this is all kinds of brittle on things like UWP
#endif
            if (isDefault) TypeModel.SetDefaultModel(this);
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
        public MetaType this[Type type] { get { return (MetaType)types[FindOrAddAuto(type, true, false, false)]; } }

        internal MetaType FindWithoutAdd(Type type)
        {
            // this list is thread-safe for reading
            foreach (MetaType metaType in types)
            {
                if (metaType.Type == type)
                {
                    if (metaType.Pending) WaitOnLock();
                    return metaType;
                }
            }
            // if that failed, check for a proxy
            Type underlyingType = ResolveProxies(type);
            return underlyingType == null ? null : FindWithoutAdd(underlyingType);
        }

        private static readonly BasicList.MatchPredicate
            MetaTypeFinder = new BasicList.MatchPredicate(MetaTypeFinderImpl),
            BasicTypeFinder = new BasicList.MatchPredicate(BasicTypeFinderImpl);

        private static bool MetaTypeFinderImpl(object value, object ctx)
        {
            return ((MetaType)value).Type == (Type)ctx;
        }

        private static bool BasicTypeFinderImpl(object value, object ctx)
        {
            return ((BasicType)value).Type == (Type)ctx;
        }

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

        private readonly BasicList basicTypes = new BasicList();

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
                    ? ValueMember.TryGetCoreSerializer(this, DataFormat.Default, type, out WireType defaultWireType, false, false, false, false)
                    : null;

                if (ser != null) basicTypes.Add(new BasicType(type, ser));
                return ser;
            }
        }

        internal int FindOrAddAuto(Type type, bool demand, bool addWithContractOnly, bool addEvenIfAutoDisabled)
        {
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

            if (!type.IsEnum && TryGetBasicTypeSerializer(type) != null)
            {
                if (shouldAdd && !addWithContractOnly) throw MetaType.InbuiltType(type);
                return -1; // this will never be a meta-type
            }

            // otherwise: we don't yet know

            // check for proxy types
            Type underlyingType = ResolveProxies(type);
            if (underlyingType != null && underlyingType != type)
            {
                key = types.IndexOf(MetaTypeFinder, underlyingType);
                type = underlyingType; // if new added, make it reflect the underlying type
            }

            if (key < 0)
            {
                int opaqueToken = 0;
                Type origType = type;
                bool weAdded = false;
                try
                {
                    TakeLock(ref opaqueToken);
                    // try to recognise a few familiar patterns...
                    if ((metaType = RecogniseCommonTypes(type)) == null)
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
                            if (demand) ThrowUnexpectedType(type);
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
                        metaType.ApplyDefaultBehaviour();
                        metaType.Pending = false;
                    }
                }
                finally
                {
                    ReleaseLock(opaqueToken);
                    if (weAdded)
                    {
                        ResetKeyCache();
                    }
                }
            }
            return key;
        }

#pragma warning disable RCS1163, IDE0060 // Unused parameter.
        private MetaType RecogniseCommonTypes(Type type)
#pragma warning restore RCS1163, IDE0060 // Unused parameter.
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
        public MetaType Add(Type type, bool applyDefaultBehaviour = true)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            MetaType newType = FindWithoutAdd(type);
            if (newType != null) return newType; // return existing
            int opaqueToken = 0;

            if (type.IsInterface && MetaType.ienumerable.IsAssignableFrom(type)
                    && GetListItemType(type) == null)
            {
                throw new ArgumentException("IEnumerable[<T>] data cannot be used as a meta-type unless an Add method can be resolved");
            }
            try
            {
                newType = RecogniseCommonTypes(type);
                if (newType != null)
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
                if (newType == null) newType = Create(type);
                newType.Pending = true;
                TakeLock(ref opaqueToken);
                // double checked
                if (FindWithoutAdd(type) != null) throw new ArgumentException("Duplicate type", nameof(type));
                ThrowIfFrozen();
                types.Add(newType);
                if (applyDefaultBehaviour) { newType.ApplyDefaultBehaviour(); }
                newType.Pending = false;
            }
            finally
            {
                ReleaseLock(opaqueToken);
                ResetKeyCache();
            }

            return newType;
        }

        /// <summary>
        /// Should serializers be compiled on demand? It may be useful
        /// to disable this for debugging purposes.
        /// </summary>
        public bool AutoCompile
        {
            get { return GetOption(OPTIONS_AutoCompile); }
            set { SetOption(OPTIONS_AutoCompile, value); }
        }

        /// <summary>
        /// Should support for unexpected types be added automatically?
        /// If false, an exception is thrown when unexpected types
        /// are encountered.
        /// </summary>
        public bool AutoAddMissingTypes
        {
            get { return GetOption(OPTIONS_AutoAddMissingTypes); }
            set
            {
                if (!value && GetOption(OPTIONS_IsDefaultModel))
                {
                    throw new InvalidOperationException("The default model must allow missing types");
                }
                ThrowIfFrozen();
                SetOption(OPTIONS_AutoAddMissingTypes, value);
            }
        }
        /// <summary>
        /// Verifies that the model is still open to changes; if not, an exception is thrown
        /// </summary>
        private void ThrowIfFrozen()
        {
            if (GetOption(OPTIONS_Frozen)) throw new InvalidOperationException("The model cannot be changed once frozen");
        }

        /// <summary>
        /// Prevents further changes to this model
        /// </summary>
        public void Freeze()
        {
            if (GetOption(OPTIONS_IsDefaultModel)) throw new InvalidOperationException("The default model cannot be frozen");
            SetOption(OPTIONS_Frozen, true);
        }

        private readonly BasicList types = new BasicList();

        /// <summary>
        /// Provides the key that represents a given type in the current model.
        /// </summary>
        protected override int GetKeyImpl(Type type)
        {
            return GetKey(type, false, true);
        }

        /// <summary>Resolve a service relative to T</summary>
        protected internal override IProtoSerializer<T> GetSerializer<T>()
            => GetServices<T>() as IProtoSerializer<T>;

        /// <summary>Resolve a service relative to T</summary>
        protected internal override IProtoSubTypeSerializer<T> GetSubTypeSerializer<T>()
            => GetServices<T>() as IProtoSubTypeSerializer<T>;

        /// <summary>Resolve a service relative to T</summary>
        protected internal override IProtoFactory<T> GetFactory<T>()
            => GetServices<T>() as IProtoFactory<T>;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private object GetServices<T>()
            => (_serviceCache[typeof(T)] ?? GetServicesSlow(typeof(T)));


        private readonly Hashtable _serviceCache = new Hashtable();
        internal void ResetServiceCache(Type type)
        {
            if (type != null)
            {
                lock (_serviceCache)
                {
                    _serviceCache.Remove(type);
                }
            }
        }

        private object GetServicesSlow(Type type)
        {
            int typeIndex = GetKey(type, false, false);
            if (typeIndex >= 0)
            {
                var mt = (MetaType)types[typeIndex];
                var service = mt.Serializer;
                lock (_serviceCache)
                {
                    _serviceCache[type] = service;
                }
                return service;
            }
            return null;
        }

        internal int GetKey(Type type, bool demand, bool getBaseKey)
        {
            Debug.Assert(type != null);
            try
            {
                int typeIndex = FindOrAddAuto(type, demand, true, false);
                if (typeIndex >= 0)
                {
                    MetaType mt = (MetaType)types[typeIndex];
                    if (getBaseKey)
                    {
                        mt = MetaType.GetRootType(mt);
                        typeIndex = FindOrAddAuto(mt.Type, true, true, false);
                    }
                }
                return typeIndex;
            }
            catch (NotSupportedException)
            {
                throw; // re-surface "as-is"
            }
            catch (Exception ex)
            {
                if (ex.Message.IndexOf(type.FullName) >= 0) throw;  // already enough info
                throw new ProtoException(ex.Message + " (" + type.FullName + ")", ex);
            }
        }

        /// <summary>
        /// Writes a protocol-buffer representation of the given instance to the supplied stream.
        /// </summary>
        /// <param name="key">Represents the type (including inheritance) to consider.</param>
        /// <param name="value">The existing instance to be serialized (cannot be null).</param>
        /// <param name="state">Writer state</param>
        protected internal override void Serialize(ref ProtoWriter.State state, int key, object value)
        {
            //Debug.WriteLine("Serialize", value);
            ((MetaType)types[key]).Serializer.Write(ref state, value);
        }

        /// <summary>
        /// Applies a protocol-buffer stream to an existing instance (which may be null).
        /// </summary>
        /// <param name="key">Represents the type (including inheritance) to consider.</param>
        /// <param name="value">The existing instance to be modified (can be null).</param>
        /// <param name="state">Reader state</param>
        protected internal override object DeserializeCore(ref ProtoReader.State state, int key, object value)
        {
            //Debug.WriteLine("Deserialize", value);
            IRuntimeProtoSerializerNode ser = ((MetaType)types[key]).Serializer;
            if (value == null && ser.ExpectedType.IsValueType)
            {
                if (ser.RequiresOldValue) value = Activator.CreateInstance(ser.ExpectedType, nonPublic: true);
                return ser.Read(ref state, value);
            }
            else
            {
                return ser.Read(ref state, value);
            }
        }

        // this is used by some unit-tests; do not remove
        internal Compiler.ProtoSerializer<TActual> GetSerializer<TActual>(IRuntimeProtoSerializerNode serializer, bool compiled)
        {
            if (serializer == null) throw new ArgumentNullException(nameof(serializer));

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
                if (mt.Serializer == null)
                    throw new InvalidOperationException("No serializer available for " + mt.Type.Name);
            }
        }

        internal sealed class SerializerPair : IComparable
        {
            int IComparable.CompareTo(object obj)
            {
                if (obj == null) throw new ArgumentException("obj");
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

        /// <summary>
        /// Fully compiles the current model into a static-compiled model instance
        /// </summary>
        /// <remarks>A full compilation is restricted to accessing public types / members</remarks>
        /// <returns>An instance of the newly created compiled type-model</returns>
        public TypeModel Compile()
        {
            CompilerOptions options = new CompilerOptions();
            return Compile(options);
        }

        internal static ILGenerator Override(TypeBuilder type, string name)
        {
            MethodInfo baseMethod;
            try
            {
                baseMethod = type.BaseType.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance);
            }
            catch (Exception ex)
            {
                throw new ArgumentException($"Unable to resolve '{name}': {ex.Message}", nameof(name), ex);
            }

            ParameterInfo[] parameters = baseMethod.GetParameters();
            Type[] paramTypes = new Type[parameters.Length];
            for (int i = 0; i < paramTypes.Length; i++)
            {
                paramTypes[i] = parameters[i].ParameterType;
            }
            MethodBuilder newMethod = type.DefineMethod(baseMethod.Name,
                (baseMethod.Attributes & ~MethodAttributes.Abstract) | MethodAttributes.Final, baseMethod.CallingConvention, baseMethod.ReturnType, paramTypes);
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
                if (from == null) throw new ArgumentNullException(nameof(from));
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
        public TypeModel Compile(CompilerOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            string typeName = options.TypeName;
#pragma warning disable 0618
            string path = options.OutputPath;
#pragma warning restore 0618
            BuildAllSerializers();
            Freeze();
            bool save = !string.IsNullOrEmpty(path);
            if (string.IsNullOrEmpty(typeName))
            {
                if (save) throw new ArgumentNullException("typeName");
                typeName = Guid.NewGuid().ToString();
            }

            string assemblyName, moduleName;
            if (path == null)
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
                AssemblyBuilderAccess.Run);
            ModuleBuilder module = asm.DefineDynamicModule(moduleName);
#else
            AssemblyName an = new AssemblyName { Name = assemblyName };
            AssemblyBuilder asm = AppDomain.CurrentDomain.DefineDynamicAssembly(an,
                save ? AssemblyBuilderAccess.RunAndSave : AssemblyBuilderAccess.Run);
            ModuleBuilder module = save ? asm.DefineDynamicModule(moduleName, path)
                                        : asm.DefineDynamicModule(moduleName);
#endif
            var scope = CompilerContextScope.CreateForModule(module, true, assemblyName);
            WriteAssemblyAttributes(options, assemblyName, asm);

            TypeBuilder type = WriteBasicTypeModel(options, typeName, module);

            WriteSerializers(scope, type);

            WriteConstructorsAndOverrides(type);

#if PLAT_NO_EMITDLL
            Type finalType = type.CreateTypeInfo().AsType();
#else
            Type finalType = type.CreateType();
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

        private void WriteConstructorsAndOverrides(TypeBuilder type)
        {
            var il = Override(type, nameof(TypeModel.GetInternStrings));
            il.Emit(InternStrings ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ret);

            il = Override(type, nameof(TypeModel.SerializeDateTimeKind));
            il.Emit(IncludeDateTimeKind ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ret);

            type.DefineDefaultConstructor(MethodAttributes.Public);
        }


        private void WriteSerializers(CompilerContextScope scope, TypeBuilder type)
        {
            for (int index = 0; index < types.Count; index++)
            {
                var metaType = (MetaType)types[index];
                var serializer = metaType.Serializer;
                var runtimeType = metaType.Type;

                Type inheritanceRoot = metaType.GetInheritanceRoot();
                
                // we always emit the serializer API
                var serType = typeof(IProtoSerializer<>).MakeGenericType(runtimeType);
                type.AddInterfaceImplementation(serType);

                var il = CompilerContextScope.Implement(type, serType, nameof(IProtoSerializer<string>.Read));
                using (var ctx = new CompilerContext(scope, il, false, false, this, runtimeType, nameof(IProtoSerializer<string>.Read)))
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

                il = CompilerContextScope.Implement(type, serType, nameof(IProtoSerializer<string>.Write));
                using (var ctx = new CompilerContext(scope, il, false, true, this, runtimeType, nameof(IProtoSerializer<string>.Write)))
                {
                    if (serializer.HasInheritance) serializer.EmitWriteRoot(ctx, ctx.InputValue);
                    else serializer.EmitWrite(ctx, ctx.InputValue);
                    ctx.Return();
                }

                // and we emit the sub-type serializer whenever inheritance is involved
                if (serializer.HasInheritance)
                {
                    serType = typeof(IProtoSubTypeSerializer<>).MakeGenericType(runtimeType);
                    type.AddInterfaceImplementation(serType);

                    il = CompilerContextScope.Implement(type, serType, nameof(IProtoSubTypeSerializer<string>.WriteSubType));
                    using (var ctx = new CompilerContext(scope, il, false, true, this,
                         runtimeType, nameof(IProtoSubTypeSerializer<string>.WriteSubType)))
                    {
                        serializer.EmitWrite(ctx, ctx.InputValue);
                        ctx.Return();
                    }

                    il = CompilerContextScope.Implement(type, serType, nameof(IProtoSubTypeSerializer<string>.ReadSubType));
                    using (var ctx = new CompilerContext(scope, il, false, false, this,
                         runtimeType, nameof(IProtoSubTypeSerializer<string>.ReadSubType)))
                    {
                        serializer.EmitRead(ctx, ctx.InputValue);
                        // note that EmitRead will unwrap the T for us on the stack
                        ctx.Return();
                    }
                }

                // if we're constructor skipping, provide a factory for that
                if (serializer.ShouldEmitCreateInstance)
                {
                    serType = typeof(IProtoFactory<>).MakeGenericType(runtimeType);
                    type.AddInterfaceImplementation(serType);

                    il = CompilerContextScope.Implement(type, serType, nameof(IProtoFactory<string>.Create));
                    using var ctx = new CompilerContext(scope, il, false, false, this,
                         typeof(ISerializationContext), nameof(IProtoFactory<string>.Create));
                    serializer.EmitCreateInstance(ctx, false);
                    ctx.Return();
                }
            }
        }

        private TypeBuilder WriteBasicTypeModel(CompilerOptions options, string typeName, ModuleBuilder module)
        {
            Type baseType = typeof(TypeModel);
            TypeAttributes typeAttributes = (baseType.Attributes & ~TypeAttributes.Abstract) | TypeAttributes.Sealed;
            if (options.Accessibility == Accessibility.Internal)
            {
                typeAttributes &= ~TypeAttributes.Public;
            }

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
                if (versionAttribType != null)
                {
                    PropertyInfo[] props;
                    object[] propValues;
                    if (string.IsNullOrEmpty(options.TargetFrameworkDisplayName))
                    {
                        props = new PropertyInfo[0];
                        propValues = new object[0];
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

            if (internalsVisibleToAttribType != null)
            {
                BasicList internalAssemblies = new BasicList(), consideredAssemblies = new BasicList();
                foreach (MetaType metaType in types)
                {
                    Assembly assembly = metaType.Type.Assembly;
                    if (consideredAssemblies.IndexOfReference(assembly) >= 0) continue;
                    consideredAssemblies.Add(assembly);

                    AttributeMap[] assemblyAttribsMap = AttributeMap.Create(assembly);
                    for (int i = 0; i < assemblyAttribsMap.Length; i++)
                    {
                        if (assemblyAttribsMap[i].AttributeType != internalsVisibleToAttribType) continue;

                        assemblyAttribsMap[i].TryGet("AssemblyName", out var privelegedAssemblyObj);
                        string privelegedAssemblyName = privelegedAssemblyObj as string;
                        if (privelegedAssemblyName == assemblyName || string.IsNullOrEmpty(privelegedAssemblyName)) continue; // ignore

                        if (internalAssemblies.IndexOfString(privelegedAssemblyName) >= 0) continue; // seen it before
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
            return meta != null && meta.IsPrepared();
        }

        internal EnumSerializer.EnumPair[] GetEnumMap(Type type)
        {
            int index = FindOrAddAuto(type, false, false, false);
            return index < 0 ? null : ((MetaType)types[index]).GetEnumMap();
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
                    if (handler != null)
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

        internal void ResolveListTypes(Type type, ref Type itemType, ref Type defaultType)
        {
            if (type == null) return;
            if (Helpers.GetTypeCode(type) != ProtoTypeCode.Unknown) return; // don't try this[type] for inbuilts

            // handle arrays
            if (type.IsArray)
            {
                RetrieveArrayListTypes(type, out itemType, out defaultType);
            }
            else
            {
                // if not an array, first check it isn't explicitly opted out
                if (this[type].IgnoreListHandling) return;
            }

            // handle lists 
            if (itemType == null) { itemType = TypeModel.GetListItemType(type); }

            // check for nested data (not allowed)
            VerifyNotNested(type, itemType);

            if (itemType != null && defaultType == null)
            {
                if (type.IsClass && !type.IsAbstract && Helpers.GetConstructor(type, Type.EmptyTypes, true) != null)
                {
                    defaultType = type;
                }
                if (defaultType == null)
                {
                    if (type.IsInterface)
                    {
                        Type[] genArgs;
                        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(System.Collections.Generic.IDictionary<,>)
                            && itemType == typeof(System.Collections.Generic.KeyValuePair<,>).MakeGenericType(genArgs = type.GetGenericArguments()))
                        {
                            defaultType = typeof(System.Collections.Generic.Dictionary<,>).MakeGenericType(genArgs);
                        }
                        else
                        {
                            defaultType = typeof(System.Collections.Generic.List<>).MakeGenericType(itemType);
                        }
                    }
                }
                // verify that the default type is appropriate
                if (defaultType != null && !type.IsAssignableFrom(defaultType)) { defaultType = null; }
            }
        }

        private void VerifyNotNested(Type type, Type itemType)
        {
            if (itemType != null)
            {
                Type nestedItemType = null, nestedDefaultType = null;
                ResolveListTypes(itemType, ref nestedItemType, ref nestedDefaultType);
                if (nestedItemType != null)
                {
                    throw TypeModel.CreateNestedListsNotSupported(type);
                }
            }
        }

        private static void RetrieveArrayListTypes(Type type, out Type itemType, out Type defaultType)
        {
            if (type.GetArrayRank() != 1)
            {
                throw new NotSupportedException("Multi-dimension arrays are supported");
            }
            itemType = type.GetElementType();
            if (itemType == typeof(byte))
            {
                defaultType = itemType = null;
            }
            else
            {
                defaultType = type;
            }
        }

        internal string GetSchemaTypeName(Type effectiveType, DataFormat dataFormat, bool asReference, bool dynamicType, ref CommonImports imports)
        {
            Type tmp = Nullable.GetUnderlyingType(effectiveType);
            if (tmp != null) effectiveType = tmp;

            if (effectiveType == typeof(byte[])) return "bytes";

            IRuntimeProtoSerializerNode ser = ValueMember.TryGetCoreSerializer(this, dataFormat, effectiveType, out var _, false, false, false, false);
            if (ser == null)
            {   // model type
                if (asReference || dynamicType)
                {
                    imports |= CommonImports.Bcl;
                    return ".bcl.NetObjectProxy";
                }
                return this[effectiveType].GetSurrogateOrBaseOrSelf(true).GetSchemaTypeName();
            }
            else
            {
                if (ser is ParseableSerializer)
                {
                    if (asReference) imports |= CommonImports.Bcl;
                    return asReference ? ".bcl.NetObjectProxy" : "string";
                }

                switch (Helpers.GetTypeCode(effectiveType))
                {
                    case ProtoTypeCode.Boolean: return "bool";
                    case ProtoTypeCode.Single: return "float";
                    case ProtoTypeCode.Double: return "double";
                    case ProtoTypeCode.String:
                        if (asReference) imports |= CommonImports.Bcl;
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
                            case DataFormat.WellKnown:
                                imports |= CommonImports.Timestamp;
                                return ".google.protobuf.Timestamp";
                            default:
                                imports |= CommonImports.Bcl;
                                return ".bcl.DateTime";
                        }
                    case ProtoTypeCode.TimeSpan:
                        switch (dataFormat)
                        {
                            case DataFormat.FixedSize: return "sint64";
                            case DataFormat.WellKnown:
                                imports |= CommonImports.Duration;
                                return ".google.protobuf.Duration";
                            default:
                                imports |= CommonImports.Bcl;
                                return ".bcl.TimeSpan";
                        }
                    case ProtoTypeCode.Decimal: imports |= CommonImports.Bcl; return ".bcl.Decimal";
                    case ProtoTypeCode.Guid: imports |= CommonImports.Bcl; return ".bcl.Guid";
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

        internal void VerifyFactory(MethodInfo factory, Type type)
        {
            if (factory != null)
            {
                if (type != null && type.IsValueType) throw new InvalidOperationException();
                if (!factory.IsStatic) throw new ArgumentException("A factory-method must be static", nameof(factory));
                if (type != null && factory.ReturnType != type && factory.ReturnType != typeof(object)) throw new ArgumentException("The factory-method must return object" + (type == null ? "" : (" or " + type.FullName)), nameof(factory));

                if (!CallbackSet.CheckCallbackParameters(factory)) throw new ArgumentException("Invalid factory signature in " + factory.DeclaringType.FullName + "." + factory.Name, nameof(factory));
            }
        }

        /// <summary>
        /// Creates a new runtime model, to which the caller
        /// can add support for a range of types. A model
        /// can be used "as is", or can be compiled for
        /// optimal performance.
        /// </summary>
        public static new RuntimeTypeModel Create()
        {
            return new RuntimeTypeModel(false);
        }

        /// <summary>
        /// Create a model that serializes all types from an
        /// assembly specified by type
        /// </summary>
        public static new TypeModel CreateForAssembly<T>()
            => CreateForAssembly(typeof(T).Assembly);
        /// <summary>
        /// Create a model that serializes all types from an
        /// assembly specified by type
        /// </summary>
        public static new TypeModel CreateForAssembly(Type type)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            return CreateForAssembly(type.Assembly);
        }

        /// <summary>
        /// Create a model that serializes all types from an assembly
        /// </summary>
        public static new TypeModel CreateForAssembly(Assembly assembly)
            => (TypeModel)s_assemblyModels[assembly ?? throw new ArgumentNullException(nameof(assembly))]
            ?? CreateForAssemblyImpl(assembly);

        private readonly static Hashtable s_assemblyModels = new Hashtable();

        private static TypeModel CreateForAssemblyImpl(Assembly assembly)
        {
            if (assembly == null) throw new ArgumentNullException(nameof(assembly));
            lock (assembly)
            {
                var found = (TypeModel)s_assemblyModels[assembly];
                if (found != null) return found;

                RuntimeTypeModel model = null;
                foreach (var type in assembly.GetTypes())
                {
                    if (type.IsDefined(typeof(ProtoContractAttribute), true))
                    {
                        (model ?? (model = Create())).Add(type, true);
                    }
                }
                if (model == null)
                    throw new InvalidOperationException($"No types marked [ProtoContract] found in assembly '{assembly.GetName().Name}'");
                var compiled = model.Compile();
                s_assemblyModels[assembly] = compiled;
                return compiled;
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