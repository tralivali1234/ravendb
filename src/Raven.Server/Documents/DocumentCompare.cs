﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Raven.Client;
using Raven.Client.Documents.Operations.Attachments;
using Sparrow;
using Sparrow.Json;

namespace Raven.Server.Documents
{
    public static class DocumentCompare
    {
        public static unsafe DocumentCompareResult IsEqualTo(BlittableJsonReaderObject original, BlittableJsonReaderObject modified,
            bool tryMergeMetadataConflicts)
        {
            if (ReferenceEquals(original, modified))
                return DocumentCompareResult.Equal;

            if (original == null || modified == null)
                return DocumentCompareResult.NotEqual;

            BlittableJsonReaderObject.AssertNoModifications(original, nameof(original), true);
            BlittableJsonReaderObject.AssertNoModifications(modified, nameof(modified), true);

            if (original.Size == modified.Size)
            {
                // if this didn't change, we can check the raw memory directly.
                if(Memory.Compare(original.BasePointer, modified.BasePointer, original.Size) == 0)
                    return DocumentCompareResult.Equal;
            }

            // Performance improvement: We compare the metadata first 
            // because that most of the time the metadata itself won't be the equal, so no need to compare all values

            var result = IsMetadataEqualTo(original, modified, tryMergeMetadataConflicts);
            if (result == DocumentCompareResult.NotEqual)
                return DocumentCompareResult.NotEqual;

            if (ComparePropertiesExceptStartingWithAt(original, modified) == DocumentCompareResult.NotEqual)
                return DocumentCompareResult.NotEqual;

            return result;
        }

        private static DocumentCompareResult IsMetadataEqualTo(BlittableJsonReaderObject current, BlittableJsonReaderObject modified, bool tryMergeConflicts)
        {
            if (modified == null)
                return DocumentCompareResult.NotEqual;

            current.TryGet(Constants.Documents.Metadata.Key, out BlittableJsonReaderObject currentMetadata);
            modified.TryGet(Constants.Documents.Metadata.Key, out BlittableJsonReaderObject objMetadata);

            if (currentMetadata == null && objMetadata == null)
                return DocumentCompareResult.Equal;

            if (currentMetadata == null || objMetadata == null)
            {
                if (tryMergeConflicts)
                {
                    DocumentCompareResult result = DocumentCompareResult.Equal;

                    if (currentMetadata == null)
                        currentMetadata = objMetadata;

                    // If there is a conflict on @metadata with @counters and/or with @attachments, we know how to resolve it.
                    var propertyNames = currentMetadata.GetPropertyNames();
                    if (propertyNames.Contains(Constants.Documents.Metadata.Counters, StringComparer.OrdinalIgnoreCase))
                        result |= DocumentCompareResult.CountersNotEqual;

                    if (propertyNames.Contains(Constants.Documents.Metadata.Attachments, StringComparer.OrdinalIgnoreCase))
                        result |= DocumentCompareResult.AttachmentsNotEqual;

                    return result != DocumentCompareResult.Equal ? result : DocumentCompareResult.NotEqual;
                }

                return DocumentCompareResult.NotEqual;
            }

            return ComparePropertiesExceptStartingWithAt(currentMetadata, objMetadata, true, tryMergeConflicts);
        }

        private static DocumentCompareResult ComparePropertiesExceptStartingWithAt(BlittableJsonReaderObject current, BlittableJsonReaderObject modified, 
            bool isMetadata = false, bool tryMergeMetadataConflicts = false)
        {
            var resolvedAttachmentConflict = false;
            var resolvedCountersConflict = false;

            var properties = new HashSet<string>(current.GetPropertyNames());
            foreach (var propertyName in modified.GetPropertyNames())
            {
                properties.Add(propertyName);
            }

            foreach (var property in properties)
            {
                if (property[0] == '@')
                {
                    if (isMetadata)
                    {
                        if (property.Equals(Constants.Documents.Metadata.Attachments, StringComparison.OrdinalIgnoreCase))
                        {
                            if (tryMergeMetadataConflicts)
                            {
                                if (current.TryGetMember(property, out object _) == false ||
                                    modified.TryGetMember(property, out object _) == false)
                                {
                                    // Resolve when just 1 document have attachments
                                    resolvedAttachmentConflict = true;
                                    continue;
                                }

                                resolvedAttachmentConflict = ShouldResolveAttachmentsConflict(current, modified);
                                if (resolvedAttachmentConflict)
                                    continue;

                                return DocumentCompareResult.NotEqual;
                            }
                        }
                        else if (property.Equals(Constants.Documents.Metadata.Counters, StringComparison.OrdinalIgnoreCase))
                        {
                            if (tryMergeMetadataConflicts)
                            {
                                if (current.TryGetMember(property, out object _) == false ||
                                    modified.TryGetMember(property, out object _) == false)
                                {
                                    // Resolve when just 1 document have counters
                                    resolvedCountersConflict = true;
                                    continue;
                                }

                                resolvedCountersConflict = ShouldResolveCountersConflict(current, modified);
                                continue;
                            }
                        }
                        else if (property.Equals(Constants.Documents.Metadata.Collection, StringComparison.OrdinalIgnoreCase) == false)
                            continue;
                    }
                    else if (property.Equals(Constants.Documents.Metadata.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }

                if (current.TryGetMember(property, out object currentProperty) == false ||
                    modified.TryGetMember(property, out object modifiedProperty) == false)
                {
                    return DocumentCompareResult.NotEqual;
                }

                if (Equals(currentProperty, modifiedProperty) == false)
                {
                    return DocumentCompareResult.NotEqual;
                }
            }

            var shouldRecreateAttachment = resolvedAttachmentConflict ? DocumentCompareResult.AttachmentsNotEqual : DocumentCompareResult.None;
            var shouldRecreateCounters = resolvedCountersConflict ? DocumentCompareResult.CountersNotEqual : DocumentCompareResult.None;
            
            return DocumentCompareResult.Equal | shouldRecreateAttachment | shouldRecreateCounters;
        }

        private static bool ShouldResolveAttachmentsConflict(BlittableJsonReaderObject currentMetadata, BlittableJsonReaderObject modifiedMetadata)
        {
            currentMetadata.TryGet(Constants.Documents.Metadata.Attachments, out BlittableJsonReaderArray currentAttachments);
            modifiedMetadata.TryGet(Constants.Documents.Metadata.Attachments, out BlittableJsonReaderArray modifiedAttachments);
            Debug.Assert(currentAttachments != null || modifiedAttachments != null, "Cannot happen. We verified that we have a conflict in @attachments.");

            var currentAttachmentNames = new Dictionary<string, BlittableJsonReaderObject>(StringComparer.OrdinalIgnoreCase);
            if (currentAttachments != null)
            {
                foreach (BlittableJsonReaderObject attachment in currentAttachments)
                {
                    if (attachment.TryGet(nameof(AttachmentName.Name), out string name) == false)
                        return false;   // Attachment must have a name. The user modified the value?

                    if (currentAttachmentNames.ContainsKey(name))
                        // The node itself has a conflict
                        return false;
                    currentAttachmentNames.Add(name, attachment);
                }
            }

            var modifiedAttachmentNames = new Dictionary<string, BlittableJsonReaderObject>(StringComparer.OrdinalIgnoreCase);
            if (modifiedAttachments != null)
            {
                foreach (BlittableJsonReaderObject attachment in modifiedAttachments)
                {
                    if (attachment.TryGet(nameof(AttachmentName.Name), out string name) == false)
                        return false;   // Attachment must have a name. The user modified the value?

                    if (modifiedAttachmentNames.ContainsKey(name))
                        // The node itself has a conflict
                        return false;
                    modifiedAttachmentNames.Add(name, attachment);
                }
            }

            foreach (var attachment in currentAttachmentNames)
            {
                if (modifiedAttachmentNames.TryGetValue(attachment.Key, out var modifiedAttachment))
                {
                    if (ComparePropertiesExceptStartingWithAt(attachment.Value, modifiedAttachment) == DocumentCompareResult.NotEqual)
                        return false;

                    modifiedAttachmentNames.Remove(attachment.Key);
                }
            }

            return true;
        }


        private static bool ShouldResolveCountersConflict(BlittableJsonReaderObject currentMetadata, BlittableJsonReaderObject modifiedMetadata)
        {
            currentMetadata.TryGet(Constants.Documents.Metadata.Counters, out BlittableJsonReaderArray currentCounters);
            modifiedMetadata.TryGet(Constants.Documents.Metadata.Counters, out BlittableJsonReaderArray modifiedCounters);
            Debug.Assert(currentCounters != null || modifiedCounters != null, "Cannot happen. We verified that we have a conflict in @counters.");

            if (currentCounters == null)
                return true;

            return currentCounters.Length != modifiedCounters.Length ||
                   !currentCounters.All(modifiedCounters.Contains);
        }

    }

    [Flags]
    public enum DocumentCompareResult
    {
        None = 0,

        NotEqual = 0x1,
        Equal = 0x2,

        AttachmentsNotEqual = 0x4,

        CountersNotEqual = 0x8
    }
}
