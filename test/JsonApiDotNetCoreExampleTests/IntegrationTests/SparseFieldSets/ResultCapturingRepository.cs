using System.Collections.Generic;
using System.Threading.Tasks;
using JsonApiDotNetCore.Configuration;
using JsonApiDotNetCore.Queries;
using JsonApiDotNetCore.Repositories;
using JsonApiDotNetCore.Resources;
using Microsoft.Extensions.Logging;

namespace JsonApiDotNetCoreExampleTests.IntegrationTests.SparseFieldSets
{
    /// <summary>
    /// Enables sparse fieldset tests to verify which fields were (not) retrieved from the database.
    /// </summary>
    public sealed class ResultCapturingRepository<TResource> : EntityFrameworkCoreRepository<TResource>
        where TResource : class, IIdentifiable<int>
    {
        private readonly ResourceCaptureStore _captureStore;

        public ResultCapturingRepository(
            ITargetedFields targetedFields,
            IDbContextResolver contextResolver,
            IResourceGraph resourceGraph,
            IResourceFactory resourceFactory,
            IEnumerable<IQueryConstraintProvider> constraintProviders,
            IResourceAccessor resourceAccessor,
            ILoggerFactory loggerFactory,
            ResourceCaptureStore captureStore)
            : base(targetedFields, contextResolver, resourceGraph, resourceFactory,
                constraintProviders, resourceAccessor, loggerFactory)
        {
            _captureStore = captureStore;
        }

        public override async Task<IReadOnlyCollection<TResource>> GetAsync(QueryLayer layer)
        {
            var resources = await base.GetAsync(layer);

            _captureStore.Add(resources);

            return resources;
        }
    }
}
