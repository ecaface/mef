﻿// -----------------------------------------------------------------------
// Copyright © Microsoft Corporation.  All rights reserved.
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Composition.Convention;
using System.Composition.Hosting;
using System.Composition.Hosting.Core;
using System.Composition.Runtime;
using System.Composition.TypedParts.ActivationFeatures;
using System.Linq;
using System.Reflection;

namespace System.Composition.TypedParts.Discovery
{
    class TypeInspector
    {
        static readonly IDictionary<string, object> NoMetadata = new Dictionary<string, object>();

        readonly ActivationFeature[] _activationFeatures;
        readonly AttributedModelProvider _attributeContext;
       
        public TypeInspector(AttributedModelProvider attributeContext, ActivationFeature[] activationFeatures)
        {
            _attributeContext = attributeContext;
            _activationFeatures = activationFeatures;
        }

        public bool InspectTypeForPart(TypeInfo type, out DiscoveredPart part)
        {
            part = null;

            if (type.IsAbstract || !type.IsClass || _attributeContext.GetDeclaredAttribute<PartNotDiscoverableAttribute>(type.AsType(), type) != null)
                return false;

            foreach (var export in DiscoverExports(type))
            {
                part = part ?? new DiscoveredPart(type, _attributeContext, _activationFeatures);
                part.AddDiscoveredExport(export);
            }

            return part != null;
        }

        IEnumerable<DiscoveredExport> DiscoverExports(TypeInfo partType)
        {
            foreach (var export in DiscoverInstanceExports(partType))
                yield return export;

            foreach (var export in DiscoverPropertyExports(partType))
                yield return export;
        }

        IEnumerable<DiscoveredExport> DiscoverInstanceExports(TypeInfo partType)
        {
            var partTypeAsType = partType.AsType();
            foreach (var export in _attributeContext.GetDeclaredAttributes<ExportAttribute>(partTypeAsType, partType))
            {
                IDictionary<string, object> metadata = new Dictionary<string, object>();
                ReadMetadataAttribute(export, metadata);

                var applied = _attributeContext.GetDeclaredAttributes(partTypeAsType, partType);
                ReadLooseMetadata(applied, metadata);

                var contractType = export.ContractType ?? partTypeAsType;
                CheckInstanceExportCompatibility(partType, contractType.GetTypeInfo());

                var exportKey = new CompositionContract(contractType, export.ContractName);

                if (metadata.Count == 0)
                    metadata = NoMetadata;

                yield return new DiscoveredInstanceExport(exportKey, metadata);
            }
        }

        IEnumerable<DiscoveredExport> DiscoverPropertyExports(TypeInfo partType)
        {
            var partTypeAsType = partType.AsType();
            foreach (var property in partTypeAsType.GetRuntimeProperties()
                .Where(pi => pi.CanRead && pi.GetMethod.IsPublic && !pi.GetMethod.IsStatic))
            {
                foreach (var export in _attributeContext.GetDeclaredAttributes<ExportAttribute>(partTypeAsType, property))
                {
                    IDictionary<string, object> metadata = new Dictionary<string, object>();
                    ReadMetadataAttribute(export, metadata);

                    var applied = _attributeContext.GetDeclaredAttributes(partTypeAsType, property);
                    ReadLooseMetadata(applied, metadata);

                    var contractType = export.ContractType ?? property.PropertyType;
                    CheckPropertyExportCompatibility(partType, property, contractType.GetTypeInfo());

                    var exportKey = new CompositionContract(export.ContractType ?? property.PropertyType, export.ContractName);

                    if (metadata.Count == 0)
                        metadata = NoMetadata;

                    yield return new DiscoveredPropertyExport(exportKey, metadata, property);
                }
            }
        }

        void ReadLooseMetadata(object[] appliedAttributes, IDictionary<string, object> metadata)
        {
            foreach (var attribute in appliedAttributes)
            {
                if (attribute is ExportAttribute)
                    continue;

                var ema = attribute as ExportMetadataAttribute;
                if (ema != null)
                {
                    AddMetadata(metadata, ema.Name, ema.Value);
                }
                else
                {
                    ReadMetadataAttribute((Attribute)attribute, metadata);
                }
            }
        }

        void AddMetadata(IDictionary<string, object> metadata, string name, object value)
        {
            object existingValue;
            if (!metadata.TryGetValue(name, out existingValue))
            {
                metadata.Add(name, value);
                return;
            }

            var valueType = existingValue.GetType();
            if (valueType.IsArray)
            {
                var existingArray = (Array)existingValue;
                var newArray = Array.CreateInstance(value.GetType(), existingArray.Length + 1);
                Array.Copy(existingArray, newArray, existingArray.Length);
                newArray.SetValue(value, existingArray.Length);
                metadata[name] = newArray;
            }
            else
            {
                var newArray = Array.CreateInstance(value.GetType(), 2);
                newArray.SetValue(existingValue, 0);
                newArray.SetValue(value, 1);
                metadata[name] = newArray;
            }
        }

        void ReadMetadataAttribute(Attribute attribute, IDictionary<string, object> metadata)
        {
            var attrType = attribute.GetType();

            // Note, we don't support ReflectionContext in this scenario as
            if (attrType.GetTypeInfo().GetCustomAttribute<MetadataAttributeAttribute>(true) == null)
                return;

            foreach (var prop in attrType
                .GetRuntimeProperties()
                .Where(p => p.DeclaringType == attrType && p.CanRead))
            {
                AddMetadata(metadata, prop.Name, prop.GetValue(attribute, null));
            }
        }

        static void CheckPropertyExportCompatibility(TypeInfo partType, PropertyInfo property, TypeInfo contractType)
        {
            if (partType.IsGenericTypeDefinition)
            {
                CheckGenericContractCompatibility(partType, property.PropertyType.GetTypeInfo(), contractType);
            }
            else if (!contractType.IsAssignableFrom(property.PropertyType.GetTypeInfo()))
            {
                var message = string.Format(Properties.Resources.TypeInspector_ExportedContractTypeNotAssignable,
                                                contractType.Name, property.Name, partType.Name);
                throw new CompositionFailedException(message);
            }
        }

        static void CheckGenericContractCompatibility(TypeInfo partType, TypeInfo exportingMemberType, TypeInfo contractType)
        {
            if (!contractType.IsGenericTypeDefinition)
            {
                var message = string.Format(Properties.Resources.TypeInspector_NoExportNonGenericContract, partType.Name, contractType.Name);
                throw new CompositionFailedException(message);
            }

            var compatible = false;

            foreach (var ifce in GetAssignableTypes(exportingMemberType))
            {
                if (ifce == contractType || (ifce.IsGenericType && ifce.GetGenericTypeDefinition() == contractType.AsType()))
                {
                    var mappedType = ifce;
                    if (!(mappedType == partType || mappedType.GenericTypeArguments.SequenceEqual(partType.GenericTypeParameters)))
                    {
                        var message = string.Format(Properties.Resources.TypeInspector_ArgumentMissmatch, contractType.Name, partType.Name);
                        throw new CompositionFailedException(message);
                    }

                    compatible = true;
                    break;
                }
            }

            if (!compatible)
            {
                var message = string.Format(Properties.Resources.TypeInspector_ExportNotCompatible, exportingMemberType.Name, partType.Name, contractType.Name);
                throw new CompositionFailedException(message);
            }
        }

        static IEnumerable<TypeInfo> GetAssignableTypes(TypeInfo exportingMemberType)
        {
            foreach (var ifce in exportingMemberType.ImplementedInterfaces)
                yield return ifce.GetTypeInfo();

            var b = exportingMemberType;
            while (b != null)
            {
                yield return b;
                b = b.BaseType.GetTypeInfo();
            }
        }

        static void CheckInstanceExportCompatibility(TypeInfo partType, TypeInfo contractType)
        {
            if (partType.IsGenericTypeDefinition)
            {
                CheckGenericContractCompatibility(partType, partType, contractType);
            }
            else if (!contractType.IsAssignableFrom(partType))
            {
                var message = string.Format(Properties.Resources.TypeInspector_ContractNotAssignable, contractType.Name, partType.Name);
                throw new CompositionFailedException(message);
            }
        }
    }
}
