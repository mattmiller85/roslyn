﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Threading;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis
{
    /// <summary>
    /// A set of helpers for extracting elements from metadata.
    /// This type is not responsible for managing the underlying storage
    /// backing the PE image.
    /// </summary>
    internal sealed class PEModule : IDisposable
    {
        // Either we have PEReader or we have pointer and size of the metadata blob:
        private readonly PEReader peReaderOpt;
        private readonly IntPtr metadataPointerOpt;
        private readonly int metadataSizeOpt;

        private MetadataReader lazyMetadataReader;

        private ImmutableArray<AssemblyIdentity> lazyAssemblyReferences;
        private Dictionary<string, AssemblyReferenceHandle> lazyForwardedTypesToAssemblyMap;

        private readonly Lazy<IdentifierCollection> lazyTypeNameCollection;
        private readonly Lazy<IdentifierCollection> lazyNamespaceNameCollection;

        private string lazyName;
        private bool isDisposed;

        /// <summary>
        /// Using <see cref="ThreeState"/> as a type for atomicity.
        /// </summary>
        private ThreeState lazyContainsNoPiaLocalTypes;

        /// <summary>
        /// If bitmap is not null, each bit indicates whether a TypeDef 
        /// with corresponding RowId has been checked if it is a NoPia 
        /// local type. If the bit is 1, local type will have an entry 
        /// in m_lazyTypeDefToTypeIdentifierMap.
        /// </summary>
        private int[] lazyNoPiaLocalTypeCheckBitMap;

        /// <summary>
        /// For each TypeDef that has 1 in m_lazyNoPiaLocalTypeCheckBitMap,
        /// this map stores corresponding TypeIdentifier AttributeInfo. 
        /// </summary>
        private ConcurrentDictionary<TypeDefinitionHandle, AttributeInfo> lazyTypeDefToTypeIdentifierMap;

        // The module can be used by different compilations or different versions of the "same"
        // compilation, which use different hash algorithms. Let's cache result for each distinct 
        // algorithm.
        private readonly CryptographicHashProvider hashesOpt;

        private delegate bool AttributeValueExtractor<T>(out T value, ref BlobReader sigReader);
        private static readonly AttributeValueExtractor<string> AttributeStringValueExtractor = CrackStringInAttributeValue;
        private static readonly AttributeValueExtractor<StringAndInt> AttributeStringAndIntValueExtractor = CrackStringAndIntInAttributeValue;
        private static readonly AttributeValueExtractor<short> AttributeShortValueExtractor = CrackShortInAttributeValue;
        private static readonly AttributeValueExtractor<int> AttributeIntValueExtractor = CrackIntInAttributeValue;
        private static readonly AttributeValueExtractor<long> AttributeLongValueExtractor = CrackLongInAttributeValue;
        // Note: not a general purpose helper
        private static readonly AttributeValueExtractor<decimal> DecimalValueInDecimalConstantAttributeExtractor = CrackDecimalInDecimalConstantAttribute;
        private static readonly AttributeValueExtractor<ImmutableArray<bool>> AttributeBoolArrayValueExtractor = CrackBoolArrayInAttributeValue;
        private static readonly AttributeValueExtractor<ObsoleteAttributeData> AttributeObsoleteDataExtractor = CrackObsoleteAttributeData;
        private static readonly AttributeValueExtractor<ObsoleteAttributeData> AttributeDeprecatedDataExtractor = CrackDeprecatedAttributeData;

        internal PEModule(PEReader peReader, IntPtr metadataOpt, int metadataSizeOpt, bool includeEmbeddedInteropTypes = false)
        {
            // shall not throw

            Debug.Assert((peReader == null) ^ (metadataOpt == IntPtr.Zero && metadataSizeOpt == 0));
            Debug.Assert(metadataOpt == IntPtr.Zero || metadataSizeOpt > 0);

            this.peReaderOpt = peReader;
            this.metadataPointerOpt = metadataOpt;
            this.metadataSizeOpt = metadataSizeOpt;
            this.lazyTypeNameCollection = new Lazy<IdentifierCollection>(ComputeTypeNameCollection);
            this.lazyNamespaceNameCollection = new Lazy<IdentifierCollection>(ComputeNamespaceNameCollection);
            this.hashesOpt = (peReader != null) ? new PEHashProvider(peReader) : null;
            this.lazyContainsNoPiaLocalTypes = includeEmbeddedInteropTypes ? ThreeState.False : ThreeState.Unknown;
        }

        private sealed class PEHashProvider : CryptographicHashProvider
        {
            private readonly PEReader peReader;

            public PEHashProvider(PEReader peReader)
            {
                Debug.Assert(peReader != null);
                this.peReader = peReader;
            }

            internal unsafe override ImmutableArray<byte> ComputeHash(HashAlgorithm algorithm)
            {
                PEMemoryBlock block = peReader.GetEntireImage();
                byte[] hash;

                using (var stream = new ReadOnlyUnmanagedMemoryStream(peReader, (IntPtr)block.Pointer, block.Length))
                {
                    hash = algorithm.ComputeHash(stream);
                }

                return ImmutableArray.Create(hash);
            }
        }

        internal bool IsDisposed
        {
            get
            {
                return isDisposed;
            }
        }

        public void Dispose()
        {
            isDisposed = true;

            if (peReaderOpt != null)
            {
                peReaderOpt.Dispose();
            }
        }

        // for testing
        internal PEReader PEReaderOpt
        {
            get
            {
                return peReaderOpt;
            }
        }

        internal unsafe MetadataReader MetadataReader
        {
            get
            {
                if (lazyMetadataReader == null)
                {
                    MetadataReader newReader;

                    // PEModule is either created with metadata memory block or a PE reader.
                    if (metadataPointerOpt != IntPtr.Zero)
                    {
                        newReader = new MetadataReader((byte*)metadataPointerOpt, metadataSizeOpt, MetadataReaderOptions.ApplyWindowsRuntimeProjections, StringTableDecoder.Instance);
                    }
                    else
                    {
                        Debug.Assert(peReaderOpt != null);
                        if (!peReaderOpt.HasMetadata)
                        {
                            throw new BadImageFormatException(CodeAnalysisResources.PEImageDoesntContainManagedMetadata);
                        }

                        newReader = peReaderOpt.GetMetadataReader(MetadataReaderOptions.ApplyWindowsRuntimeProjections, StringTableDecoder.Instance);
                    }

                    Interlocked.CompareExchange(ref lazyMetadataReader, newReader, null);
                }

                return lazyMetadataReader;
            }
        }

        #region Module level properties and methods

        internal bool IsManifestModule
        {
            get
            {
                return MetadataReader.IsAssembly;
            }
        }

        internal bool IsLinkedModule
        {
            get
            {
                return !MetadataReader.IsAssembly;
            }
        }

        internal bool IsCOFFOnly
        {
            get
            {
                // default value if we only have metadata
                if (peReaderOpt == null)
                {
                    return false;
                }

                return peReaderOpt.PEHeaders.IsCoffOnly;
            }
        }

        /// <summary>
        /// Target architecture of the machine.
        /// </summary>
        internal Machine Machine
        {
            get
            {
                // platform agnostic if we only have metadata
                if (peReaderOpt == null)
                {
                    return Machine.I386;
                }

                return peReaderOpt.PEHeaders.CoffHeader.Machine;
            }
        }

        /// <summary>
        /// Indicates that this PE file makes Win32 calls. See CorPEKind.pe32BitRequired for more information (http://msdn.microsoft.com/en-us/library/ms230275.aspx).
        /// </summary>
        internal bool Bit32Required
        {
            get
            {
                // platform agnostic if we only have metadata
                if (peReaderOpt == null)
                {
                    return false;
                }

                return (peReaderOpt.PEHeaders.CorHeader.Flags & CorFlags.Requires32Bit) != 0;
            }
        }

        internal ImmutableArray<byte> GetHash(AssemblyHashAlgorithm algorithmId)
        {
            Debug.Assert(hashesOpt != null);
            return hashesOpt.GetHash(algorithmId);
        }

        #endregion

        #region ModuleDef helpers

        internal string Name
        {
            get
            {
                if (this.lazyName == null)
                {
                    this.lazyName = MetadataReader.GetString(MetadataReader.GetModuleDefinition().Name);
                }

                return this.lazyName;
            }
        }

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        internal Guid GetModuleVersionIdOrThrow()
        {
            return MetadataReader.GetGuid(MetadataReader.GetModuleDefinition().Mvid);
        }

        #endregion

        #region ModuleRef, File helpers

        /// <summary>
        /// Returns the names of linked managed modules.
        /// </summary>
        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        internal ImmutableArray<string> GetMetadataModuleNamesOrThrow()
        {
            var builder = ArrayBuilder<string>.GetInstance();
            try
            {
                foreach (var fileHandle in MetadataReader.AssemblyFiles)
                {
                    var file = MetadataReader.GetAssemblyFile(fileHandle);
                    if (!file.ContainsMetadata)
                    {
                        continue;
                    }

                    string moduleName = MetadataReader.GetString(file.Name);
                    if (!MetadataHelpers.IsValidMetadataFileName(moduleName))
                    {
                        throw new BadImageFormatException(string.Format(CodeAnalysisResources.InvalidModuleName, this.Name, moduleName));
                    }

                    builder.Add(moduleName);
                }

                return builder.ToImmutable();
            }
            finally
            {
                builder.Free();
            }
        }

        /// <summary>
        /// Returns names of referenced modules.
        /// </summary>
        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        internal IEnumerable<string> GetReferencedManagedModulesOrThrow()
        {
            HashSet<Handle> nameTokens = new HashSet<Handle>();
            foreach (var handle in MetadataReader.TypeReferences)
            {
                TypeReference typeRef = MetadataReader.GetTypeReference(handle);
                Handle scope = typeRef.ResolutionScope;
                if (scope.Kind == HandleKind.ModuleReference)
                {
                    nameTokens.Add(scope);
                }
            }

            foreach (var token in nameTokens)
            {
                yield return this.GetModuleRefNameOrThrow((ModuleReferenceHandle)token);
            }
        }

        internal ImmutableArray<EmbeddedResource> GetEmbeddedResourcesOrThrow()
        {
            if (MetadataReader.ManifestResources.Count == 0)
            {
                return ImmutableArray<EmbeddedResource>.Empty;
            }

            var builder = ImmutableArray.CreateBuilder<EmbeddedResource>();
            foreach (var handle in MetadataReader.ManifestResources)
            {
                var resource = MetadataReader.GetManifestResource(handle);
                if (resource.Implementation.IsNil)
                {
                    string resourceName = MetadataReader.GetString(resource.Name);
                    builder.Add(new EmbeddedResource((uint)resource.Offset, resource.Attributes, resourceName));
                }
            }

            return builder.ToImmutable();
        }

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        public string GetModuleRefNameOrThrow(ModuleReferenceHandle moduleRef)
        {
            return MetadataReader.GetString(MetadataReader.GetModuleReference(moduleRef).Name);
        }

        #endregion

        #region AssemblyRef helpers

        // The array is sorted by AssemblyRef RowId, starting with RowId=1 and doesn't have any RowId gaps.
        public ImmutableArray<AssemblyIdentity> ReferencedAssemblies
        {
            get
            {
                if (this.lazyAssemblyReferences == null)
                {
                    this.lazyAssemblyReferences = GetReferencedAssembliesOrThrow(this.MetadataReader);
                }

                return this.lazyAssemblyReferences;
            }
        }

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        private static ImmutableArray<AssemblyIdentity> GetReferencedAssembliesOrThrow(MetadataReader reader)
        {
            var result = ArrayBuilder<AssemblyIdentity>.GetInstance(reader.AssemblyReferences.Count);
            try
            {
                foreach (var assemblyRef in reader.AssemblyReferences)
                {
                    AssemblyReference reference = reader.GetAssemblyReference(assemblyRef);
                    result.Add(CreateAssemblyIdentityOrThrow(
                        reader,
                        reference.Version,
                        reference.Flags,
                        reference.PublicKeyOrToken,
                        reference.Name,
                        reference.Culture,
                        isReference: true));
                }

                return result.ToImmutable();
            }
            finally
            {
                result.Free();
            }
        }

        #endregion

        #region PE Header helpers

        internal string MetadataVersion
        {
            get { return MetadataReader.MetadataVersion; }
        }

        #endregion

        #region Heaps

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        internal BlobReader GetMemoryReaderOrThrow(BlobHandle blob)
        {
            return MetadataReader.GetBlobReader(blob);
        }

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        internal string GetFullNameOrThrow(Handle namespaceHandle, StringHandle nameHandle)
        {
            Debug.Assert(namespaceHandle.Kind == HandleKind.String || namespaceHandle.Kind == HandleKind.NamespaceDefinition);

            var attributeTypeName = MetadataReader.GetString(nameHandle);
            var attributeTypeNamespaceName = namespaceHandle.Kind == HandleKind.String
                ? MetadataReader.GetString((StringHandle)namespaceHandle)
                : MetadataReader.GetString((NamespaceDefinitionHandle)namespaceHandle);

            return MetadataHelpers.BuildQualifiedName(attributeTypeNamespaceName, attributeTypeName);
        }

        #endregion

        #region AssemblyDef helpers

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        internal AssemblyIdentity ReadAssemblyIdentityOrThrow()
        {
            if (!MetadataReader.IsAssembly)
            {
                return null;
            }

            var assemblyDef = MetadataReader.GetAssemblyDefinition();

            return CreateAssemblyIdentityOrThrow(
                MetadataReader,
                assemblyDef.Version,
                assemblyDef.Flags,
                assemblyDef.PublicKey,
                assemblyDef.Name,
                assemblyDef.Culture,
                isReference: false);
        }

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        private static AssemblyIdentity CreateAssemblyIdentityOrThrow(
            MetadataReader reader,
            Version version,
            AssemblyFlags flags,
            BlobHandle publicKey,
            StringHandle name,
            StringHandle culture,
            bool isReference)
        {
            string nameStr = reader.GetString(name);
            if (!MetadataHelpers.IsValidMetadataIdentifier(nameStr))
            {
                throw new BadImageFormatException(string.Format(CodeAnalysisResources.InvalidAssemblyName, nameStr));
            }

            string cultureName = (!culture.IsNil) ? reader.GetString(culture) : null;
            if (cultureName != null && !MetadataHelpers.IsValidMetadataIdentifier(cultureName))
            {
                throw new BadImageFormatException(string.Format(CodeAnalysisResources.InvalidCultureName, cultureName));
            }

            var hasPublicKey = (flags & AssemblyFlags.PublicKey) != 0;
            var publicKeyOrToken = !publicKey.IsNil ? reader.GetBlobBytes(publicKey).AsImmutableOrNull() : default(ImmutableArray<byte>);
            if (hasPublicKey)
            {
                if (!MetadataHelpers.IsValidPublicKey(publicKeyOrToken))
                {
                    throw new BadImageFormatException(CodeAnalysisResources.InvalidPublicKey);
                }
            }
            else if (isReference)
            {
                if (!publicKeyOrToken.IsDefaultOrEmpty && publicKeyOrToken.Length != AssemblyIdentity.PublicKeyTokenSize)
                {
                    throw new BadImageFormatException(CodeAnalysisResources.InvalidPublicKeyToken);
                }
            }
            else
            {
                // Assembly definitions do not contain public key tokens, but they may contain public key
                // data without being marked as strong name signed (e.g. delay-signed assemblies).
                publicKeyOrToken = default(ImmutableArray<byte>);
            }

            return new AssemblyIdentity(
                name: nameStr,
                version: version,
                cultureName: cultureName,
                publicKeyOrToken: publicKeyOrToken,
                hasPublicKey: hasPublicKey,
                isRetargetable: (flags & AssemblyFlags.Retargetable) != 0,
                contentType: (AssemblyContentType)((int)(flags & AssemblyFlags.ContentTypeMask) >> 9),
                noThrow: true);
        }

        #endregion

        #region TypeDef helpers

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        public TypeDefinitionHandle GetContainingTypeOrThrow(TypeDefinitionHandle typeDef)
        {
            return MetadataReader.GetTypeDefinition(typeDef).GetDeclaringType();
        }

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        public string GetTypeDefNameOrThrow(TypeDefinitionHandle typeDef)
        {
            TypeDefinition typeDefinition = MetadataReader.GetTypeDefinition(typeDef);
            string name = MetadataReader.GetString(typeDefinition.Name);
            Debug.Assert(name.Length == 0 || MetadataHelpers.IsValidMetadataIdentifier(name)); // Obfuscated assemblies can have types with empty names.

            // The problem is that the mangled name for an static machine type looks like 
            // "<" + methodName + ">d__" + uniqueId.However, methodName will have dots in 
            // it for explicit interface implementations (e.g. "<I.F>d__0").  Unfortunately, 
            // the native compiler emits such names in a very strange way: everything before 
            // the last dot goes in the namespace (!!) field of the typedef.Since state
            // machine types are always nested types and since nested types never have 
            // explicit namespaces (since they are in the same namespaces as their containing
            // types), it should be safe to check for a non-empty namespace name on a nested
            // type and prepend the namespace name and a dot to the type name.  After that, 
            // debugging support falls out.
            if (IsNestedTypeDefOrThrow(typeDef))
            {
                string namespaceName = MetadataReader.GetString(typeDefinition.Namespace);
                if (namespaceName.Length > 0)
                {
                    // As explained above, this is not really the qualified name - the namespace
                    // name is actually the part of the name that preceded the last dot (in bad
                    // metadata).
                    name = namespaceName + "." + name;
                }
            }

            return name;
        }

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        public string GetTypeDefNamespaceOrThrow(TypeDefinitionHandle typeDef)
        {
            return MetadataReader.GetString(MetadataReader.GetTypeDefinition(typeDef).Namespace);
        }

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        public Handle GetTypeDefExtendsOrThrow(TypeDefinitionHandle typeDef)
        {
            return MetadataReader.GetTypeDefinition(typeDef).BaseType;
        }

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        public TypeAttributes GetTypeDefFlagsOrThrow(TypeDefinitionHandle typeDef)
        {
            return MetadataReader.GetTypeDefinition(typeDef).Attributes;
        }

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        public GenericParameterHandleCollection GetTypeDefGenericParamsOrThrow(TypeDefinitionHandle typeDef)
        {
            return MetadataReader.GetTypeDefinition(typeDef).GetGenericParameters();
        }

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        public bool HasGenericParametersOrThrow(TypeDefinitionHandle typeDef)
        {
            return MetadataReader.GetTypeDefinition(typeDef).GetGenericParameters().Count > 0;
        }

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        public void GetTypeDefPropsOrThrow(
            TypeDefinitionHandle typeDef,
            out string name,
            out string @namespace,
            out TypeAttributes flags,
            out Handle extends)
        {
            TypeDefinition row = MetadataReader.GetTypeDefinition(typeDef);
            name = MetadataReader.GetString(row.Name);
            @namespace = MetadataReader.GetString(row.Namespace);
            flags = row.Attributes;
            extends = row.BaseType;
        }

        internal TypeDefinitionHandle FindSystemObjectTypeDef()
        {
            MetadataReader reader = MetadataReader;

            foreach (TypeDefinitionHandle handle in reader.TypeDefinitions)
            {
                try
                {
                    var typeDef = reader.GetTypeDefinition(handle);

                    if (typeDef.BaseType.IsNil &&
                        (typeDef.Attributes & (TypeAttributes.Public | TypeAttributes.Interface)) == TypeAttributes.Public &&
                        IsSystemObjectOrThrow(typeDef))
                    {
                        return handle;
                    }
                }
                catch (BadImageFormatException)
                { }
            }

            return default(TypeDefinitionHandle);
        }

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        private bool IsSystemObjectOrThrow(TypeDefinition typeDef)
        {
            return MetadataReader.StringComparer.Equals(typeDef.Name, "Object") &&
                   MetadataReader.StringComparer.Equals(typeDef.Namespace, "System");
        }

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        internal bool IsNestedTypeDefOrThrow(TypeDefinitionHandle typeDef)
        {
            return IsNestedTypeDefOrThrow(MetadataReader, typeDef);
        }

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        private static bool IsNestedTypeDefOrThrow(MetadataReader metadataReader, TypeDefinitionHandle typeDef)
        {
            return IsNested(metadataReader.GetTypeDefinition(typeDef).Attributes);
        }

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        internal bool IsInterfaceOrThrow(TypeDefinitionHandle typeDef)
        {
            return MetadataReader.GetTypeDefinition(typeDef).Attributes.IsInterface();
        }

        private struct TypeDefToNamespace
        {
            internal readonly TypeDefinitionHandle TypeDef;
            internal readonly NamespaceDefinitionHandle NamespaceHandle;

            internal TypeDefToNamespace(TypeDefinitionHandle typeDef, NamespaceDefinitionHandle namespaceHandle)
            {
                TypeDef = typeDef;
                NamespaceHandle = namespaceHandle;
            }
        }

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        private IEnumerable<TypeDefToNamespace> GetTypeDefsOrThrow(bool topLevelOnly)
        {
            foreach (var typeDef in MetadataReader.TypeDefinitions)
            {
                var row = MetadataReader.GetTypeDefinition(typeDef);

                if (topLevelOnly && IsNested(row.Attributes))
                {
                    continue;
                }

                yield return new TypeDefToNamespace(typeDef, row.Namespace);
            }
        }

        /// <summary>
        /// The function groups types defined in the module by their fully-qualified namespace name.
        /// The case-sensitivity of the grouping depends upon the provided StringComparer.
        /// 
        /// The sequence is sorted by name by using provided comparer. Therefore, if there are multiple 
        /// groups for a namespace name (e.g. because they differ in case), the groups are going to be 
        /// adjacent to each other. 
        /// 
        /// Empty string is used as namespace name for types in the Global namespace. Therefore, all types 
        /// in the Global namespace, if any, should be in the first group (assuming a reasonable StringComparer).
        /// </summary>
        /// Comparer to sort the groups.
        /// <param name="nameComparer">
        /// </param>
        /// <returns>A sorted list of TypeDef row ids, grouped by fully-qualified namespace name.</returns>
        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        internal IEnumerable<IGrouping<string, TypeDefinitionHandle>> GroupTypesByNamespaceOrThrow(StringComparer nameComparer)
        {
            // TODO: Consider if we should cache the result (not the IEnumerable, but the actual values).

            // NOTE:  Rather than use a sorted dictionary, we accumulate the groupings in a normal dictionary
            // and then sort the list.  We do this so that namespaces with distinct names are not
            // merged, even if they are equal according to the provided comparer.  This improves the error
            // experience because types retain their exact namespaces.

            Dictionary<string, ArrayBuilder<TypeDefinitionHandle>> namespaces = new Dictionary<string, ArrayBuilder<TypeDefinitionHandle>>();

            GetTypeNamespaceNamesOrThrow(namespaces);
            GetForwardedTypeNamespaceNamesOrThrow(namespaces);

            var result = new ArrayBuilder<IGrouping<string, TypeDefinitionHandle>>();

            foreach (var pair in namespaces)
            {
                result.Add(new Grouping<string, TypeDefinitionHandle>(pair.Key, pair.Value ?? SpecializedCollections.EmptyEnumerable<TypeDefinitionHandle>()));
            }

            result.Sort(new TypesByNamespaceSortComparer(nameComparer));
            return result;
        }

        private class TypesByNamespaceSortComparer : IComparer<IGrouping<string, TypeDefinitionHandle>>
        {
            private readonly StringComparer nameComparer;

            public TypesByNamespaceSortComparer(StringComparer nameComparer)
            {
                this.nameComparer = nameComparer;
            }

            public int Compare(IGrouping<string, TypeDefinitionHandle> left, IGrouping<string, TypeDefinitionHandle> right)
            {
                if (left == right)
                {
                    return 0;
                }

                int result = nameComparer.Compare(left.Key, right.Key);

                if (result == 0)
                {
                    var fLeft = left.FirstOrDefault();
                    var fRight = right.FirstOrDefault();

                    if (fLeft.IsNil ^ fRight.IsNil)
                    {
                        result = fLeft.IsNil ? +1 : -1;
                    }
                    else
                    {
                        result = HandleComparer.Default.Compare(fLeft, fRight);
                    }

                    if (result == 0)
                    {
                        // This can only happen when both are for forwarded types.
                        Debug.Assert(left.IsEmpty() && right.IsEmpty());
                        result = string.CompareOrdinal(left.Key, right.Key);
                    }
                }

                Debug.Assert(result != 0);
                return result;
            }
        }

        /// <summary>
        /// Groups together the RowIds of types in a given namespaces.  The types considered are
        /// those defined in this module.
        /// </summary>
        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        private void GetTypeNamespaceNamesOrThrow(Dictionary<string, ArrayBuilder<TypeDefinitionHandle>> namespaces)
        {
            // PERF: Group by namespace handle so we only have to allocate one string for every namespace
            var namespaceHandles = new Dictionary<NamespaceDefinitionHandle, ArrayBuilder<TypeDefinitionHandle>>(NamespaceHandleEqualityComparer.Singleton);
            foreach (TypeDefToNamespace pair in GetTypeDefsOrThrow(topLevelOnly: true))
            {
                NamespaceDefinitionHandle nsHandle = pair.NamespaceHandle;
                TypeDefinitionHandle typeDef = pair.TypeDef;

                ArrayBuilder<TypeDefinitionHandle> builder;

                if (namespaceHandles.TryGetValue(nsHandle, out builder))
                {
                    builder.Add(typeDef);
                }
                else
                {
                    namespaceHandles.Add(nsHandle, new ArrayBuilder<TypeDefinitionHandle> { typeDef });
                }
            }

            foreach (var kvp in namespaceHandles)
            {
                string @namespace = MetadataReader.GetString(kvp.Key);

                ArrayBuilder<TypeDefinitionHandle> builder;

                if (namespaces.TryGetValue(@namespace, out builder))
                {
                    builder.AddRange(kvp.Value);
                }
                else
                {
                    namespaces.Add(@namespace, kvp.Value);
                }
            }
        }

        private class NamespaceHandleEqualityComparer : IEqualityComparer<NamespaceDefinitionHandle>
        {
            public static readonly NamespaceHandleEqualityComparer Singleton = new NamespaceHandleEqualityComparer();

            private NamespaceHandleEqualityComparer()
            {
            }

            public bool Equals(NamespaceDefinitionHandle x, NamespaceDefinitionHandle y)
            {
                return x == y;
            }

            public int GetHashCode(NamespaceDefinitionHandle obj)
            {
                return obj.GetHashCode();
            }
        }

        /// <summary>
        /// Supplements the namespace-to-RowIDs map with the namespaces of forwarded types.
        /// These types will not have associated row IDs (represented as null, for efficiency).
        /// These namespaces are important because we want lookups of missing forwarded types
        /// to succeed far enough that we can actually find the type forwarder and provide
        /// information about the target assembly.
        /// 
        /// For example, consider the following forwarded type:
        /// 
        /// .class extern forwarder Namespace.Type {}
        /// 
        /// If this type is referenced in source as "Namespace.Type", then dev10 reports
        /// 
        /// error CS1070: The type name 'Namespace.Name' could not be found. This type has been 
        /// forwarded to assembly 'pe2, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null'. 
        /// Consider adding a reference to that assembly.
        /// 
        /// If we did not include "Namespace" as a child of the global namespace of this module
        /// (the forwarding module), then Roslyn would report that the type "Namespace" was not
        /// found and say nothing about "Name" (because of the diagnostic already attached to 
        /// the qualifier).
        /// </summary>
        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        private void GetForwardedTypeNamespaceNamesOrThrow(Dictionary<string, ArrayBuilder<TypeDefinitionHandle>> namespaces)
        {
            EnsureForwardTypeToAssemblyMap();

            foreach (var typeName in lazyForwardedTypesToAssemblyMap.Keys)
            {
                int index = typeName.LastIndexOf('.');
                string namespaceName = index >= 0 ? typeName.Substring(0, index) : "";
                if (!namespaces.ContainsKey(namespaceName))
                {
                    namespaces.Add(namespaceName, null);
                }
            }
        }

        private IdentifierCollection ComputeTypeNameCollection()
        {
            try
            {
                var allTypeDefs = GetTypeDefsOrThrow(topLevelOnly: false);
                var typeNames =
                    from typeDef in allTypeDefs
                    let metadataName = GetTypeDefNameOrThrow(typeDef.TypeDef)
                    let backtickIndex = metadataName.IndexOf('`')
                    select backtickIndex < 0 ? metadataName : metadataName.Substring(0, backtickIndex);

                return new IdentifierCollection(typeNames);
            }
            catch (BadImageFormatException)
            {
                return new IdentifierCollection();
            }
        }

        private IdentifierCollection ComputeNamespaceNameCollection()
        {
            try
            {
                var allTypeIds = GetTypeDefsOrThrow(topLevelOnly: true);
                var fullNamespaceNames =
                    from id in allTypeIds
                    where !id.NamespaceHandle.IsNil
                    select MetadataReader.GetString(id.NamespaceHandle);

                var namespaceNames =
                    from fullName in fullNamespaceNames.Distinct()
                    from name in fullName.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries)
                    select name;

                return new IdentifierCollection(namespaceNames);
            }
            catch (BadImageFormatException)
            {
                return new IdentifierCollection();
            }
        }

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        internal ImmutableArray<TypeDefinitionHandle> GetNestedTypeDefsOrThrow(TypeDefinitionHandle container)
        {
            return MetadataReader.GetTypeDefinition(container).GetNestedTypes();
        }

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        internal MethodImplementationHandleCollection GetMethodImplementationsOrThrow(TypeDefinitionHandle typeDef)
        {
            return MetadataReader.GetTypeDefinition(typeDef).GetMethodImplementations();
        }

        /// <summary>
        /// Returns a collection of interfaces implemented by given type.
        /// </summary>
        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        internal InterfaceImplementationHandleCollection GetInterfaceImplementationsOrThrow(TypeDefinitionHandle typeDef)
        {
            return MetadataReader.GetTypeDefinition(typeDef).GetInterfaceImplementations();
        }

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        internal MethodDefinitionHandleCollection GetMethodsOfTypeOrThrow(TypeDefinitionHandle typeDef)
        {
            return MetadataReader.GetTypeDefinition(typeDef).GetMethods();
        }

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        internal PropertyDefinitionHandleCollection GetPropertiesOfTypeOrThrow(TypeDefinitionHandle typeDef)
        {
            return MetadataReader.GetTypeDefinition(typeDef).GetProperties();
        }

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        internal EventDefinitionHandleCollection GetEventsOfTypeOrThrow(TypeDefinitionHandle typeDef)
        {
            return MetadataReader.GetTypeDefinition(typeDef).GetEvents();
        }

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        internal FieldDefinitionHandleCollection GetFieldsOfTypeOrThrow(TypeDefinitionHandle typeDef)
        {
            return MetadataReader.GetTypeDefinition(typeDef).GetFields();
        }

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        internal Handle GetBaseTypeOfTypeOrThrow(TypeDefinitionHandle typeDef)
        {
            return MetadataReader.GetTypeDefinition(typeDef).BaseType;
        }

        internal TypeLayout GetTypeLayout(TypeDefinitionHandle typeDef)
        {
            try
            {
                // CLI Spec 22.8.3:
                // The Class or ValueType indexed by Parent shall be SequentialLayout or ExplicitLayout. 
                // That is, AutoLayout types shall not own any rows in the ClassLayout table.
                var def = MetadataReader.GetTypeDefinition(typeDef);

                LayoutKind kind;
                switch (def.Attributes & TypeAttributes.LayoutMask)
                {
                    case TypeAttributes.SequentialLayout:
                        kind = LayoutKind.Sequential;
                        break;

                    case TypeAttributes.ExplicitLayout:
                        kind = LayoutKind.Explicit;
                        break;

                    case TypeAttributes.AutoLayout:
                        return default(TypeLayout);

                    default:
                        // TODO (tomat) report error:
                        return default(TypeLayout);
                }

                var layout = def.GetLayout();
                int size = layout.Size;
                int packingSize = layout.PackingSize;

                if (packingSize > byte.MaxValue)
                {
                    // TODO (tomat) report error:
                    packingSize = 0;
                }

                if (size < 0)
                {
                    // TODO (tomat) report error:
                    size = 0;
                }

                return new TypeLayout(kind, size, (byte)packingSize);
            }
            catch (BadImageFormatException)
            {
                return default(TypeLayout);
            }
        }

        internal bool IsNoPiaLocalType(TypeDefinitionHandle typeDef)
        {
            AttributeInfo attributeInfo;
            return IsNoPiaLocalType(typeDef, out attributeInfo);
        }

        internal bool HasParamsAttribute(Handle token)
        {
            return FindTargetAttribute(token, AttributeDescription.ParamArrayAttribute).HasValue;
        }

        internal bool HasExtensionAttribute(Handle token, bool ignoreCase)
        {
            return FindTargetAttribute(token, ignoreCase ? AttributeDescription.CaseInsensitiveExtensionAttribute : AttributeDescription.CaseSensitiveExtensionAttribute).HasValue;
        }

        internal bool HasFSharpInterfaceDataVersionAttribute(Handle token)
        {
            return FindTargetAttribute(token, AttributeDescription.FSharpInterfaceDataVersionAttribute).HasValue;
        }

        internal bool HasVisualBasicEmbeddedAttribute(Handle token)
        {
            return FindTargetAttribute(token, AttributeDescription.VisualBasicEmbeddedAttribute).HasValue;
        }

        internal bool HasDefaultMemberAttribute(Handle token, out string memberName)
        {
            return HasStringValuedAttribute(token, AttributeDescription.DefaultMemberAttribute, out memberName);
        }

        internal bool HasGuidAttribute(Handle token, out string guidValue)
        {
            return HasStringValuedAttribute(token, AttributeDescription.GuidAttribute, out guidValue);
        }

        internal bool HasFixedBufferAttribute(Handle token, out string elementTypeName, out int bufferSize)
        {
            return HasStringAndIntValuedAttribute(token, AttributeDescription.FixedBufferAttribute, out elementTypeName, out bufferSize);
        }

        internal bool HasAccessedThroughPropertyAttribute(Handle token, out string propertyName)
        {
            return HasStringValuedAttribute(token, AttributeDescription.AccessedThroughPropertyAttribute, out propertyName);
        }

        internal bool HasRequiredAttributeAttribute(Handle token)
        {
            return FindTargetAttribute(token, AttributeDescription.RequiredAttributeAttribute).HasValue;
        }

        internal bool HasAttribute(Handle token, AttributeDescription description)
        {
            return FindTargetAttribute(token, description).HasValue;
        }

        internal CustomAttributeHandle GetAttributeHandle(Handle token, AttributeDescription description)
        {
            return FindTargetAttribute(token, description).Handle;
        }

        private static readonly ImmutableArray<bool> SimpleDynamicTransforms = ImmutableArray.Create<bool>(true);

        internal bool HasDynamicAttribute(Handle token, out ImmutableArray<bool> dynamicTransforms)
        {
            AttributeInfo info = FindTargetAttribute(token, AttributeDescription.DynamicAttribute);
            Debug.Assert(!info.HasValue || info.SignatureIndex == 0 || info.SignatureIndex == 1);

            if (!info.HasValue)
            {
                dynamicTransforms = default(ImmutableArray<bool>);
                return false;
            }

            if (info.SignatureIndex == 0)
            {
                dynamicTransforms = SimpleDynamicTransforms;
                return true;
            }

            return TryExtractBoolArrayValueFromAttribute(info.Handle, out dynamicTransforms);
        }

        internal bool HasDeprecatedOrObsoleteAttribute(Handle token, out ObsoleteAttributeData obsoleteData)
        {
            AttributeInfo info;

            info = FindTargetAttribute(token, AttributeDescription.DeprecatedAttribute);
            if (info.HasValue)
            {
                return TryExtractDeprecatedDataFromAttribute(info, out obsoleteData);
            }

            info = FindTargetAttribute(token, AttributeDescription.ObsoleteAttribute);
            if (info.HasValue)
            {
                return TryExtractObsoleteDataFromAttribute(info, out obsoleteData);
            }

            obsoleteData = null;
            return false;
        }

        internal CustomAttributeHandle GetAttributeUsageAttributeHandle(Handle token)
        {
            AttributeInfo info = FindTargetAttribute(token, AttributeDescription.AttributeUsageAttribute);
            Debug.Assert(info.SignatureIndex == 0);
            return info.Handle;
        }

        internal bool HasInterfaceTypeAttribute(Handle token, out ComInterfaceType interfaceType)
        {
            AttributeInfo info = FindTargetAttribute(token, AttributeDescription.InterfaceTypeAttribute);
            if (info.HasValue && TryExtractInterfaceTypeFromAttribute(info, out interfaceType))
            {
                return true;
            }

            interfaceType = default(ComInterfaceType);
            return false;
        }

        internal bool HasTypeLibTypeAttribute(Handle token, out Cci.TypeLibTypeFlags flags)
        {
            AttributeInfo info = FindTargetAttribute(token, AttributeDescription.TypeLibTypeAttribute);
            if (info.HasValue && TryExtractTypeLibTypeFromAttribute(info, out flags))
            {
                return true;
            }

            flags = default(Cci.TypeLibTypeFlags);
            return false;
        }

        internal bool HasDateTimeConstantAttribute(Handle token, out ConstantValue defaultValue)
        {
            long value;
            AttributeInfo info = FindLastTargetAttribute(token, AttributeDescription.DateTimeConstantAttribute);
            if (info.HasValue && TryExtractLongValueFromAttribute(info.Handle, out value))
            {
                defaultValue = ConstantValue.Create(new DateTime(value));
                return true;
            }

            defaultValue = null;
            return false;
        }

        internal bool HasDecimalConstantAttribute(Handle token, out ConstantValue defaultValue)
        {
            decimal value;
            AttributeInfo info = FindLastTargetAttribute(token, AttributeDescription.DecimalConstantAttribute);
            if (info.HasValue && TryExtractDecimalValueFromDecimalConstantAttribute(info.Handle, out value))
            {
                defaultValue = ConstantValue.Create(value);
                return true;
            }

            defaultValue = null;
            return false;
        }

        internal ImmutableArray<string> GetInternalsVisibleToAttributeValues(Handle token)
        {
            List<AttributeInfo> attrInfos = FindTargetAttributes(token, AttributeDescription.InternalsVisibleToAttribute);
            ArrayBuilder<string> result = ExtractStringValuesFromAttributes(attrInfos);
            return result != null ? result.ToImmutableAndFree() : ImmutableArray<string>.Empty;
        }

        internal ImmutableArray<string> GetConditionalAttributeValues(Handle token)
        {
            List<AttributeInfo> attrInfos = FindTargetAttributes(token, AttributeDescription.ConditionalAttribute);
            ArrayBuilder<string> result = ExtractStringValuesFromAttributes(attrInfos);

            ImmutableArray<string> list;
            if (result != null)
            {
                list = result.ToImmutableAndFree();
            }
            else
            {
                list = ImmutableArray<string>.Empty;
            }

            return list;
        }

        // This method extracts all the non-null string values from the given attributes.
        private ArrayBuilder<string> ExtractStringValuesFromAttributes(List<AttributeInfo> attrInfos)
        {
            if (attrInfos == null)
            {
                return null;
            }

            var result = ArrayBuilder<string>.GetInstance(attrInfos.Count);

            foreach (var ai in attrInfos)
            {
                string extractedStr;
                if (TryExtractStringValueFromAttribute(ai.Handle, out extractedStr) && extractedStr != null)
                {
                    result.Add(extractedStr);
                }
            }

            return result;
        }

        private bool TryExtractObsoleteDataFromAttribute(AttributeInfo attributeInfo, out ObsoleteAttributeData obsoleteData)
        {
            Debug.Assert(attributeInfo.HasValue);

            switch (attributeInfo.SignatureIndex)
            {
                case 0:
                    // ObsoleteAttribute()
                    obsoleteData = new ObsoleteAttributeData(message: null, isError: false);
                    return true;

                case 1:
                    // ObsoleteAttribute(string)
                    string message;
                    if (TryExtractStringValueFromAttribute(attributeInfo.Handle, out message))
                    {
                        obsoleteData = new ObsoleteAttributeData(message, isError: false);
                        return true;
                    }

                    obsoleteData = null;
                    return false;

                case 2:
                    // ObsoleteAttribute(string, bool)
                    return TryExtractValueFromAttribute<ObsoleteAttributeData>(attributeInfo.Handle, out obsoleteData, AttributeObsoleteDataExtractor);

                default:
                    Debug.Assert(false, "unexpected ObsoleteAttribute signature");
                    obsoleteData = null;
                    return false;
            }
        }

        private bool TryExtractDeprecatedDataFromAttribute(AttributeInfo attributeInfo, out ObsoleteAttributeData obsoleteData)
        {
            Debug.Assert(attributeInfo.HasValue);

            switch (attributeInfo.SignatureIndex)
            {
                case 0: // DeprecatedAttribute(String, DeprecationType, UInt32) 
                case 1: // DeprecatedAttribute(String, DeprecationType, UInt32, Platform) 
                case 2: // DeprecatedAttribute(String, DeprecationType, UInt32, Type) 
                    return TryExtractValueFromAttribute<ObsoleteAttributeData>(attributeInfo.Handle, out obsoleteData, AttributeDeprecatedDataExtractor);

                default:
                    Debug.Assert(false, "unexpected DeprecatedAttribute signature");
                    obsoleteData = null;
                    return false;
            }
        }

        private bool TryExtractInterfaceTypeFromAttribute(AttributeInfo attributeInfo, out ComInterfaceType interfaceType)
        {
            Debug.Assert(attributeInfo.HasValue);

            switch (attributeInfo.SignatureIndex)
            {
                case 0:
                    // InterfaceTypeAttribute(Int16)
                    short shortValue;
                    if (TryExtractValueFromAttribute<short>(attributeInfo.Handle, out shortValue, AttributeShortValueExtractor) &&
                        IsValidComInterfaceType(shortValue))
                    {
                        interfaceType = (ComInterfaceType)shortValue;
                        return true;
                    }
                    break;

                case 1:
                    // InterfaceTypeAttribute(ComInterfaceType)
                    int intValue;
                    if (TryExtractValueFromAttribute<int>(attributeInfo.Handle, out intValue, AttributeIntValueExtractor) &&
                        IsValidComInterfaceType(intValue))
                    {
                        interfaceType = (ComInterfaceType)intValue;
                        return true;
                    }
                    break;

                default:
                    Debug.Assert(false, "unexpected InterfaceTypeAttribute signature");
                    interfaceType = 0;
                    return false;
            }

            interfaceType = default(ComInterfaceType);
            return false;
        }

        private bool IsValidComInterfaceType(int comInterfaceType)
        {
            switch (comInterfaceType)
            {
                case (int)ComInterfaceType.InterfaceIsDual:
                case (int)ComInterfaceType.InterfaceIsIDispatch:
                case (int)ComInterfaceType.InterfaceIsIInspectable:
                case (int)ComInterfaceType.InterfaceIsIUnknown:
                    return true;

                default:
                    return false;
            }
        }

        private bool TryExtractTypeLibTypeFromAttribute(AttributeInfo info, out Cci.TypeLibTypeFlags flags)
        {
            Debug.Assert(info.HasValue);

            switch (info.SignatureIndex)
            {
                case 0:
                    // TypeLibTypeAttribute(Int16)
                    short shortValue;
                    if (TryExtractValueFromAttribute<short>(info.Handle, out shortValue, AttributeShortValueExtractor))
                    {
                        flags = (Cci.TypeLibTypeFlags)shortValue;
                        return true;
                    }
                    break;

                case 1:
                    // TypeLibTypeAttribute(TypeLibTypeFlags)
                    int intValue;
                    if (TryExtractValueFromAttribute<int>(info.Handle, out intValue, AttributeIntValueExtractor))
                    {
                        flags = (Cci.TypeLibTypeFlags)intValue;
                        return true;
                    }
                    break;

                default:
                    Debug.Assert(false, "unexpected TypeLibAttribute signature");
                    flags = 0;
                    return false;
            }

            flags = default(Cci.TypeLibTypeFlags);
            return false;
        }

        private bool TryExtractStringValueFromAttribute(CustomAttributeHandle handle, out string value)
        {
            return TryExtractValueFromAttribute<string>(handle, out value, AttributeStringValueExtractor);
        }

        private bool TryExtractLongValueFromAttribute(CustomAttributeHandle handle, out long value)
        {
            return TryExtractValueFromAttribute<long>(handle, out value, AttributeLongValueExtractor);
        }

        // Note: not a general purpose helper
        private bool TryExtractDecimalValueFromDecimalConstantAttribute(CustomAttributeHandle handle, out decimal value)
        {
            return TryExtractValueFromAttribute<decimal>(handle, out value, DecimalValueInDecimalConstantAttributeExtractor);
        }

        private struct StringAndInt
        {
            public string StringValue;
            public int IntValue;
        }

        private bool TryExtractStringAndIntValueFromAttribute(CustomAttributeHandle handle, out string stringValue, out int intValue)
        {
            StringAndInt data;
            var result = TryExtractValueFromAttribute<StringAndInt>(handle, out data, AttributeStringAndIntValueExtractor);
            stringValue = data.StringValue;
            intValue = data.IntValue;
            return result;
        }

        private bool TryExtractBoolArrayValueFromAttribute(CustomAttributeHandle handle, out ImmutableArray<bool> value)
        {
            return TryExtractValueFromAttribute<ImmutableArray<bool>>(handle, out value, AttributeBoolArrayValueExtractor);
        }

        private bool TryExtractValueFromAttribute<T>(CustomAttributeHandle handle, out T value, AttributeValueExtractor<T> valueExtractor)
        {
            Debug.Assert(!handle.IsNil);

            // extract the value
            try
            {
                BlobHandle valueBlob = GetCustomAttributeValueOrThrow(handle);

                if (!valueBlob.IsNil)
                {
                    // TODO: error checking offset in range
                    BlobReader reader = MetadataReader.GetBlobReader(valueBlob);

                    if (reader.Length > 4)
                    {
                        // check prolog
                        if (reader.ReadByte() == 1 && reader.ReadByte() == 0)
                        {
                            return valueExtractor(out value, ref reader);
                        }
                    }
                }
            }
            catch (BadImageFormatException)
            { }

            value = default(T);
            return false;
        }

        internal bool HasStringValuedAttribute(Handle token, AttributeDescription description, out string value)
        {
            AttributeInfo info = FindTargetAttribute(token, description);
            if (info.HasValue)
            {
                return TryExtractStringValueFromAttribute(info.Handle, out value);
            }

            value = null;
            return false;
        }

        private bool HasStringAndIntValuedAttribute(Handle token, AttributeDescription description, out string stringValue, out int intValue)
        {
            AttributeInfo info = FindTargetAttribute(token, description);
            if (info.HasValue)
            {
                return TryExtractStringAndIntValueFromAttribute(info.Handle, out stringValue, out intValue);
            }

            stringValue = null;
            intValue = 0;
            return false;
        }

        internal bool IsNoPiaLocalType(
            TypeDefinitionHandle typeDef,
            out string interfaceGuid,
            out string scope,
            out string identifier)
        {
            AttributeInfo typeIdentifierInfo;

            if (!IsNoPiaLocalType(typeDef, out typeIdentifierInfo))
            {
                interfaceGuid = null;
                scope = null;
                identifier = null;

                return false;
            }

            interfaceGuid = null;
            scope = null;
            identifier = null;

            try
            {
                if (GetTypeDefFlagsOrThrow(typeDef).IsInterface())
                {
                    HasGuidAttribute(typeDef, out interfaceGuid);
                }

                if (typeIdentifierInfo.SignatureIndex == 1)
                {
                    // extract the value
                    BlobHandle valueBlob = GetCustomAttributeValueOrThrow(typeIdentifierInfo.Handle);

                    if (!valueBlob.IsNil)
                    {
                        BlobReader reader = MetadataReader.GetBlobReader(valueBlob);

                        if (reader.Length > 4)
                        {
                            // check prolog
                            if (reader.ReadInt16() == 1)
                            {
                                if (!CrackStringInAttributeValue(out scope, ref reader) ||
                                    !CrackStringInAttributeValue(out identifier, ref reader))
                                {
                                    return false;
                                }
                            }
                        }
                    }
                }

                return true;
            }
            catch (BadImageFormatException)
            {
                return false;
            }
        }

        internal static bool CrackObsoleteAttributeData(out ObsoleteAttributeData value, ref BlobReader sig)
        {
            string message;
            if (CrackStringInAttributeValue(out message, ref sig) && sig.RemainingBytes >= 1)
            {
                bool isError = sig.ReadBoolean();
                value = new ObsoleteAttributeData(message, isError);
                return true;
            }

            value = null;
            return false;
        }

        internal static bool CrackDeprecatedAttributeData(out ObsoleteAttributeData value, ref BlobReader sig)
        {
            StringAndInt args;
            if (CrackStringAndIntInAttributeValue(out args, ref sig))
            {
                value = new ObsoleteAttributeData(args.StringValue, args.IntValue == 1);
                return true;
            }

            value = null;
            return false;
        }

        private static bool CrackStringAndIntInAttributeValue(out StringAndInt value, ref BlobReader sig)
        {
            value = default(StringAndInt);
            return
                CrackStringInAttributeValue(out value.StringValue, ref sig) &&
                CrackIntInAttributeValue(out value.IntValue, ref sig);
        }

        internal static bool CrackStringInAttributeValue(out string value, ref BlobReader sig)
        {
            try
            {
                int strLen;
                if (sig.TryReadCompressedInteger(out strLen) && sig.RemainingBytes >= strLen)
                {
                    value = sig.ReadUTF8(strLen);

                    // Trim null characters at the end to mimic native compiler behavior.
                    // There are libraries that have them and leaving them in breaks tests.
                    value = value.TrimEnd('\0');

                    return true;
                }

                value = null;

                // Strings are stored as UTF8, but 0xFF means NULL string.
                return sig.RemainingBytes >= 1 && sig.ReadByte() == 0xFF;
            }
            catch (BadImageFormatException)
            {
                value = null;
                return false;
            }
        }

        internal static bool CrackStringArrayInAttributeValue(out ImmutableArray<string> value, ref BlobReader sig)
        {
            if (sig.RemainingBytes >= 4)
            {
                uint arrayLen = sig.ReadUInt32();
                if (arrayLen >= 0)
                {
                    var stringArray = new string[arrayLen];
                    for (int i = 0; i < arrayLen; i++)
                    {
                        if (!CrackStringInAttributeValue(out stringArray[i], ref sig))
                        {
                            value = stringArray.AsImmutableOrNull();
                            return false;
                        }
                    }

                    value = stringArray.AsImmutableOrNull();
                    return true;
                }
            }

            value = default(ImmutableArray<string>);
            return false;
        }

        internal static bool CrackByteInAttributeValue(out byte value, ref BlobReader sig)
        {
            if (sig.RemainingBytes >= 1)
            {
                value = sig.ReadByte();
                return true;
            }

            value = 0xff;
            return false;
        }

        internal static bool CrackShortInAttributeValue(out short value, ref BlobReader sig)
        {
            if (sig.RemainingBytes >= 2)
            {
                value = sig.ReadInt16();
                return true;
            }

            value = -1;
            return false;
        }

        internal static bool CrackIntInAttributeValue(out int value, ref BlobReader sig)
        {
            if (sig.RemainingBytes >= 4)
            {
                value = sig.ReadInt32();
                return true;
            }

            value = -1;
            return false;
        }

        internal static bool CrackLongInAttributeValue(out long value, ref BlobReader sig)
        {
            if (sig.RemainingBytes >= 8)
            {
                value = sig.ReadInt64();
                return true;
            }

            value = -1;
            return false;
        }

        // Note: not a general purpose helper
        internal static bool CrackDecimalInDecimalConstantAttribute(out decimal value, ref BlobReader sig)
        {
            byte scale;
            byte sign;
            int high;
            int mid;
            int low;

            if (CrackByteInAttributeValue(out scale, ref sig) &&
                CrackByteInAttributeValue(out sign, ref sig) &&
                CrackIntInAttributeValue(out high, ref sig) &&
                CrackIntInAttributeValue(out mid, ref sig) &&
                CrackIntInAttributeValue(out low, ref sig))
            {
                value = new decimal(low, mid, high, sign != 0, scale);
                return true;
            }

            value = -1;
            return false;
        }

        internal static bool CrackBoolArrayInAttributeValue(out ImmutableArray<bool> value, ref BlobReader sig)
        {
            if (sig.RemainingBytes >= 4)
            {
                uint arrayLen = sig.ReadUInt32();
                if (arrayLen >= 0 && sig.RemainingBytes >= arrayLen)
                {
                    var boolArray = new bool[arrayLen];
                    for (int i = 0; i < arrayLen; i++)
                    {
                        boolArray[i] = (sig.ReadByte() == 1);
                    }

                    value = boolArray.AsImmutableOrNull();
                    return true;
                }
            }

            value = default(ImmutableArray<bool>);
            return false;
        }

        internal struct AttributeInfo
        {
            public readonly CustomAttributeHandle Handle;
            public readonly byte SignatureIndex;

            public AttributeInfo(CustomAttributeHandle handle, int signatureIndex)
            {
                Debug.Assert(signatureIndex >= 0 && signatureIndex <= byte.MaxValue);
                this.Handle = handle;
                this.SignatureIndex = (byte)signatureIndex;
            }

            public bool HasValue
            {
                get { return !Handle.IsNil; }
            }
        }

        internal List<AttributeInfo> FindTargetAttributes(Handle hasAttribute, AttributeDescription description)
        {
            List<AttributeInfo> result = null;

            try
            {
                foreach (var attributeHandle in MetadataReader.GetCustomAttributes(hasAttribute))
                {
                    int signatureIndex = GetTargetAttributeSignatureIndex(attributeHandle, description);
                    if (signatureIndex != -1)
                    {
                        if (result == null)
                        {
                            result = new List<AttributeInfo>();
                        }

                        // We found a match
                        result.Add(new AttributeInfo(attributeHandle, signatureIndex));
                    }
                }
            }
            catch (BadImageFormatException)
            { }

            return result;
        }

        private AttributeInfo FindTargetAttribute(Handle hasAttribute, AttributeDescription description)
        {
            return FindTargetAttribute(MetadataReader, hasAttribute, description);
        }

        internal static AttributeInfo FindTargetAttribute(MetadataReader metadataReader, Handle hasAttribute, AttributeDescription description)
        {
            try
            {
                foreach (var attributeHandle in metadataReader.GetCustomAttributes(hasAttribute))
                {
                    int signatureIndex = GetTargetAttributeSignatureIndex(metadataReader, attributeHandle, description);
                    if (signatureIndex != -1)
                    {
                        // We found a match
                        return new AttributeInfo(attributeHandle, signatureIndex);
                    }
                }
            }
            catch (BadImageFormatException)
            { }

            return default(AttributeInfo);
        }

        internal AttributeInfo FindLastTargetAttribute(Handle hasAttribute, AttributeDescription description)
        {
            try
            {
                AttributeInfo attrInfo = default(AttributeInfo);
                foreach (var attributeHandle in MetadataReader.GetCustomAttributes(hasAttribute))
                {
                    int signatureIndex = GetTargetAttributeSignatureIndex(attributeHandle, description);
                    if (signatureIndex != -1)
                    {
                        // We found a match
                        attrInfo = new AttributeInfo(attributeHandle, signatureIndex);
                    }
                }
                return attrInfo;
            }
            catch (BadImageFormatException)
            { }

            return default(AttributeInfo);
        }

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        internal int GetParamArrayCountOrThrow(Handle hasAttribute)
        {
            int count = 0;
            foreach (var attributeHandle in MetadataReader.GetCustomAttributes(hasAttribute))
            {
                if (GetTargetAttributeSignatureIndex(attributeHandle,
                    AttributeDescription.ParamArrayAttribute) != -1)
                {
                    count++;
                }
            }
            return count;
        }

        private bool IsNoPiaLocalType(TypeDefinitionHandle typeDef, out AttributeInfo attributeInfo)
        {
            if (lazyContainsNoPiaLocalTypes == ThreeState.False)
            {
                attributeInfo = default(AttributeInfo);
                return false;
            }

            if (lazyNoPiaLocalTypeCheckBitMap != null &&
                lazyTypeDefToTypeIdentifierMap != null)
            {
                int rid = MetadataReader.GetRowNumber(typeDef);
                Debug.Assert(rid > 0);

                int item = rid / 32;
                int bit = 1 << (rid % 32);

                if ((lazyNoPiaLocalTypeCheckBitMap[item] & bit) != 0)
                {
                    return lazyTypeDefToTypeIdentifierMap.TryGetValue(typeDef, out attributeInfo);
                }
            }

            try
            {
                foreach (var attributeHandle in MetadataReader.GetCustomAttributes(typeDef))
                {
                    int signatureIndex = IsTypeIdentifierAttribute(attributeHandle);
                    if (signatureIndex != -1)
                    {
                        // We found a match
                        lazyContainsNoPiaLocalTypes = ThreeState.True;

                        RegisterNoPiaLocalType(typeDef, attributeHandle, signatureIndex);
                        attributeInfo = new AttributeInfo(attributeHandle, signatureIndex);
                        return true;
                    }
                }
            }
            catch (BadImageFormatException)
            { }

            RecordNoPiaLocalTypeCheck(typeDef);
            attributeInfo = default(AttributeInfo);
            return false;
        }

        private void RegisterNoPiaLocalType(TypeDefinitionHandle typeDef, CustomAttributeHandle customAttribute, int signatureIndex)
        {
            if (lazyNoPiaLocalTypeCheckBitMap == null)
            {
                Interlocked.CompareExchange(
                    ref lazyNoPiaLocalTypeCheckBitMap,
                    new int[(MetadataReader.TypeDefinitions.Count + 32) / 32],
                    null);
            }

            if (lazyTypeDefToTypeIdentifierMap == null)
            {
                Interlocked.CompareExchange(
                    ref lazyTypeDefToTypeIdentifierMap,
                    new ConcurrentDictionary<TypeDefinitionHandle, AttributeInfo>(),
                    null);
            }

            lazyTypeDefToTypeIdentifierMap.TryAdd(typeDef, new AttributeInfo(customAttribute, signatureIndex));

            RecordNoPiaLocalTypeCheck(typeDef);
        }

        private void RecordNoPiaLocalTypeCheck(TypeDefinitionHandle typeDef)
        {
            if (lazyNoPiaLocalTypeCheckBitMap == null)
            {
                return;
            }

            int rid = MetadataTokens.GetRowNumber(typeDef);
            Debug.Assert(rid > 0);
            int item = rid / 32;
            int bit = 1 << (rid % 32);
            int oldValue;

            do
            {
                oldValue = lazyNoPiaLocalTypeCheckBitMap[item];
            }
            while (Interlocked.CompareExchange(
                        ref lazyNoPiaLocalTypeCheckBitMap[item],
                        oldValue | bit,
                        oldValue) != oldValue);
        }

        /// <summary>
        /// Determine if custom attribute application is 
        /// NoPia TypeIdentifier.
        /// </summary>
        /// <returns>
        /// An index of the target constructor signature in 
        /// signaturesOfTypeIdentifierAttribute array, -1 if
        /// this is not NoPia TypeIdentifier.
        /// </returns>
        private int IsTypeIdentifierAttribute(CustomAttributeHandle customAttribute)
        {
            const int No = -1;

            try
            {
                if (MetadataReader.GetCustomAttribute(customAttribute).Parent.Kind != HandleKind.TypeDefinition)
                {
                    // Ignore attributes attached to anything, but type definitions.
                    return No;
                }

                return GetTargetAttributeSignatureIndex(customAttribute, AttributeDescription.TypeIdentifierAttribute);
            }
            catch (BadImageFormatException)
            {
                return No;
            }
        }

        /// <summary>
        /// Determines if a custom attribute matches a namespace and name.
        /// </summary>
        /// <param name="customAttribute">Handle of the custom attribute.</param>
        /// <param name="namespaceName">The custom attribute's namespace in metadata format (case sensitive)</param>
        /// <param name="typeName">The custom attribute's type name in metadata format (case sensitive)</param>
        /// <param name="ctor">Constructor of the custom attribute.</param>
        /// <param name="ignoreCase">Should case be ignored for name comparison?</param>
        /// <returns>true if match is found</returns>
        internal bool IsTargetAttribute(
            CustomAttributeHandle customAttribute,
            string namespaceName,
            string typeName,
            out Handle ctor,
            bool ignoreCase = false)
        {
            return IsTargetAttribute(MetadataReader, customAttribute, namespaceName, typeName, out ctor, ignoreCase);
        }

        /// <summary>
        /// Determines if a custom attribute matches a namespace and name.
        /// </summary>
        /// <param name="metadataReader">The metadata reader.</param>
        /// <param name="customAttribute">Handle of the custom attribute.</param>
        /// <param name="namespaceName">The custom attribute's namespace in metadata format (case sensitive)</param>
        /// <param name="typeName">The custom attribute's type name in metadata format (case sensitive)</param>
        /// <param name="ctor">Constructor of the custom attribute.</param>
        /// <param name="ignoreCase">Should case be ignored for name comparison?</param>
        /// <returns>true if match is found</returns>
        private static bool IsTargetAttribute(
            MetadataReader metadataReader,
            CustomAttributeHandle customAttribute,
            string namespaceName,
            string typeName,
            out Handle ctor,
            bool ignoreCase)
        {
            Debug.Assert(namespaceName != null);
            Debug.Assert(typeName != null);

            Handle ctorType;
            Handle ctorTypeNamespace;
            StringHandle ctorTypeName;

            if (!GetTypeAndConstructor(metadataReader, customAttribute, out ctorType, out ctor))
            {
                return false;
            }

            if (!GetAttributeNamespaceAndName(metadataReader, ctorType, out ctorTypeNamespace, out ctorTypeName))
            {
                return false;
            }

            try
            {
                return StringEquals(metadataReader, ctorTypeName, typeName, ignoreCase)
                    && StringEquals(metadataReader, ctorTypeNamespace, namespaceName, ignoreCase);
            }
            catch (BadImageFormatException)
            {
                return false;
            }
        }

        /// <summary>
        /// Returns MetadataToken for assembly ref matching name
        /// </summary>
        /// <param name="assemblyName">The assembly name in metadata format (case sensitive)</param>
        /// <returns>Matching assembly ref token or nil (0)</returns>
        internal AssemblyReferenceHandle GetAssemblyRef(string assemblyName)
        {
            Debug.Assert(assemblyName != null);

            try
            {
                // Iterate over assembly ref rows
                foreach (var assemblyRef in MetadataReader.AssemblyReferences)
                {
                    // Check whether matching name                    
                    if (MetadataReader.StringComparer.Equals(MetadataReader.GetAssemblyReference(assemblyRef).Name, assemblyName))
                    {
                        // Return assembly ref token
                        return assemblyRef;
                    }
                }
            }
            catch (BadImageFormatException)
            { }

            // Not found
            return default(AssemblyReferenceHandle);
        }

        /// <summary>
        /// Returns MetadataToken for type ref matching resolution scope and name
        /// </summary>
        /// <param name="resolutionScope">The resolution scope token</param>
        /// <param name="namespaceName">The namespace name in metadata format (case sensitive)</param>
        /// <param name="typeName">The type name in metadata format (case sensitive)</param>
        /// <returns>Matching type ref token or nil (0)</returns>
        internal Handle GetTypeRef(
            Handle resolutionScope,
            string namespaceName,
            string typeName)
        {
            Debug.Assert(!resolutionScope.IsNil);
            Debug.Assert(namespaceName != null);
            Debug.Assert(typeName != null);

            try
            {
                // Iterate over type ref rows
                foreach (var handle in MetadataReader.TypeReferences)
                {
                    var typeRef = MetadataReader.GetTypeReference(handle);

                    // Check whether matching resolution scope
                    if (typeRef.ResolutionScope != resolutionScope)
                    {
                        continue;
                    }

                    // Check whether matching name
                    if (!MetadataReader.StringComparer.Equals(typeRef.Name, typeName))
                    {
                        continue;
                    }

                    if (MetadataReader.StringComparer.Equals(typeRef.Namespace, namespaceName))
                    {
                        // Return type ref token
                        return handle;
                    }
                }
            }
            catch (BadImageFormatException)
            { }

            // Not found
            return default(TypeReferenceHandle);
        }

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        public void GetTypeRefPropsOrThrow(
            TypeReferenceHandle handle,
            out string name,
            out string @namespace,
            out Handle resolutionScope)
        {
            TypeReference typeRef = MetadataReader.GetTypeReference(handle);
            resolutionScope = typeRef.ResolutionScope;
            name = MetadataReader.GetString(typeRef.Name);
            Debug.Assert(MetadataHelpers.IsValidMetadataIdentifier(name));
            @namespace = MetadataReader.GetString(typeRef.Namespace);
        }

        /// <summary>
        /// Determine if custom attribute matches the target attribute.
        /// </summary>
        /// <param name="customAttribute">
        /// Handle of the custom attribute.
        /// </param>
        /// <param name="description">The attribute to match.</param>
        /// <returns>
        /// An index of the target constructor signature in
        /// signatures array, -1 if
        /// this is not the target attribute.
        /// </returns>
        internal int GetTargetAttributeSignatureIndex(CustomAttributeHandle customAttribute, AttributeDescription description)
        {
            return GetTargetAttributeSignatureIndex(MetadataReader, customAttribute, description);
        }

        /// <summary>
        /// Determine if custom attribute matches the target attribute.
        /// </summary>
        /// <param name="metadataReader">
        /// The metadata reader.
        /// </param>
        /// <param name="customAttribute">
        /// Handle of the custom attribute.
        /// </param>
        /// <param name="description">The attribute to match.</param>
        /// <returns>
        /// An index of the target constructor signature in
        /// signatures array, -1 if
        /// this is not the target attribute.
        /// </returns>
        private static int GetTargetAttributeSignatureIndex(MetadataReader metadataReader, CustomAttributeHandle customAttribute, AttributeDescription description)
        {
            const int No = -1;
            Handle ctor;

            // Check namespace and type name and get signature if a match is found
            if (!IsTargetAttribute(metadataReader, customAttribute, description.Namespace, description.Name, out ctor, description.MatchIgnoringCase))
            {
                return No;
            }

            try
            {
                // Check signatures
                BlobReader sig = metadataReader.GetBlobReader(GetMethodSignatureOrThrow(metadataReader, ctor));

                for (int i = 0; i < description.Signatures.Length; i++)
                {
                    var targetSignature = description.Signatures[i];
                    Debug.Assert(targetSignature.Length >= 3);
                    sig.Reset();

                    // Make sure the headers match.
                    if (sig.RemainingBytes >= 3 &&
                        sig.ReadByte() == targetSignature[0] &&
                        sig.ReadByte() == targetSignature[1] &&
                        sig.ReadByte() == targetSignature[2])
                    {
                        int j = 3;
                        for (; j < targetSignature.Length; j++)
                        {
                            if (sig.RemainingBytes == 0)
                            {
                                // No more bytes in the signature
                                break;
                            }

                            SignatureTypeCode b = sig.ReadSignatureTypeCode();
                            if ((SignatureTypeCode)targetSignature[j] == b)
                            {
                                switch (b)
                                {
                                    case SignatureTypeCode.TypeHandle:
                                        Handle token = sig.ReadTypeHandle();
                                        HandleKind tokenType = token.Kind;
                                        StringHandle name;
                                        Handle ns;

                                        if (tokenType == HandleKind.TypeDefinition)
                                        {
                                            TypeDefinitionHandle typeHandle = (TypeDefinitionHandle)token;

                                            if (IsNestedTypeDefOrThrow(metadataReader, typeHandle))
                                            {
                                                // At the moment, none of the well-known attributes take nested types.
                                                break; // Signature doesn't match.
                                            }

                                            TypeDefinition typeDef = metadataReader.GetTypeDefinition(typeHandle);
                                            name = typeDef.Name;
                                            ns = typeDef.Namespace;
                                        }
                                        else if (tokenType == HandleKind.TypeReference)
                                        {
                                            TypeReference typeRef = metadataReader.GetTypeReference((TypeReferenceHandle)token);

                                            if (typeRef.ResolutionScope.Kind == HandleKind.TypeReference)
                                            {
                                                // At the moment, none of the well-known attributes take nested types.
                                                break; // Signature doesn't match.
                                            }

                                            name = typeRef.Name;
                                            ns = typeRef.Namespace;
                                        }
                                        else
                                        {
                                            break; // Signature doesn't match.
                                        }

                                        AttributeDescription.TypeHandleTargetInfo targetInfo = AttributeDescription.TypeHandleTargets[targetSignature[j + 1]];

                                        if (StringEquals(metadataReader, ns, targetInfo.Namespace, ignoreCase: false) && 
                                            StringEquals(metadataReader, name, targetInfo.Name, ignoreCase: false))
                                        {
                                            j++;
                                            continue;
                                        }

                                        break; // Signature doesn't match.

                                    case SignatureTypeCode.SZArray:
                                        // Verify array element type
                                        continue;

                                    default:
                                        continue;
                                }
                            }

                            break; // Signature doesn't match.
                        }

                        if (sig.RemainingBytes == 0 && j == targetSignature.Length)
                        {
                            // We found a match
                            return i;
                        }
                    }
                }
            }
            catch (BadImageFormatException)
            { }

            return No;
        }

        /// <summary>
        /// Given a token for a constructor, return the token for the constructor's type and the blob containing the
        /// constructor's signature.
        /// </summary>
        /// <returns>True if the function successfully returns the type and signature.</returns>
        internal bool GetTypeAndConstructor(
            CustomAttributeHandle customAttribute,
            out Handle ctorType,
            out Handle attributeCtor)
        {
            return GetTypeAndConstructor(MetadataReader, customAttribute, out ctorType, out attributeCtor);
        }

        /// <summary>
        /// Given a token for a constructor, return the token for the constructor's type and the blob containing the
        /// constructor's signature.
        /// </summary>
        /// <returns>True if the function successfully returns the type and signature.</returns>
        private static bool GetTypeAndConstructor(
            MetadataReader metadataReader,
            CustomAttributeHandle customAttribute,
            out Handle ctorType,
            out Handle attributeCtor)
        {
            try
            {
                ctorType = default(Handle);

                attributeCtor = metadataReader.GetCustomAttribute(customAttribute).Constructor;

                if (attributeCtor.Kind == HandleKind.MemberReference)
                {
                    MemberReference memberRef = metadataReader.GetMemberReference((MemberReferenceHandle)attributeCtor);

                    StringHandle ctorName = memberRef.Name;

                    if (!metadataReader.StringComparer.Equals(ctorName, WellKnownMemberNames.InstanceConstructorName))
                    {
                        // Not a constructor.
                        return false;
                    }

                    ctorType = memberRef.Parent;
                }
                else if (attributeCtor.Kind == HandleKind.MethodDefinition)
                {
                    var methodDef = metadataReader.GetMethodDefinition((MethodDefinitionHandle)attributeCtor);

                    if (!metadataReader.StringComparer.Equals(methodDef.Name, WellKnownMemberNames.InstanceConstructorName))
                    {
                        // Not a constructor.
                        return false;
                    }

                    ctorType = methodDef.GetDeclaringType();
                    Debug.Assert(!ctorType.IsNil);
                }
                else
                {
                    // invalid metadata
                    return false;
                }

                return true;
            }
            catch (BadImageFormatException)
            {
                ctorType = default(Handle);
                attributeCtor = default(Handle);
                return false;
            }
        }

        /// <summary>
        /// Given a token for a type, return the type's name and namespace.  Only works for top level types. 
        /// namespaceHandle will be NamespaceDefinitionHandle for defs and StringHandle for refs. 
        /// </summary>
        /// <returns>True if the function successfully returns the name and namespace.</returns>
        internal bool GetAttributeNamespaceAndName(Handle typeDefOrRef, out Handle namespaceHandle, out StringHandle nameHandle)
        {
            return GetAttributeNamespaceAndName(MetadataReader, typeDefOrRef, out namespaceHandle, out nameHandle);
        }

        /// <summary>
        /// Given a token for a type, return the type's name and namespace.  Only works for top level types. 
        /// namespaceHandle will be NamespaceDefinitionHandle for defs and StringHandle for refs. 
        /// </summary>
        /// <returns>True if the function successfully returns the name and namespace.</returns>
        private static bool GetAttributeNamespaceAndName(MetadataReader metadataReader, Handle typeDefOrRef, out Handle namespaceHandle, out StringHandle nameHandle)
        {
            nameHandle = default(StringHandle);
            namespaceHandle = default(Handle);

            try
            {
                if (typeDefOrRef.Kind == HandleKind.TypeReference)
                {
                    TypeReference typeRefRow = metadataReader.GetTypeReference((TypeReferenceHandle)typeDefOrRef);
                    HandleKind handleType = typeRefRow.ResolutionScope.Kind;

                    if (handleType == HandleKind.TypeReference || handleType == HandleKind.TypeDefinition)
                    {
                        // TODO - Support nested types.  
                        return false;
                    }

                    nameHandle = typeRefRow.Name;
                    namespaceHandle = typeRefRow.Namespace;
                }
                else if (typeDefOrRef.Kind == HandleKind.TypeDefinition)
                {
                    var def = metadataReader.GetTypeDefinition((TypeDefinitionHandle)typeDefOrRef);

                    if (IsNested(def.Attributes))
                    {
                        // TODO - Support nested types. 
                        return false;
                    }

                    nameHandle = def.Name;
                    namespaceHandle = def.Namespace;
                }
                else
                {
                    // unsupported metadata
                    return false;
                }

                return true;
            }
            catch (BadImageFormatException)
            {
                return false;
            }
        }

        /// <summary>
        /// For testing purposes only!!!
        /// </summary>
        internal void PretendThereArentNoPiaLocalTypes()
        {
            Debug.Assert(lazyContainsNoPiaLocalTypes != ThreeState.True);
            lazyContainsNoPiaLocalTypes = ThreeState.False;
        }

        internal bool ContainsNoPiaLocalTypes()
        {
            if (lazyContainsNoPiaLocalTypes == ThreeState.Unknown)
            {
                try
                {
                    foreach (var attributeHandle in MetadataReader.CustomAttributes)
                    {
                        int signatureIndex = IsTypeIdentifierAttribute(attributeHandle);
                        if (signatureIndex != -1)
                        {
                            // We found a match
                            lazyContainsNoPiaLocalTypes = ThreeState.True;

                            // We excluded attributes not applied on TypeDefs above:
                            var parent = (TypeDefinitionHandle)MetadataReader.GetCustomAttribute(attributeHandle).Parent;

                            RegisterNoPiaLocalType(parent, attributeHandle, signatureIndex);
                            return true;
                        }
                    }
                }
                catch (BadImageFormatException)
                { }

                lazyContainsNoPiaLocalTypes = ThreeState.False;
            }

            return lazyContainsNoPiaLocalTypes == ThreeState.True;
        }

        #endregion

        #region TypeSpec helpers

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        internal BlobReader GetTypeSpecificationSignatureReaderOrThrow(TypeSpecificationHandle typeSpec)
        {
            // TODO: Check validity of the typeSpec handle.
            BlobHandle signature = MetadataReader.GetTypeSpecification(typeSpec).Signature;

            // TODO: error checking offset in range
            return MetadataReader.GetBlobReader(signature);
        }

        #endregion

        #region MethodSpec helpers

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        internal void GetMethodSpecificationOrThrow(MethodSpecificationHandle handle, out Handle method, out BlobHandle instantiation)
        {
            var methodSpec = MetadataReader.GetMethodSpecification(handle);
            method = methodSpec.Method;
            instantiation = methodSpec.Signature;
        }

        #endregion

        #region GenericParam helpers

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        internal void GetGenericParamPropsOrThrow(
            GenericParameterHandle handle,
            out string name,
            out GenericParameterAttributes flags)
        {
            GenericParameter row = MetadataReader.GetGenericParameter(handle);
            name = MetadataReader.GetString(row.Name);
            flags = row.Attributes;
        }

        /// <summary>
        /// Returns an array of tokens for type constraints. Null reference if none.
        /// </summary>
        /// <param name="genericParam"></param>
        /// <returns>
        /// An array of tokens for type constraints. Null reference if none.
        /// </returns>
        internal Handle[] GetGenericParamConstraintsOrThrow(GenericParameterHandle genericParam)
        {
            var constraints = MetadataReader.GetGenericParameter(genericParam).GetConstraints();
            if (constraints.Count != 0)
            {
                Handle[] constraintTypes = new Handle[constraints.Count];
                for (int i = 0; i < constraintTypes.Length; i++)
                {
                    constraintTypes[i] = MetadataReader.GetGenericParameterConstraint(constraints[i]).Type;
                }

                return constraintTypes;
            }

            return null;
        }

        #endregion

        #region MethodDef helpers

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        internal string GetMethodDefNameOrThrow(MethodDefinitionHandle methodDef)
        {
            return MetadataReader.GetString(MetadataReader.GetMethodDefinition(methodDef).Name);
        }

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        internal BlobHandle GetMethodSignatureOrThrow(MethodDefinitionHandle methodDef)
        {
            return GetMethodSignatureOrThrow(MetadataReader, methodDef);
        }

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        private static BlobHandle GetMethodSignatureOrThrow(MetadataReader metadataReader, MethodDefinitionHandle methodDef)
        {
            return metadataReader.GetMethodDefinition(methodDef).Signature;
        }

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        internal BlobHandle GetMethodSignatureOrThrow(Handle methodDefOrRef)
        {
            return GetMethodSignatureOrThrow(MetadataReader, methodDefOrRef);
        }

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        private static BlobHandle GetMethodSignatureOrThrow(MetadataReader metadataReader, Handle methodDefOrRef)
        {
            switch (methodDefOrRef.Kind)
            {
                case HandleKind.MethodDefinition:
                    return GetMethodSignatureOrThrow(metadataReader, (MethodDefinitionHandle)methodDefOrRef);

                case HandleKind.MemberReference:
                    return GetSignatureOrThrow(metadataReader, (MemberReferenceHandle)methodDefOrRef);

                default:
                    throw ExceptionUtilities.UnexpectedValue(methodDefOrRef.Kind);
            }
        }

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        public MethodAttributes GetMethodDefFlagsOrThrow(MethodDefinitionHandle methodDef)
        {
            return MetadataReader.GetMethodDefinition(methodDef).Attributes;
        }

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        internal TypeDefinitionHandle FindContainingTypeOrThrow(MethodDefinitionHandle methodDef)
        {
            return MetadataReader.GetMethodDefinition(methodDef).GetDeclaringType();
        }

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        internal TypeDefinitionHandle FindContainingTypeOrThrow(FieldDefinitionHandle fieldDef)
        {
            return MetadataReader.GetFieldDefinition(fieldDef).GetDeclaringType();
        }

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        internal Handle GetContainingTypeOrThrow(MemberReferenceHandle memberRef)
        {
            return MetadataReader.GetMemberReference(memberRef).Parent;
        }

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        public void GetMethodDefPropsOrThrow(
            MethodDefinitionHandle methodDef,
            out string name,
            out MethodImplAttributes implFlags,
            out MethodAttributes flags,
            out int rva)
        {
            MethodDefinition methodRow = MetadataReader.GetMethodDefinition(methodDef);
            name = MetadataReader.GetString(methodRow.Name);
            implFlags = methodRow.ImplAttributes;
            flags = methodRow.Attributes;
            rva = methodRow.RelativeVirtualAddress;
            Debug.Assert(rva >= 0);
        }

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        internal void GetMethodImplPropsOrThrow(
            MethodImplementationHandle methodImpl,
            out Handle body,
            out Handle declaration)
        {
            var impl = MetadataReader.GetMethodImplementation(methodImpl);
            body = impl.MethodBody;
            declaration = impl.MethodDeclaration;
        }

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        internal GenericParameterHandleCollection GetGenericParametersForMethodOrThrow(MethodDefinitionHandle methodDef)
        {
            return MetadataReader.GetMethodDefinition(methodDef).GetGenericParameters();
        }

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        internal ParameterHandleCollection GetParametersOfMethodOrThrow(MethodDefinitionHandle methodDef)
        {
            return MetadataReader.GetMethodDefinition(methodDef).GetParameters();
        }

        internal DllImportData GetDllImportData(MethodDefinitionHandle methodDef)
        {
            try
            {
                var methodImport = MetadataReader.GetMethodDefinition(methodDef).GetImport();
                if (methodImport.Module.IsNil)
                {
                    // TODO (tomat): report an error?
                    return null;
                }

                string moduleName = GetModuleRefNameOrThrow(methodImport.Module);
                string entryPointName = MetadataReader.GetString(methodImport.Name);
                Cci.PInvokeAttributes flags = (Cci.PInvokeAttributes)methodImport.Attributes;

                return new DllImportData(moduleName, entryPointName, flags);
            }
            catch (BadImageFormatException)
            {
                return null;
            }
        }

        #endregion

        #region MemberRef helpers

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        public string GetMemberRefNameOrThrow(MemberReferenceHandle memberRef)
        {
            return GetMemberRefNameOrThrow(MetadataReader, memberRef);
        }

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        private static string GetMemberRefNameOrThrow(MetadataReader metadataReader, MemberReferenceHandle memberRef)
        {
            return metadataReader.GetString(metadataReader.GetMemberReference(memberRef).Name);
        }

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        internal BlobHandle GetSignatureOrThrow(MemberReferenceHandle memberRef)
        {
            return GetSignatureOrThrow(MetadataReader, memberRef);
        }

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        private static BlobHandle GetSignatureOrThrow(MetadataReader metadataReader, MemberReferenceHandle memberRef)
        {
            return metadataReader.GetMemberReference(memberRef).Signature;
        }

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        public void GetMemberRefPropsOrThrow(
            MemberReferenceHandle memberRef,
            out Handle @class,
            out string name,
            out byte[] signature)
        {
            MemberReference row = MetadataReader.GetMemberReference(memberRef);
            @class = row.Parent;
            name = MetadataReader.GetString(row.Name);
            signature = MetadataReader.GetBlobBytes(row.Signature);
        }

        #endregion MemberRef helpers

        #region ParamDef helpers

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        internal void GetParamPropsOrThrow(
            ParameterHandle parameterDef,
            out string name,
            out ParameterAttributes flags)
        {
            Parameter parameter = MetadataReader.GetParameter(parameterDef);
            name = MetadataReader.GetString(parameter.Name);
            flags = parameter.Attributes;
        }

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        internal int GetParameterSequenceNumberOrThrow(ParameterHandle param)
        {
            return MetadataReader.GetParameter(param).SequenceNumber;
        }

        #endregion

        #region PropertyDef helpers

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        internal string GetPropertyDefNameOrThrow(PropertyDefinitionHandle propertyDef)
        {
            return MetadataReader.GetString(MetadataReader.GetPropertyDefinition(propertyDef).Name);
        }

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        internal BlobHandle GetPropertySignatureOrThrow(PropertyDefinitionHandle propertyDef)
        {
            return MetadataReader.GetPropertyDefinition(propertyDef).Signature;
        }

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        internal void GetPropertyDefPropsOrThrow(
            PropertyDefinitionHandle propertyDef,
            out string name,
            out PropertyAttributes flags)
        {
            PropertyDefinition property = MetadataReader.GetPropertyDefinition(propertyDef);
            name = MetadataReader.GetString(property.Name);
            flags = property.Attributes;
        }

        #endregion

        #region EventDef helpers

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        internal string GetEventDefNameOrThrow(EventDefinitionHandle eventDef)
        {
            return MetadataReader.GetString(MetadataReader.GetEventDefinition(eventDef).Name);
        }

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        internal void GetEventDefPropsOrThrow(
            EventDefinitionHandle eventDef,
            out string name,
            out EventAttributes flags,
            out Handle type)
        {
            EventDefinition eventRow = MetadataReader.GetEventDefinition(eventDef);
            name = MetadataReader.GetString(eventRow.Name);
            flags = eventRow.Attributes;
            type = eventRow.Type;
        }

        #endregion

        #region FieldDef helpers

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        public string GetFieldDefNameOrThrow(FieldDefinitionHandle fieldDef)
        {
            return MetadataReader.GetString(MetadataReader.GetFieldDefinition(fieldDef).Name);
        }

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        internal BlobHandle GetFieldSignatureOrThrow(FieldDefinitionHandle fieldDef)
        {
            return MetadataReader.GetFieldDefinition(fieldDef).Signature;
        }

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        public FieldAttributes GetFieldDefFlagsOrThrow(FieldDefinitionHandle fieldDef)
        {
            return MetadataReader.GetFieldDefinition(fieldDef).Attributes;
        }

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        public void GetFieldDefPropsOrThrow(
            FieldDefinitionHandle fieldDef,
            out string name,
            out FieldAttributes flags)
        {
            FieldDefinition fieldRow = MetadataReader.GetFieldDefinition(fieldDef);

            name = MetadataReader.GetString(fieldRow.Name);
            flags = fieldRow.Attributes;
        }

        internal ConstantValue GetParamDefaultValue(ParameterHandle param)
        {
            Debug.Assert(!param.IsNil);

            try
            {
                var constantHandle = MetadataReader.GetParameter(param).GetDefaultValue();

                // TODO: Error checking: Throw an error if the table entry cannot be found
                return constantHandle.IsNil ? ConstantValue.Bad : GetConstantValueOrThrow(constantHandle);
            }
            catch (BadImageFormatException)
            {
                return ConstantValue.Bad;
            }
        }

        internal ConstantValue GetConstantFieldValue(FieldDefinitionHandle fieldDef)
        {
            Debug.Assert(!fieldDef.IsNil);

            try
            {
                var constantHandle = MetadataReader.GetFieldDefinition(fieldDef).GetDefaultValue();

                // TODO: Error checking: Throw an error if the table entry cannot be found
                return constantHandle.IsNil ? ConstantValue.Bad : GetConstantValueOrThrow(constantHandle);
            }
            catch (BadImageFormatException)
            {
                return ConstantValue.Bad;
            }
        }

        #endregion

        #region Attribute Helpers

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        public CustomAttributeHandleCollection GetCustomAttributesOrThrow(Handle handle)
        {
            return MetadataReader.GetCustomAttributes(handle);
        }

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        public BlobHandle GetCustomAttributeValueOrThrow(CustomAttributeHandle handle)
        {
            return MetadataReader.GetCustomAttribute(handle).Value;
        }

        #endregion

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        private BlobHandle GetMarshallingDescriptorHandleOrThrow(Handle fieldOrParameterToken)
        {
            return fieldOrParameterToken.Kind == HandleKind.FieldDefinition ?
                MetadataReader.GetFieldDefinition((FieldDefinitionHandle)fieldOrParameterToken).GetMarshallingDescriptor() :
                MetadataReader.GetParameter((ParameterHandle)fieldOrParameterToken).GetMarshallingDescriptor();
        }

        internal UnmanagedType GetMarshallingType(Handle fieldOrParameterToken)
        {
            try
            {
                var blob = GetMarshallingDescriptorHandleOrThrow(fieldOrParameterToken);

                if (blob.IsNil)
                {
                    // TODO (tomat): report error:
                    return 0;
                }

                byte firstByte = MetadataReader.GetBlobReader(blob).ReadByte();

                // return only valid types, other values are not interesting for the compiler:
                return firstByte <= 0x50 ? (UnmanagedType)firstByte : 0;
            }
            catch (BadImageFormatException)
            {
                return 0;
            }
        }

        internal ImmutableArray<byte> GetMarshallingDescriptor(Handle fieldOrParameterToken)
        {
            try
            {
                var blob = GetMarshallingDescriptorHandleOrThrow(fieldOrParameterToken);
                if (blob.IsNil)
                {
                    // TODO (tomat): report error:
                    return ImmutableArray<byte>.Empty;
                }

                return MetadataReader.GetBlobBytes(blob).AsImmutableOrNull();
            }
            catch (BadImageFormatException)
            {
                return ImmutableArray<byte>.Empty;
            }
        }

        internal int? GetFieldOffset(FieldDefinitionHandle fieldDef)
        {
            try
            {
                int offset = MetadataReader.GetFieldDefinition(fieldDef).GetOffset();
                if (offset == -1)
                {
                    return null;
                }

                return offset;
            }
            catch (BadImageFormatException)
            {
                return null;
            }
        }

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        private ConstantValue GetConstantValueOrThrow(ConstantHandle handle)
        {
            var constantRow = MetadataReader.GetConstant(handle);
            ConstantTypeCode type = constantRow.TypeCode;

            // Partition II section 22.9:
            //
            // Type shall be exactly one of: ELEMENT_TYPE_BOOLEAN, ELEMENT_TYPE_CHAR, ELEMENT_TYPE_I1, 
            // ELEMENT_TYPE_U1, ELEMENT_TYPE_I2, ELEMENT_TYPE_U2, ELEMENT_TYPE_I4, ELEMENT_TYPE_U4, 
            // ELEMENT_TYPE_I8, ELEMENT_TYPE_U8, ELEMENT_TYPE_R4, ELEMENT_TYPE_R8, or ELEMENT_TYPE_STRING; 
            // or ELEMENT_TYPE_CLASS with a Value of zero  (23.1.16)

            BlobReader reader = MetadataReader.GetBlobReader(constantRow.Value);
            // TODO: Error checking; we do not verify that the block size matches the size of the constant we are expecting.
            // TODO: The blob heap could be corrupt.
            switch (type)
            {
                case ConstantTypeCode.Boolean:
                    byte b = reader.ReadByte();
                    return ConstantValue.Create(b != 0);
                case ConstantTypeCode.Char:
                    return ConstantValue.Create(reader.ReadChar());
                case ConstantTypeCode.SByte:
                    return ConstantValue.Create(reader.ReadSByte());
                case ConstantTypeCode.Int16:
                    return ConstantValue.Create(reader.ReadInt16());
                case ConstantTypeCode.Int32:
                    return ConstantValue.Create(reader.ReadInt32());
                case ConstantTypeCode.Int64:
                    return ConstantValue.Create(reader.ReadInt64());
                case ConstantTypeCode.Byte:
                    return ConstantValue.Create(reader.ReadByte());
                case ConstantTypeCode.UInt16:
                    return ConstantValue.Create(reader.ReadUInt16());
                case ConstantTypeCode.UInt32:
                    return ConstantValue.Create(reader.ReadUInt32());
                case ConstantTypeCode.UInt64:
                    return ConstantValue.Create(reader.ReadUInt64());
                case ConstantTypeCode.Single:
                    return ConstantValue.Create(reader.ReadSingle());
                case ConstantTypeCode.Double:
                    return ConstantValue.Create(reader.ReadDouble());
                case ConstantTypeCode.String:
                    // A null string constant is represented as an ELEMENT_TYPE_CLASS.
                    int byteLen = reader.Length;
                    return ConstantValue.Create(byteLen == 0 ? "" : reader.ReadUTF16(byteLen));
                case ConstantTypeCode.NullReference:
                    // TODO: Error checking; verify that the value is all zero bytes;
                    return ConstantValue.Null;
            }

            return ConstantValue.Bad;
        }

        internal AssemblyReferenceHandle GetAssemblyForForwardedType(string fullName, bool ignoreCase, out string matchedName)
        {
            EnsureForwardTypeToAssemblyMap();

            if (ignoreCase)
            {
                // This linear search is not the optimal way to use a hashmap, but we should only use
                // this functionality when computing diagnostics.  Note
                // that we can't store the map case-insensitively, since real metadata name
                // lookup has to remain case sensitive.
                foreach (var pair in lazyForwardedTypesToAssemblyMap)
                {
                    if (string.Equals(pair.Key, fullName, StringComparison.OrdinalIgnoreCase))
                    {
                        matchedName = pair.Key;
                        return pair.Value;
                    }
                }
            }
            else
            {
                AssemblyReferenceHandle assemblyRef;
                if (lazyForwardedTypesToAssemblyMap.TryGetValue(fullName, out assemblyRef))
                {
                    matchedName = fullName;
                    return assemblyRef;
                }
            }

            matchedName = null;
            return default(AssemblyReferenceHandle);
        }

        internal IEnumerable<KeyValuePair<string, AssemblyReferenceHandle>> GetForwardedTypes()
        {
            EnsureForwardTypeToAssemblyMap();
            return lazyForwardedTypesToAssemblyMap;
        }

        private void EnsureForwardTypeToAssemblyMap()
        {
            if (lazyForwardedTypesToAssemblyMap == null)
            {
                var typesToAssemblyMap = new Dictionary<string, AssemblyReferenceHandle>();

                try
                {
                    var forwarders = MetadataReader.ExportedTypes;
                    foreach (var handle in forwarders)
                    {
                        ExportedType exportedType = MetadataReader.GetExportedType(handle);
                        if (!exportedType.IsForwarder)
                        {
                            continue;
                        }

                        string name = MetadataReader.GetString(exportedType.Name);
                        NamespaceDefinitionHandle ns = exportedType.Namespace;
                        if (!ns.IsNil)
                        {
                            string namespaceString = MetadataReader.GetString(ns);
                            if (namespaceString.Length > 0)
                            {
                                name = namespaceString + "." + name;
                            }
                        }

                        typesToAssemblyMap.Add(name, (AssemblyReferenceHandle)exportedType.Implementation);
                    }
                }
                catch (BadImageFormatException)
                { }

                lazyForwardedTypesToAssemblyMap = typesToAssemblyMap;
            }
        }

        internal IdentifierCollection TypeNames
        {
            get
            {
                return lazyTypeNameCollection.Value;
            }
        }

        internal IdentifierCollection NamespaceNames
        {
            get
            {
                return lazyNamespaceNameCollection.Value;
            }
        }

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        internal PropertyAccessors GetPropertyMethodsOrThrow(PropertyDefinitionHandle propertyDef)
        {
            return MetadataReader.GetPropertyDefinition(propertyDef).GetAccessors();
        }

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        internal EventAccessors GetEventMethodsOrThrow(EventDefinitionHandle eventDef)
        {
            return MetadataReader.GetEventDefinition(eventDef).GetAccessors();
        }

        /// <exception cref="BadImageFormatException">An exception from metadata reader.</exception>
        internal int GetAssemblyReferenceIndexOrThrow(AssemblyReferenceHandle assemblyRef)
        {
            return MetadataReader.GetRowNumber(assemblyRef) - 1;
        }

        internal static bool IsNested(TypeAttributes flags)
        {
            return (flags & ((TypeAttributes)0x00000006)) != 0;
        }

        /// <summary>
        /// Returns true if method IL can be retrieved from the module.
        /// </summary>
        internal bool HasIL
        {
            get { return IsEntireImageAvailable; }
        }

        /// <summary>
        /// Returns true if the full image of the module is available.
        /// </summary>
        internal bool IsEntireImageAvailable
        {
            get { return peReaderOpt != null && peReaderOpt.IsEntireImageAvailable; }
        }

        /// <exception cref="BadImageFormatException">Invalid metadata.</exception>
        internal MethodBodyBlock GetMethodBodyOrThrow(MethodDefinitionHandle methodHandle)
        {
            // we shouldn't ask for method IL if we don't have PE image
            Debug.Assert(peReaderOpt != null);

            MethodDefinition method = this.MetadataReader.GetMethodDefinition(methodHandle);
            if ((method.ImplAttributes & MethodImplAttributes.CodeTypeMask) != MethodImplAttributes.IL ||
                 method.RelativeVirtualAddress == 0)
            {
                return null;
            }

            return peReaderOpt.GetMethodBody(method.RelativeVirtualAddress);
        }

        private static bool StringEquals(MetadataReader metadataReader, Handle nameHandle, string name, bool ignoreCase)
        {
            switch (nameHandle.Kind)
            {
                case HandleKind.NamespaceDefinition:
                    return StringEquals(metadataReader, (NamespaceDefinitionHandle)nameHandle, name, ignoreCase);
                case HandleKind.String:
                    return StringEquals(metadataReader, (StringHandle)nameHandle, name, ignoreCase);
                default:
                    throw ExceptionUtilities.UnexpectedValue(nameHandle.Kind);
            }
        }

        // TODO: remove, API should be provided by MetadataReader
        private static bool StringEquals(MetadataReader metadataReader, StringHandle nameHandle, string name, bool ignoreCase)
        {
            if (ignoreCase)
            {
                return string.Equals(metadataReader.GetString(nameHandle), name, StringComparison.OrdinalIgnoreCase);
            }

            return metadataReader.StringComparer.Equals(nameHandle, name);
        }

        // TODO: remove, API should be provided by MetadataReader
        private static bool StringEquals(MetadataReader metadataReader, NamespaceDefinitionHandle nameHandle, string name, bool ignoreCase)
        {
            if (ignoreCase)
            {
                return string.Equals(metadataReader.GetString(nameHandle), name, StringComparison.OrdinalIgnoreCase);
            }

            return metadataReader.StringComparer.Equals(nameHandle, name);
        }

        // Provides a UTF8 decoder to the MetadataReader that reuses strings from the string table
        // rather than allocating on each call to MetadataReader.GetString(handle).
        private sealed class StringTableDecoder : MetadataStringDecoder
        {
            public static readonly StringTableDecoder Instance = new StringTableDecoder();

            public StringTableDecoder() : base(System.Text.Encoding.UTF8) { }

            public unsafe override string GetString(byte* bytes, int byteCount)
            {
                return StringTable.AddSharedUTF8(bytes, byteCount);
            }
        }
    }
}
