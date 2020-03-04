﻿using System.Collections.Generic;
using Raven.Client.Documents.Indexes;
using Raven.Server.Documents.Indexes.Static;
using Raven.Server.Documents.Indexes.Static.Roslyn.Rewriters;

namespace Raven.Server.Documents.Indexes.MapReduce.Static
{
    public class MapReduceIndexDefinition : MapIndexDefinition
    {
        public MapReduceIndexDefinition(IndexDefinition definition, HashSet<string> collections, string[] outputFields,
            CompiledIndexField[] groupByFields, bool hasDynamicFields)
            : base(definition, collections, outputFields, hasDynamicFields)
        {
            GroupByFields = new HashSet<CompiledIndexField>(groupByFields);
            OutputReduceToCollection = definition.OutputReduceToCollection;
        }

        public HashSet<CompiledIndexField> GroupByFields { get; }
        public string OutputReduceToCollection { get; }
    }
}
