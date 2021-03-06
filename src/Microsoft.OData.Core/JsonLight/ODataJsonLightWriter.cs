﻿//---------------------------------------------------------------------
// <copyright file="ODataJsonLightWriter.cs" company="Microsoft">
//      Copyright (C) Microsoft Corporation. All rights reserved. See License.txt in the project root for license information.
// </copyright>
//---------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
#if PORTABLELIB
using System.Threading.Tasks;
#endif
using Microsoft.OData.Edm;
using Microsoft.OData.Evaluation;
using Microsoft.OData.Metadata;
using Microsoft.OData.Json;

namespace Microsoft.OData.JsonLight
{
    /// <summary>
    /// Implementation of the ODataWriter for the JsonLight format.
    /// </summary>
    internal sealed class ODataJsonLightWriter : ODataWriterCore
    {
        /// <summary>
        /// The output context to write to.
        /// </summary>
        private readonly ODataJsonLightOutputContext jsonLightOutputContext;

        /// <summary>
        /// The JsonLight resource and resource set serializer to use.
        /// </summary>
        private readonly ODataJsonLightResourceSerializer jsonLightResourceSerializer;

        /// <summary>
        /// True if the writer was created for writing a parameter; false otherwise.
        /// </summary>
        private readonly bool writingParameter;

        /// <summary>
        /// The underlying JSON writer.
        /// </summary>
        private readonly IJsonWriter jsonWriter;

        /// <summary>
        /// OData annotation writer.
        /// </summary>
        private readonly JsonLightODataAnnotationWriter odataAnnotationWriter;

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="jsonLightOutputContext">The output context to write to.</param>
        /// <param name="navigationSource">The navigation source we are going to write resource set for.</param>
        /// <param name="resourceType">The structured type for the items in the resource set to be written (or null if the entity set base type should be used).</param>
        /// <param name="writingResourceSet">true if the writer is created for writing a resource set; false when it is created for writing a resource.</param>
        /// <param name="writingParameter">true if the writer is created for writing a parameter; false otherwise.</param>
        /// <param name="writingDelta">True if the writer is created for writing delta response; false otherwise.</param>
        /// <param name="listener">If not null, the writer will notify the implementer of the interface of relevant state changes in the writer.</param>
        internal ODataJsonLightWriter(
            ODataJsonLightOutputContext jsonLightOutputContext,
            IEdmNavigationSource navigationSource,
            IEdmStructuredType resourceType,
            bool writingResourceSet,
            bool writingParameter = false,
            bool writingDelta = false,
            IODataReaderWriterListener listener = null)
            : base(jsonLightOutputContext, navigationSource, resourceType, writingResourceSet, writingDelta, listener)
        {
            Debug.Assert(jsonLightOutputContext != null, "jsonLightOutputContext != null");

            this.jsonLightOutputContext = jsonLightOutputContext;
            this.jsonLightResourceSerializer = new ODataJsonLightResourceSerializer(this.jsonLightOutputContext);

            this.writingParameter = writingParameter;
            this.jsonWriter = this.jsonLightOutputContext.JsonWriter;
            this.odataAnnotationWriter = new JsonLightODataAnnotationWriter(this.jsonWriter,
                this.jsonLightOutputContext.ODataSimplifiedOptions.EnableWritingODataAnnotationWithoutPrefix);
        }

        /// <summary>
        /// Returns the current JsonLightResourceScope.
        /// </summary>
        private JsonLightResourceScope CurrentResourceScope
        {
            get
            {
                JsonLightResourceScope currentJsonLightResourceScope = this.CurrentScope as JsonLightResourceScope;
                Debug.Assert(currentJsonLightResourceScope != null, "Asking for JsonLightResourceScope when the current scope is not an JsonLightResourceScope.");
                return currentJsonLightResourceScope;
            }
        }

        /// <summary>
        /// Returns the current JsonLightResourceSetScope.
        /// </summary>
        private JsonLightResourceSetScope CurrentResourceSetScope
        {
            get
            {
                JsonLightResourceSetScope currentJsonLightResourceSetScope = this.CurrentScope as JsonLightResourceSetScope;
                Debug.Assert(currentJsonLightResourceSetScope != null, "Asking for JsonResourceSetScope when the current scope is not a JsonResourceSetScope.");
                return currentJsonLightResourceSetScope;
            }
        }

        /// <summary>
        /// Check if the object has been disposed; called from all public API methods. Throws an ObjectDisposedException if the object
        /// has already been disposed.
        /// </summary>
        protected override void VerifyNotDisposed()
        {
            this.jsonLightOutputContext.VerifyNotDisposed();
        }

        /// <summary>
        /// Flush the output.
        /// </summary>
        protected override void FlushSynchronously()
        {
            this.jsonLightOutputContext.Flush();
        }

#if PORTABLELIB
        /// <summary>
        /// Flush the output.
        /// </summary>
        /// <returns>Task representing the pending flush operation.</returns>
        protected override Task FlushAsynchronously()
        {
            return this.jsonLightOutputContext.FlushAsync();
        }
#endif

        /// <summary>
        /// Starts writing a payload (called exactly once before anything else)
        /// </summary>
        protected override void StartPayload()
        {
            this.jsonLightResourceSerializer.WritePayloadStart();
        }

        /// <summary>
        /// Ends writing a payload (called exactly once after everything else in case of success)
        /// </summary>
        protected override void EndPayload()
        {
            this.jsonLightResourceSerializer.WritePayloadEnd();
        }

        /// <summary>
        /// Place where derived writers can perform custom steps before the resource is written, at the beginning of WriteStartEntryImplementation.
        /// </summary>
        /// <param name="resourceScope">The ResourceScope.</param>
        /// <param name="resource">Resource to write.</param>
        /// <param name="writingResponse">True if writing response.</param>
        /// <param name="selectedProperties">The selected properties of this scope.</param>
        protected override void PrepareResourceForWriteStart(ResourceScope resourceScope, ODataResource resource, bool writingResponse, SelectedPropertiesNode selectedProperties)
        {
            var typeContext = resourceScope.GetOrCreateTypeContext(writingResponse);
            if (this.jsonLightOutputContext.MetadataLevel is JsonNoMetadataLevel)
            {
                // 1. NoMetadata level: always enable its NullResourceMetadataBuilder
                InnerPrepareResourceForWriteStart(resource, typeContext, selectedProperties);
            }
            else
            {
                // 2. Minimal/Full Metadata level: Use ODataConventionalEntityMetadataBuilder for entity, ODataConventionalResourceMetadataBuilder for other cases.
                if (this.jsonLightOutputContext.Model.IsUserModel() || resourceScope.SerializationInfo != null)
                {
                    InnerPrepareResourceForWriteStart(resource, typeContext, selectedProperties);
                }

                // 3. Here fallback to the default NoOpResourceMetadataBuilder, when model and serializationInfo are both null.
            }
        }

        /// <summary>
        /// Start writing a resource.
        /// </summary>
        /// <param name="resource">The resource to write.</param>
        protected override void StartResource(ODataResource resource)
        {
            ODataNestedResourceInfo parentNavLink = this.ParentNestedResourceInfo;
            if (parentNavLink != null)
            {
                // Write the property name of an expanded navigation property to start the value.
                this.jsonWriter.WriteName(parentNavLink.Name);
            }

            if (resource == null)
            {
                this.jsonWriter.WriteValue((string)null);
                return;
            }

            // Write just the object start, nothing else, since we might not have complete information yet
            this.jsonWriter.StartObjectScope();

            JsonLightResourceScope resourceScope = this.CurrentResourceScope;

            if (this.IsTopLevel)
            {
                var contextUriInfo = this.jsonLightResourceSerializer.WriteResourceContextUri(
                        resourceScope.GetOrCreateTypeContext(this.jsonLightOutputContext.WritingResponse));

                // Is writing an undeclared resource.
                if (contextUriInfo != null)
                {
                    resourceScope.IsUndeclared = contextUriInfo.IsUndeclared.HasValue && contextUriInfo.IsUndeclared.Value;
                }
            }

            // Write the metadata
            this.jsonLightResourceSerializer.WriteResourceStartMetadataProperties(resourceScope);
            this.jsonLightResourceSerializer.WriteResourceMetadataProperties(resourceScope);

            // Write custom instance annotations
            this.jsonLightResourceSerializer.InstanceAnnotationWriter.WriteInstanceAnnotations(resource.InstanceAnnotations, resourceScope.InstanceAnnotationWriteTracker);

            this.jsonLightOutputContext.PropertyCacheHandler.SetCurrentResourceScopeLevel(this.ScopeLevel);

            this.jsonLightResourceSerializer.JsonLightValueSerializer.AssertRecursionDepthIsZero();
            this.jsonLightResourceSerializer.WriteProperties(
                this.ResourceType,
                resource.Properties,
                false /* isComplexValue */,
                this.DuplicatePropertyNameChecker);
            this.jsonLightResourceSerializer.JsonLightValueSerializer.AssertRecursionDepthIsZero();

            // COMPAT 48: Position of navigation properties/links in JSON differs.
        }

        /// <summary>
        /// Finish writing a resource.
        /// </summary>
        /// <param name="resource">The resource to write.</param>
        protected override void EndResource(ODataResource resource)
        {
            if (resource == null)
            {
                return;
            }

            // Get the projected properties
            JsonLightResourceScope resourceScope = this.CurrentResourceScope;

            this.jsonLightResourceSerializer.WriteResourceMetadataProperties(resourceScope);

            // Write custom instance annotations
            this.jsonLightResourceSerializer.InstanceAnnotationWriter.WriteInstanceAnnotations(resource.InstanceAnnotations, resourceScope.InstanceAnnotationWriteTracker);

            this.jsonLightResourceSerializer.WriteResourceEndMetadataProperties(resourceScope, resourceScope.DuplicatePropertyNameChecker);

            // Close the object scope
            this.jsonWriter.EndObjectScope();
        }

        /// <summary>
        /// Start writing a resource set.
        /// </summary>
        /// <param name="resourceSet">The resource set to write.</param>
        protected override void StartResourceSet(ODataResourceSet resourceSet)
        {
            Debug.Assert(resourceSet != null, "resourceSet != null");

            if (this.ParentNestedResourceInfo == null && this.writingParameter)
            {
                // Start array which will hold the entries in the resource set.
                this.jsonWriter.StartArrayScope();
            }
            else if (this.ParentNestedResourceInfo == null)
            {
                // Top-level resource set.
                // "{"
                this.jsonWriter.StartObjectScope();

                // @odata.context
                this.jsonLightResourceSerializer.WriteResourceSetContextUri(this.CurrentResourceSetScope.GetOrCreateTypeContext(this.jsonLightOutputContext.WritingResponse));

                if (this.jsonLightOutputContext.WritingResponse)
                {
                    // write "odata.actions" metadata
                    IEnumerable<ODataAction> actions = resourceSet.Actions;
                    if (actions != null && actions.Any())
                    {
                        this.jsonLightResourceSerializer.WriteOperations(actions.Cast<ODataOperation>(), /*isAction*/ true);
                    }

                    // write "odata.functions" metadata
                    IEnumerable<ODataFunction> functions = resourceSet.Functions;
                    if (functions != null && functions.Any())
                    {
                        this.jsonLightResourceSerializer.WriteOperations(functions.Cast<ODataOperation>(), /*isAction*/ false);
                    }

                    // Write the inline count if it's available.
                    this.WriteResourceSetCount(resourceSet, /*propertyName*/null);

                    // Write the next link if it's available.
                    this.WriteResourceSetNextLink(resourceSet, /*propertyName*/null);

                    // Write the delta link if it's available.
                    this.WriteResourceSetDeltaLink(resourceSet);
                }

                // Write custom instance annotations
                this.jsonLightResourceSerializer.InstanceAnnotationWriter.WriteInstanceAnnotations(resourceSet.InstanceAnnotations, this.CurrentResourceSetScope.InstanceAnnotationWriteTracker);

                // "value":
                this.jsonWriter.WriteValuePropertyName();

                // Start array which will hold the entries in the resource set.
                this.jsonWriter.StartArrayScope();
            }
            else
            {
                // Expanded resource set.
                Debug.Assert(
                    this.ParentNestedResourceInfo != null && this.ParentNestedResourceInfo.IsCollection.HasValue && this.ParentNestedResourceInfo.IsCollection.Value,
                    "We should have verified that resource sets can only be written into IsCollection = true links in requests.");

                this.ValidateNoDeltaLinkForExpandedResourceSet(resourceSet);
                this.ValidateNoCustomInstanceAnnotationsForExpandedResourceSet(resourceSet);

                string propertyName = this.ParentNestedResourceInfo.Name;
                bool isUndeclared = (this.CurrentScope as JsonLightResourceSetScope).IsUndeclared;
                var expectedResourceTypeName =
                    this.CurrentResourceSetScope.GetOrCreateTypeContext(this.jsonLightOutputContext.WritingResponse)
                    .ExpectedResourceTypeName;

                if (this.jsonLightOutputContext.WritingResponse)
                {
                    // Write the inline count if it's available.
                    this.WriteResourceSetCount(resourceSet, propertyName);

                    // Write the next link if it's available.
                    this.WriteResourceSetNextLink(resourceSet, propertyName);

                    // Write the odata type.
                    this.jsonLightResourceSerializer.WriteResourceSetStartMetadataProperties(resourceSet, propertyName, expectedResourceTypeName, isUndeclared);

                    // And then write the property name to start the value.
                    this.jsonWriter.WriteName(propertyName);

                    // Start array which will hold the entries in the resource set.
                    this.jsonWriter.StartArrayScope();
                }
                else
                {
                    JsonLightNestedResourceInfoScope navigationLinkScope = (JsonLightNestedResourceInfoScope)this.ParentNestedResourceInfoScope;
                    if (!navigationLinkScope.ResourceSetWritten)
                    {
                        // Close the entity reference link array (if written)
                        if (navigationLinkScope.EntityReferenceLinkWritten)
                        {
                            this.jsonWriter.EndArrayScope();
                        }

                        // Write the odata type.
                        this.jsonLightResourceSerializer.WriteResourceSetStartMetadataProperties(resourceSet, propertyName, expectedResourceTypeName, isUndeclared);

                        // And then write the property name to start the value.
                        this.jsonWriter.WriteName(propertyName);

                        // Start array which will hold the entries in the resource set.
                        this.jsonWriter.StartArrayScope();

                        navigationLinkScope.ResourceSetWritten = true;
                    }
                }
            }

            this.jsonLightOutputContext.PropertyCacheHandler.EnterResourceSetScope(this.CurrentResourceSetScope.ResourceType, this.ScopeLevel);
        }

        /// <summary>
        /// Finish writing a resource set.
        /// </summary>
        /// <param name="resourceSet">The resource set to write.</param>
        protected override void EndResourceSet(ODataResourceSet resourceSet)
        {
            Debug.Assert(resourceSet != null, "resourceSet != null");

            if (this.ParentNestedResourceInfo == null && this.writingParameter)
            {
                // End the array which holds the entries in the resource set.
                this.jsonWriter.EndArrayScope();
            }
            else if (this.ParentNestedResourceInfo == null)
            {
                // End the array which holds the entries in the resource set.
                this.jsonWriter.EndArrayScope();

                // Write custom instance annotations
                this.jsonLightResourceSerializer.InstanceAnnotationWriter.WriteInstanceAnnotations(resourceSet.InstanceAnnotations, this.CurrentResourceSetScope.InstanceAnnotationWriteTracker);

                if (this.jsonLightOutputContext.WritingResponse)
                {
                    // Write the next link if it's available.
                    this.WriteResourceSetNextLink(resourceSet, /*propertyName*/null);

                    // Write the delta link if it's available.
                    this.WriteResourceSetDeltaLink(resourceSet);
                }

                // Close the object wrapper.
                this.jsonWriter.EndObjectScope();
            }
            else
            {
                Debug.Assert(
                    this.ParentNestedResourceInfo != null && this.ParentNestedResourceInfo.IsCollection.HasValue && this.ParentNestedResourceInfo.IsCollection.Value,
                    "We should have verified that resource sets can only be written into IsCollection = true links in requests.");
                string propertyName = this.ParentNestedResourceInfo.Name;

                this.ValidateNoDeltaLinkForExpandedResourceSet(resourceSet);
                this.ValidateNoCustomInstanceAnnotationsForExpandedResourceSet(resourceSet);

                if (this.jsonLightOutputContext.WritingResponse)
                {
                    // End the array which holds the entries in the resource set.
                    // NOTE: in requests we will only write the EndArray of a resource set
                    //       when we hit the nested resource info end since a nested resource info
                    //       can contain multiple resource sets that get collapesed into a single array value.
                    this.jsonWriter.EndArrayScope();

                    // Write the next link if it's available.
                    this.WriteResourceSetNextLink(resourceSet, propertyName);
                }
            }

            this.jsonLightOutputContext.PropertyCacheHandler.LeaveResourceSetScope();
        }

        /// <summary>
        /// Start writing a deferred (non-expanded) nested resource info.
        /// </summary>
        /// <param name="nestedResourceInfo">The nested resource info to write.</param>
        protected override void WriteDeferredNestedResourceInfo(ODataNestedResourceInfo nestedResourceInfo)
        {
            Debug.Assert(nestedResourceInfo != null, "nestedResourceInfo != null");
            Debug.Assert(this.jsonLightOutputContext.WritingResponse, "Deferred links are only supported in response, we should have verified this already.");

            // A deferred nested resource info is just the link metadata, no value.
            this.jsonLightResourceSerializer.WriteNavigationLinkMetadata(nestedResourceInfo, this.DuplicatePropertyNameChecker);
        }

        /// <summary>
        /// Start writing the nested resource info with content.
        /// </summary>
        /// <param name="nestedResourceInfo">The nested resource info to write.</param>
        protected override void StartNestedResourceInfoWithContent(ODataNestedResourceInfo nestedResourceInfo)
        {
            Debug.Assert(nestedResourceInfo != null, "nestedResourceInfo != null");
            Debug.Assert(!string.IsNullOrEmpty(nestedResourceInfo.Name), "The nested resource info name should have been verified by now.");

            if (this.jsonLightOutputContext.WritingResponse)
            {
                // Write @odata.context annotation for navigation property
                var containedEntitySet = this.CurrentScope.NavigationSource as IEdmContainedEntitySet;
                if (containedEntitySet != null)
                {
                    ODataContextUrlInfo info = ODataContextUrlInfo.Create(
                                                this.CurrentScope.NavigationSource,
                                                this.CurrentScope.ResourceType.FullTypeName(),
                                                containedEntitySet.NavigationProperty.Type.TypeKind() != EdmTypeKind.Collection,
                                                this.CurrentScope.ODataUri);
                    this.jsonLightResourceSerializer.WriteNestedResourceInfoContextUrl(nestedResourceInfo, info);
                }

                // Write the nested resource info metadata first. The rest is written by the content resource or resource set.
                this.jsonLightResourceSerializer.WriteNavigationLinkMetadata(nestedResourceInfo, this.DuplicatePropertyNameChecker);
            }
            else
            {
                this.WriterValidator.ValidateNestedResourceInfoHasCardinality(nestedResourceInfo);
            }
        }

        /// <summary>
        /// Finish writing nested resource info with content.
        /// </summary>
        /// <param name="nestedResourceInfo">The nested resource info to write.</param>
        protected override void EndNestedResourceInfoWithContent(ODataNestedResourceInfo nestedResourceInfo)
        {
            Debug.Assert(nestedResourceInfo != null, "nestedResourceInfo != null");

            if (!this.jsonLightOutputContext.WritingResponse)
            {
                JsonLightNestedResourceInfoScope navigationLinkScope = (JsonLightNestedResourceInfoScope)this.CurrentScope;

                // If we wrote entity reference links for a collection navigation property but no
                // resource set afterwards, we have to now close the array of links.
                if (navigationLinkScope.EntityReferenceLinkWritten && !navigationLinkScope.ResourceSetWritten && nestedResourceInfo.IsCollection.Value)
                {
                    this.jsonWriter.EndArrayScope();
                }

                // In requests, the nested resource info may have multiple entries in multiple resource sets in it; if we
                // wrote at least one resource set, close the resulting array here.
                if (navigationLinkScope.ResourceSetWritten)
                {
                    Debug.Assert(nestedResourceInfo.IsCollection.Value, "nestedResourceInfo.IsCollection.Value");
                    this.jsonWriter.EndArrayScope();
                }
            }
        }

        /// <summary>
        /// Write an entity reference link.
        /// </summary>
        /// <param name="parentNestedResourceInfo">The parent navigation link which is being written around the entity reference link.</param>
        /// <param name="entityReferenceLink">The entity reference link to write.</param>
        protected override void WriteEntityReferenceInNavigationLinkContent(ODataNestedResourceInfo parentNestedResourceInfo, ODataEntityReferenceLink entityReferenceLink)
        {
            Debug.Assert(parentNestedResourceInfo != null, "parentNestedResourceInfo != null");
            Debug.Assert(entityReferenceLink != null, "entityReferenceLink != null");
            Debug.Assert(!this.jsonLightOutputContext.WritingResponse, "Entity reference links are only supported in request, we should have verified this already.");

            // In JSON Light, we can only write entity reference links at the beginning of a navigation link in requests;
            // once we wrote a resource set, entity reference links are not allowed anymore (we require all the entity reference
            // link to come first because of the grouping in the JSON Light wire format).
            JsonLightNestedResourceInfoScope navigationLinkScope = (JsonLightNestedResourceInfoScope)this.CurrentScope;
            if (navigationLinkScope.ResourceSetWritten)
            {
                throw new ODataException(Strings.ODataJsonLightWriter_EntityReferenceLinkAfterResourceSetInRequest);
            }

            if (!navigationLinkScope.EntityReferenceLinkWritten)
            {
                // Write the property annotation for the entity reference link(s)
                this.odataAnnotationWriter.WritePropertyAnnotationName(parentNestedResourceInfo.Name, ODataAnnotationNames.ODataBind);
                Debug.Assert(parentNestedResourceInfo.IsCollection.HasValue, "parentNestedResourceInfo.IsCollection.HasValue");
                if (parentNestedResourceInfo.IsCollection.Value)
                {
                    this.jsonWriter.StartArrayScope();
                }

                navigationLinkScope.EntityReferenceLinkWritten = true;
            }

            Debug.Assert(entityReferenceLink.Url != null, "The entity reference link Url should have been validated by now.");
            this.jsonWriter.WriteValue(this.jsonLightResourceSerializer.UriToString(entityReferenceLink.Url));
        }

        /// <summary>
        /// Create a new resource set scope.
        /// </summary>
        /// <param name="resourceSet">The resource set for the new scope.</param>
        /// <param name="navigationSource">The navigation source we are going to write resources for.</param>
        /// <param name="resourceType">The structured type for the items in the resource set to be written (or null if the entity set base type should be used).</param>
        /// <param name="skipWriting">true if the content of the scope to create should not be written.</param>
        /// <param name="selectedProperties">The selected properties of this scope.</param>
        /// <param name="odataUri">The ODataUri info of this scope.</param>
        /// <param name="isUndeclared">true if the resource set is for an undeclared property</param>
        /// <returns>The newly create scope.</returns>
        protected override ResourceSetScope CreateResourceSetScope(
            ODataResourceSet resourceSet,
            IEdmNavigationSource navigationSource,
            IEdmStructuredType resourceType,
            bool skipWriting,
            SelectedPropertiesNode selectedProperties,
            ODataUri odataUri,
            bool isUndeclared)
        {
            return new JsonLightResourceSetScope(resourceSet, navigationSource, resourceType, skipWriting, selectedProperties, odataUri, isUndeclared);
        }

        /// <summary>
        /// Create a new resource scope.
        /// </summary>
        /// <param name="resource">The resource for the new scope.</param>
        /// <param name="navigationSource">The navigation source we are going to write resources for.</param>
        /// <param name="resourceType">The structured type for the items in the resource set to be written (or null if the entity set base type should be used).</param>
        /// <param name="skipWriting">true if the content of the scope to create should not be written.</param>
        /// <param name="selectedProperties">The selected properties of this scope.</param>
        /// <param name="odataUri">The ODataUri info of this scope.</param>
        /// <param name="isUndeclared">true if the resource is for an undeclared property</param>
        /// <returns>The newly create scope.</returns>
        protected override ResourceScope CreateResourceScope(ODataResource resource, IEdmNavigationSource navigationSource, IEdmStructuredType resourceType, bool skipWriting, SelectedPropertiesNode selectedProperties, ODataUri odataUri, bool isUndeclared)
        {
            return new JsonLightResourceScope(
                resource,
                this.GetResourceSerializationInfo(resource),
                navigationSource,
                resourceType,
                skipWriting,
                this.jsonLightOutputContext.MessageWriterSettings,
                selectedProperties,
                odataUri,
                isUndeclared);
        }

        /// <summary>
        /// Creates a new JSON Light nested resource info scope.
        /// </summary>
        /// <param name="writerState">The writer state for the new scope.</param>
        /// <param name="navLink">The nested resource info for the new scope.</param>
        /// <param name="navigationSource">The navigation source we are going to write entities for.</param>
        /// <param name="resourceType">The resource type for the items in the resource set to be written (or null if the navigationSource base type should be used).</param>
        /// <param name="skipWriting">true if the content of the scope to create should not be written.</param>
        /// <param name="selectedProperties">The selected properties of this scope.</param>
        /// <param name="odataUri">The ODataUri info of this scope.</param>
        /// <returns>The newly created JSON Light  nested resource info scope.</returns>
        protected override NestedResourceInfoScope CreateNestedResourceInfoScope(WriterState writerState, ODataNestedResourceInfo navLink, IEdmNavigationSource navigationSource, IEdmStructuredType resourceType, bool skipWriting, SelectedPropertiesNode selectedProperties, ODataUri odataUri)
        {
            return new JsonLightNestedResourceInfoScope(writerState, navLink, navigationSource, resourceType, skipWriting, selectedProperties, odataUri);
        }

        /// <summary>
        /// Sets resource's metadata builder based on current metadata level.
        /// </summary>
        /// <param name="resource">Resource to write.</param>
        /// <param name="typeContext">The context object to answer basic questions regarding the type of the resource or resource set.</param>
        /// <param name="selectedProperties">The selected properties of this scope.</param>
        private void InnerPrepareResourceForWriteStart(ODataResource resource, ODataResourceTypeContext typeContext, SelectedPropertiesNode selectedProperties)
        {
            ResourceScope resourceScope = (ResourceScope)this.CurrentScope;
            Debug.Assert(resourceScope != null, "resourceScope != null");

            ODataResourceMetadataBuilder builder = this.jsonLightOutputContext.MetadataLevel.CreateResourceMetadataBuilder(
                resource,
                typeContext,
                resourceScope.SerializationInfo,
                resourceScope.ResourceType,
                selectedProperties,
                this.jsonLightOutputContext.WritingResponse,
                this.jsonLightOutputContext.ODataSimplifiedOptions.EnableWritingKeyAsSegment,
                resourceScope.ODataUri);

            if (builder != null)
            {
                builder.NameAsProperty = this.BelongingNestedResourceInfo != null ? this.BelongingNestedResourceInfo.Name : null;
                builder.IsFromCollection = this.BelongingNestedResourceInfo != null && this.BelongingNestedResourceInfo.IsCollection == true;

                if (builder is ODataConventionalResourceMetadataBuilder)
                {
                    builder.ParentMetadataBuilder = this.FindParentResourceMetadataBuilder();
                }

                this.jsonLightOutputContext.MetadataLevel.InjectMetadataBuilder(resource, builder);
            }
        }

        /// <summary>
        /// Find instance of the metadata builder which belong to the parent odata resource
        /// </summary>
        /// <returns>
        /// The metadata builder of the parent odata resource
        /// Or null if there is no parent odata resource
        /// </returns>
        private ODataResourceMetadataBuilder FindParentResourceMetadataBuilder()
        {
            ResourceScope parentResourceScope = this.GetParentResourceScope();

            if (parentResourceScope != null)
            {
                ODataResource resource = parentResourceScope.Item as ODataResource;
                if (resource != null)
                {
                    return resource.MetadataBuilder;
                }
            }

            return null;
        }

        /// <summary>
        /// Writes the odata.count annotation for a resource set if it has not been written yet (and the count is specified on the resource set).
        /// </summary>
        /// <param name="resourceSet">The resource set to write the count for.</param>
        /// <param name="propertyName">The name of the expanded nav property or null for a top-level resource set.</param>
        private void WriteResourceSetCount(ODataResourceSet resourceSet, string propertyName)
        {
            Debug.Assert(resourceSet != null, "resourceSet != null");

            // If we haven't written the count yet and it's available, write it.
            long? count = resourceSet.Count;
            if (count.HasValue)
            {
                if (propertyName == null)
                {
                    this.odataAnnotationWriter.WriteInstanceAnnotationName(ODataAnnotationNames.ODataCount);
                }
                else
                {
                    this.odataAnnotationWriter.WritePropertyAnnotationName(propertyName, ODataAnnotationNames.ODataCount);
                }

                this.jsonWriter.WriteValue(count.Value);
            }
        }

        /// <summary>
        /// Writes the odata.nextLink annotation for a resource set if it has not been written yet (and the next link is specified on the resource set).
        /// </summary>
        /// <param name="resourceSet">The resource set to write the next link for.</param>
        /// <param name="propertyName">The name of the expanded nav property or null for a top-level resource set.</param>
        private void WriteResourceSetNextLink(ODataResourceSet resourceSet, string propertyName)
        {
            Debug.Assert(resourceSet != null, "resourceSet != null");

            // If we haven't written the next link yet and it's available, write it.
            Uri nextPageLink = resourceSet.NextPageLink;
            if (nextPageLink != null && !this.CurrentResourceSetScope.NextPageLinkWritten)
            {
                if (propertyName == null)
                {
                    this.odataAnnotationWriter.WriteInstanceAnnotationName(ODataAnnotationNames.ODataNextLink);
                }
                else
                {
                    this.odataAnnotationWriter.WritePropertyAnnotationName(propertyName, ODataAnnotationNames.ODataNextLink);
                }

                this.jsonWriter.WriteValue(this.jsonLightResourceSerializer.UriToString(nextPageLink));
                this.CurrentResourceSetScope.NextPageLinkWritten = true;
            }
        }

        /// <summary>
        /// Writes the odata.deltaLink annotation for a resource set if it has not been written yet (and the delta link is specified on the resource set).
        /// </summary>
        /// <param name="resourceSet">The resource set to write the delta link for.</param>
        private void WriteResourceSetDeltaLink(ODataResourceSet resourceSet)
        {
            Debug.Assert(resourceSet != null, "resourceSet != null");

            // If we haven't written the delta link yet and it's available, write it.
            Uri deltaLink = resourceSet.DeltaLink;
            if (deltaLink != null && !this.CurrentResourceSetScope.DeltaLinkWritten)
            {
                this.odataAnnotationWriter.WriteInstanceAnnotationName(ODataAnnotationNames.ODataDeltaLink);
                this.jsonWriter.WriteValue(this.jsonLightResourceSerializer.UriToString(deltaLink));
                this.CurrentResourceSetScope.DeltaLinkWritten = true;
            }
        }

        /// <summary>
        /// Validates that the ODataResourceSet.InstanceAnnotations collection is empty for the given expanded resource set.
        /// </summary>
        /// <param name="resourceSet">The expanded resource set in question.</param>
        [SuppressMessage("Microsoft.Performance", "CA1822:MarkMembersAsStatic", Justification = "An instance field is used in a debug assert.")]
        private void ValidateNoCustomInstanceAnnotationsForExpandedResourceSet(ODataResourceSet resourceSet)
        {
            Debug.Assert(resourceSet != null, "resourceSet != null");
            Debug.Assert(
                this.ParentNestedResourceInfo != null && this.ParentNestedResourceInfo.IsCollection.HasValue && this.ParentNestedResourceInfo.IsCollection.Value == true,
                "This should only be called when writing an expanded resource set.");

            if (resourceSet.InstanceAnnotations.Count > 0)
            {
                throw new ODataException(Strings.ODataJsonLightWriter_InstanceAnnotationNotSupportedOnExpandedResourceSet);
            }
        }

        /// <summary>
        /// A scope for a JSON lite resource set.
        /// </summary>
        private sealed class JsonLightResourceSetScope : ResourceSetScope
        {
            /// <summary>true if the odata.nextLink was already written, false otherwise.</summary>
            private bool nextLinkWritten;

            /// <summary>true if the odata.deltaLink was already written, false otherwise.</summary>
            private bool deltaLinkWritten;

            /// <summary>true if the resource set represents a collection of complex property or a collection navigation property that is undeclared, false otherwise.</summary>
            private bool isUndeclared;

            /// <summary>
            /// Constructor to create a new resource set scope.
            /// </summary>
            /// <param name="resourceSet">The resource set for the new scope.</param>
            /// <param name="navigationSource">The navigation source we are going to write resources for.</param>
            /// <param name="resourceType">The structured type for the items in the resource set to be written (or null if the entity set base type should be used).</param>
            /// <param name="skipWriting">true if the content of the scope to create should not be written.</param>
            /// <param name="selectedProperties">The selected properties of this scope.</param>
            /// <param name="odataUri">The ODataUri info of this scope.</param>
            /// <param name="isUndeclared">true if the resource set is for an undeclared property</param>
            internal JsonLightResourceSetScope(ODataResourceSet resourceSet, IEdmNavigationSource navigationSource, IEdmStructuredType resourceType, bool skipWriting, SelectedPropertiesNode selectedProperties, ODataUri odataUri, bool isUndeclared)
                : base(resourceSet, navigationSource, resourceType, skipWriting, selectedProperties, odataUri)
            {
                this.isUndeclared = isUndeclared;
            }

            /// <summary>
            /// true if the odata.nextLink annotation was already written, false otherwise.
            /// </summary>
            internal bool NextPageLinkWritten
            {
                get
                {
                    return this.nextLinkWritten;
                }

                set
                {
                    this.nextLinkWritten = value;
                }
            }

            /// <summary>
            /// true if the odata.deltaLink annotation was already written, false otherwise.
            /// </summary>
            internal bool DeltaLinkWritten
            {
                get
                {
                    return this.deltaLinkWritten;
                }

                set
                {
                    this.deltaLinkWritten = value;
                }
            }

            /// <summary>
            /// true if the resource set represents a collection of complex property or a collection navigation property that is undeclared, false otherwise.
            /// </summary>
            internal bool IsUndeclared
            {
                get { return isUndeclared; }
            }
        }

        /// <summary>
        /// A scope for a resource in JSON Light writer.
        /// </summary>
        private sealed class JsonLightResourceScope : ResourceScope, IODataJsonLightWriterResourceState
        {
            /// <summary>Bit field of the JSON Light metadata properties written so far.</summary>
            private int alreadyWrittenMetadataProperties;

            /// <summary>true if the resource set represents a complex property or a singleton navigation property that is undeclared, false otherwise.</summary>
            private bool isUndeclared;

            /// <summary>
            /// Constructor to create a new resource scope.
            /// </summary>
            /// <param name="resource">The resource for the new scope.</param>
            /// <param name="serializationInfo">The serialization info for the current resource.</param>
            /// <param name="navigationSource">The navigation source we are going to write resources for.</param>
            /// <param name="resourceType">The structured type for the items in the resource set to be written (or null if the entity set base type should be used).</param>
            /// <param name="skipWriting">true if the content of the scope to create should not be written.</param>
            /// <param name="writerSettings">The <see cref="ODataMessageWriterSettings"/> The settings of the writer.</param>
            /// <param name="selectedProperties">The selected properties of this scope.</param>
            /// <param name="odataUri">The ODataUri info of this scope.</param>
            /// <param name="isUndeclared">true if the resource is for an undeclared property</param>
            internal JsonLightResourceScope(
                ODataResource resource,
                ODataResourceSerializationInfo serializationInfo,
                IEdmNavigationSource navigationSource,
                IEdmStructuredType resourceType,
                bool skipWriting,
                ODataMessageWriterSettings writerSettings,
                SelectedPropertiesNode selectedProperties,
                ODataUri odataUri,
                bool isUndeclared)
                : base(resource, serializationInfo, navigationSource, resourceType, skipWriting, writerSettings, selectedProperties, odataUri)
            {
                this.isUndeclared = isUndeclared;
            }

            /// <summary>
            /// Enumeration of JSON Light metadata property flags, used to keep track of which properties were already written.
            /// </summary>
            [Flags]
            private enum JsonLightEntryMetadataProperty
            {
                /// <summary>The odata.editLink property.</summary>
                EditLink = 0x1,

                /// <summary>The odata.readLink property.</summary>
                ReadLink = 0x2,

                /// <summary>The odata.mediaEditLink property.</summary>
                MediaEditLink = 0x4,

                /// <summary>The odata.mediaReadLink property.</summary>
                MediaReadLink = 0x8,

                /// <summary>The odata.mediaContentType property.</summary>
                MediaContentType = 0x10,

                /// <summary>The odata.mediaEtag property.</summary>
                MediaETag = 0x20,
            }

            /// <summary>
            /// The resource being written.
            /// </summary>
            public ODataResource Resource
            {
                get { return (ODataResource)this.Item; }
            }

            /// <summary>
            /// true if the resource set represents a complex property or a singleton navigation property that is undeclared, false otherwise.
            /// </summary>
            public bool IsUndeclared
            {
                get { return isUndeclared; }
                set { isUndeclared = value; }
            }

            /// <summary>
            /// Flag which indicates that the odata.editLink metadata property has been written.
            /// </summary>
            public bool EditLinkWritten
            {
                get
                {
                    return this.IsMetadataPropertyWritten(JsonLightEntryMetadataProperty.EditLink);
                }

                set
                {
                    Debug.Assert(value == true, "The flag that a metadata property has been written should only ever be set from false to true.");
                    this.SetWrittenMetadataProperty(JsonLightEntryMetadataProperty.EditLink);
                }
            }

            /// <summary>
            /// Flag which indicates that the odata.readLink metadata property has been written.
            /// </summary>
            public bool ReadLinkWritten
            {
                get
                {
                    return this.IsMetadataPropertyWritten(JsonLightEntryMetadataProperty.ReadLink);
                }

                set
                {
                    Debug.Assert(value == true, "The flag that a metadata property has been written should only ever be set from false to true.");
                    this.SetWrittenMetadataProperty(JsonLightEntryMetadataProperty.ReadLink);
                }
            }

            /// <summary>
            /// Flag which indicates that the odata.mediaEditLink metadata property has been written.
            /// </summary>
            public bool MediaEditLinkWritten
            {
                get
                {
                    return this.IsMetadataPropertyWritten(JsonLightEntryMetadataProperty.MediaEditLink);
                }

                set
                {
                    Debug.Assert(value == true, "The flag that a metadata property has been written should only ever be set from false to true.");
                    this.SetWrittenMetadataProperty(JsonLightEntryMetadataProperty.MediaEditLink);
                }
            }

            /// <summary>
            /// Flag which indicates that the odata.mediaReadLink metadata property has been written.
            /// </summary>
            public bool MediaReadLinkWritten
            {
                get
                {
                    return this.IsMetadataPropertyWritten(JsonLightEntryMetadataProperty.MediaReadLink);
                }

                set
                {
                    Debug.Assert(value == true, "The flag that a metadata property has been written should only ever be set from false to true.");
                    this.SetWrittenMetadataProperty(JsonLightEntryMetadataProperty.MediaReadLink);
                }
            }

            /// <summary>
            /// Flag which indicates that the odata.mediaContentType metadata property has been written.
            /// </summary>
            public bool MediaContentTypeWritten
            {
                get
                {
                    return this.IsMetadataPropertyWritten(JsonLightEntryMetadataProperty.MediaContentType);
                }

                set
                {
                    Debug.Assert(value == true, "The flag that a metadata property has been written should only ever be set from false to true.");
                    this.SetWrittenMetadataProperty(JsonLightEntryMetadataProperty.MediaContentType);
                }
            }

            /// <summary>
            /// Flag which indicates that the odata.mediaEtag metadata property has been written.
            /// </summary>
            public bool MediaETagWritten
            {
                get
                {
                    return this.IsMetadataPropertyWritten(JsonLightEntryMetadataProperty.MediaETag);
                }

                set
                {
                    Debug.Assert(value == true, "The flag that a metadata property has been written should only ever be set from false to true.");
                    this.SetWrittenMetadataProperty(JsonLightEntryMetadataProperty.MediaETag);
                }
            }

            /// <summary>
            /// Marks the <paramref name="jsonLightMetadataProperty"/> as written in this resource scope.
            /// </summary>
            /// <param name="jsonLightMetadataProperty">The metadta property which was written.</param>
            private void SetWrittenMetadataProperty(JsonLightEntryMetadataProperty jsonLightMetadataProperty)
            {
                Debug.Assert(!this.IsMetadataPropertyWritten(jsonLightMetadataProperty), "Can't write the same metadata property twice.");
                this.alreadyWrittenMetadataProperties |= (int)jsonLightMetadataProperty;
            }

            /// <summary>
            /// Determines if the <paramref name="jsonLightMetadataProperty"/> was already written for this resource scope.
            /// </summary>
            /// <param name="jsonLightMetadataProperty">The metadata property to test for.</param>
            /// <returns>true if the <paramref name="jsonLightMetadataProperty"/> was already written for this resource scope; false otherwise.</returns>
            private bool IsMetadataPropertyWritten(JsonLightEntryMetadataProperty jsonLightMetadataProperty)
            {
                return (this.alreadyWrittenMetadataProperties & (int)jsonLightMetadataProperty) == (int)jsonLightMetadataProperty;
            }
        }

        /// <summary>
        /// A scope for a JSON Light nested resource info.
        /// </summary>
        private sealed class JsonLightNestedResourceInfoScope : NestedResourceInfoScope
        {
            /// <summary>true if we have already written an entity reference link for this nested resource info in requests; otherwise false.</summary>
            private bool entityReferenceLinkWritten;

            /// <summary>true if we have written at least one resource set for this nested resource info in requests; otherwise false.</summary>
            private bool resourceSetWritten;

            /// <summary>
            /// Constructor to create a new JSON Light nested resource info scope.
            /// </summary>
            /// <param name="writerState">The writer state for the new scope.</param>
            /// <param name="navLink">The nested resource info for the new scope.</param>
            /// <param name="navigationSource">The navigation source we are going to write entities for.</param>
            /// <param name="resourceType">The resource type for the items in the resource set to be written (or null if the navigationSource base type should be used).</param>
            /// <param name="skipWriting">true if the content of the scope to create should not be written.</param>
            /// <param name="selectedProperties">The selected properties of this scope.</param>
            /// <param name="odataUri">The ODataUri info of this scope.</param>
            internal JsonLightNestedResourceInfoScope(WriterState writerState, ODataNestedResourceInfo navLink, IEdmNavigationSource navigationSource, IEdmStructuredType resourceType, bool skipWriting, SelectedPropertiesNode selectedProperties, ODataUri odataUri)
                : base(writerState, navLink, navigationSource, resourceType, skipWriting, selectedProperties, odataUri)
            {
            }

            /// <summary>
            /// true if we have already written an entity reference link for this navigation link in requests; otherwise false.
            /// </summary>
            internal bool EntityReferenceLinkWritten
            {
                get
                {
                    return this.entityReferenceLinkWritten;
                }

                set
                {
                    this.entityReferenceLinkWritten = value;
                }
            }

            /// <summary>
            /// true if we have written at least one resource set for this nested resource info in requests; otherwise false.
            /// </summary>
            internal bool ResourceSetWritten
            {
                get
                {
                    return this.resourceSetWritten;
                }

                set
                {
                    this.resourceSetWritten = value;
                }
            }

            /// <summary>
            /// Clones this JSON Light nested resource info scope and sets a new writer state.
            /// </summary>
            /// <param name="newWriterState">The writer state to set.</param>
            /// <returns>The cloned nested resource info scope with the specified writer state.</returns>
            internal override NestedResourceInfoScope Clone(WriterState newWriterState)
            {
                return new JsonLightNestedResourceInfoScope(newWriterState, (ODataNestedResourceInfo)this.Item, this.NavigationSource, this.ResourceType, this.SkipWriting, this.SelectedProperties, this.ODataUri)
                {
                    EntityReferenceLinkWritten = this.entityReferenceLinkWritten,
                    ResourceSetWritten = this.resourceSetWritten,
                };
            }
        }
    }
}
