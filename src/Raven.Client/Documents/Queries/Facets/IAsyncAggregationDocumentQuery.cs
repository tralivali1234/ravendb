using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Raven.Client.Documents.Queries.Facets
{
    public interface IAsyncAggregationDocumentQuery<T>
    {
        IAsyncAggregationDocumentQuery<T> AndAggregateBy(Action<IFacetBuilder<T>> builder = null);
        IAsyncAggregationDocumentQuery<T> AndAggregateBy(FacetBase facet);
        Task<Dictionary<string, FacetResult>> ExecuteAsync(CancellationToken token = default);
        Lazy<Task<Dictionary<string, FacetResult>>> ExecuteLazyAsync(Action<Dictionary<string, FacetResult>> onEval = null, CancellationToken token = default);
    }
}
