using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using JsonApiDotNetCore.Configuration;
using JsonApiDotNetCore.Errors;
using JsonApiDotNetCore.Hooks.Internal;
using JsonApiDotNetCore.Hooks.Internal.Execution;
using JsonApiDotNetCore.Middleware;
using JsonApiDotNetCore.Queries;
using JsonApiDotNetCore.Queries.Expressions;
using JsonApiDotNetCore.Repositories;
using JsonApiDotNetCore.Resources;
using JsonApiDotNetCore.Resources.Annotations;
using Microsoft.Extensions.Logging;

namespace JsonApiDotNetCore.Services
{
    /// <inheritdoc />
    public class JsonApiResourceService<TResource, TId> :
        IResourceService<TResource, TId>
        where TResource : class, IIdentifiable<TId>
    {
        private readonly IResourceRepository<TResource, TId> _repository;
        private readonly IQueryLayerComposer _queryLayerComposer;
        private readonly IPaginationContext _paginationContext;
        private readonly IJsonApiOptions _options;
        private readonly TraceLogWriter<JsonApiResourceService<TResource, TId>> _traceWriter;
        private readonly IJsonApiRequest _request;
        private readonly IResourceChangeTracker<TResource> _resourceChangeTracker;
        private readonly IResourceFactory _resourceFactory;
        private readonly IResourceAccessor _resourceAccessor;
        private readonly ITargetedFields _targetedFields;
        private readonly IResourceContextProvider _provider;
        private readonly IResourceHookExecutor _hookExecutor;

        public JsonApiResourceService(
            IResourceRepository<TResource, TId> repository,
            IQueryLayerComposer queryLayerComposer,
            IPaginationContext paginationContext,
            IJsonApiOptions options,
            ILoggerFactory loggerFactory,
            IJsonApiRequest request,
            IResourceChangeTracker<TResource> resourceChangeTracker,
            IResourceFactory resourceFactory,
            IResourceAccessor resourceAccessor,
            ITargetedFields targetedFields,
            IResourceContextProvider provider,
            IResourceHookExecutor hookExecutor = null)
        {
            if (loggerFactory == null) throw new ArgumentNullException(nameof(loggerFactory));

            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _queryLayerComposer = queryLayerComposer ?? throw new ArgumentNullException(nameof(queryLayerComposer));
            _paginationContext = paginationContext ?? throw new ArgumentNullException(nameof(paginationContext));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _traceWriter = new TraceLogWriter<JsonApiResourceService<TResource, TId>>(loggerFactory);
            _request = request ?? throw new ArgumentNullException(nameof(request));
            _resourceChangeTracker = resourceChangeTracker ?? throw new ArgumentNullException(nameof(resourceChangeTracker));
            _resourceFactory = resourceFactory ?? throw new ArgumentNullException(nameof(resourceFactory));
            _resourceAccessor = resourceAccessor ?? throw new ArgumentNullException(nameof(resourceAccessor));
            _targetedFields = targetedFields ?? throw new ArgumentNullException(nameof(targetedFields));
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _hookExecutor = hookExecutor;
        }

        /// <inheritdoc />
        // triggered by POST /articles
        public virtual async Task<TResource> CreateAsync(TResource resource)
        {
            _traceWriter.LogMethodStart(new {resource});
            if (resource == null) throw new ArgumentNullException(nameof(resource));

            if (_hookExecutor != null)
            {
                resource = _hookExecutor.BeforeCreate(AsList(resource), ResourcePipeline.Post).Single();
            }
            
            if (HasNonNullRelationshipAssignments(resource, out var assignments))
            {
                await AssertRelationshipValuesExistAsync(assignments);
            }
            
            await _repository.CreateAsync(resource);

            resource = await GetPrimaryResourceById(resource.Id, true);
            
            
            if (_hookExecutor != null)
            {
                _hookExecutor.AfterCreate(AsList(resource), ResourcePipeline.Post);
                resource = _hookExecutor.OnReturn(AsList(resource), ResourcePipeline.Post).Single();
            }

            return resource;
        }

        /// <inheritdoc />
        public virtual async Task<IReadOnlyCollection<TResource>> GetAsync()
        {
            _traceWriter.LogMethodStart();

            _hookExecutor?.BeforeRead<TResource>(ResourcePipeline.Get);

            if (_options.IncludeTotalResourceCount)
            {
                var topFilter = _queryLayerComposer.GetTopFilter();
                _paginationContext.TotalResourceCount = await _repository.CountAsync(topFilter);

                if (_paginationContext.TotalResourceCount == 0)
                {
                    return Array.Empty<TResource>();
                }
            }

            var queryLayer = _queryLayerComposer.Compose(_request.PrimaryResource);
            var resources = await _repository.GetAsync(queryLayer);

            if (_hookExecutor != null)
            {
                _hookExecutor.AfterRead(resources, ResourcePipeline.Get);
                return _hookExecutor.OnReturn(resources, ResourcePipeline.Get).ToArray();
            }

            if (queryLayer.Pagination?.PageSize != null && queryLayer.Pagination.PageSize.Value == resources.Count)
            {
                _paginationContext.IsPageFull = true;
            }

            return resources;
        }

        /// <inheritdoc />
        // triggered by GET /articles/{id}
        public virtual async Task<TResource> GetAsync(TId id)
        {
            _traceWriter.LogMethodStart(new {id});

            _hookExecutor?.BeforeRead<TResource>(ResourcePipeline.GetSingle, id.ToString());

            var primaryResource = await GetPrimaryResourceById(id, true);

            if (_hookExecutor != null)
            {
                _hookExecutor.AfterRead(AsList(primaryResource), ResourcePipeline.GetSingle);
                return _hookExecutor.OnReturn(AsList(primaryResource), ResourcePipeline.GetSingle).Single();
            }

            return primaryResource;
        }

        private async Task<TResource> GetPrimaryResourceById(TId id, bool allowTopSparseFieldSet)
        {
            var primaryLayer = _queryLayerComposer.Compose(_request.PrimaryResource);
            primaryLayer.Sort = null;
            primaryLayer.Pagination = null;
            primaryLayer.Filter = IncludeFilterById(id, primaryLayer.Filter);

            if (!allowTopSparseFieldSet && primaryLayer.Projection != null)
            {
                // Discard any ?fields= or attribute exclusions from ResourceDefinition, because we need the full record.

                while (primaryLayer.Projection.Any(p => p.Key is AttrAttribute))
                {
                    primaryLayer.Projection.Remove(primaryLayer.Projection.First(p => p.Key is AttrAttribute));
                }
            }

            var primaryResources = await _repository.GetAsync(primaryLayer);

            var primaryResource = primaryResources.SingleOrDefault();
            AssertPrimaryResourceExists(primaryResource);

            return primaryResource;
        }

        private FilterExpression IncludeFilterById(TId id, FilterExpression existingFilter)
        {
            var primaryIdAttribute = _request.PrimaryResource.Attributes.Single(a => a.Property.Name == nameof(Identifiable.Id));

            FilterExpression filterById = new ComparisonExpression(ComparisonOperator.Equals,
                new ResourceFieldChainExpression(primaryIdAttribute), new LiteralConstantExpression(id.ToString()));

            return existingFilter == null
                ? filterById
                : new LogicalExpression(LogicalOperator.And, new[] {filterById, existingFilter});
        }

        /// <inheritdoc />
        // triggered by GET /articles/{id}/{relationshipName}
        public virtual async Task<object> GetSecondaryAsync(TId id, string relationshipName)
        {
            _traceWriter.LogMethodStart(new {id, relationshipName});
            if (relationshipName == null) throw new ArgumentNullException(nameof(relationshipName));

            AssertRelationshipExists(relationshipName);

            _hookExecutor?.BeforeRead<TResource>(ResourcePipeline.GetRelationship, id.ToString());

            var secondaryLayer = _queryLayerComposer.Compose(_request.SecondaryResource);
            var primaryLayer = _queryLayerComposer.WrapLayerForSecondaryEndpoint(secondaryLayer, _request.PrimaryResource, id, _request.Relationship);

            if (_request.IsCollection && _options.IncludeTotalResourceCount)
            {
                // TODO: Consider support for pagination links on secondary resource collection. This requires to call Count() on the inverse relationship (which may not exist).
                // For /blogs/{id}/articles we need to execute Count(Articles.Where(article => article.Blog.Id == 1 && article.Blog.existingFilter))) to determine TotalResourceCount.
                // This also means we need to invoke ResourceRepository<Article>.CountAsync() from ResourceService<Blog>.
                // And we should call BlogResourceDefinition.OnApplyFilter to filter out soft-deleted blogs and translate from equals('IsDeleted','false') to equals('Blog.IsDeleted','false')
            }

            var primaryResources = await _repository.GetAsync(primaryLayer);
            
            var primaryResource = primaryResources.SingleOrDefault();
            AssertPrimaryResourceExists(primaryResource);

            if (_hookExecutor != null)
            {   
                _hookExecutor.AfterRead(AsList(primaryResource), ResourcePipeline.GetRelationship);
                primaryResource = _hookExecutor.OnReturn(AsList(primaryResource), ResourcePipeline.GetRelationship).Single();
            }

            var secondaryResource = _request.Relationship.GetValue(primaryResource);

            if (secondaryResource is ICollection secondaryResources && 
                secondaryLayer.Pagination?.PageSize != null && secondaryLayer.Pagination.PageSize.Value == secondaryResources.Count)
            {
                _paginationContext.IsPageFull = true;
            }

            return secondaryResource;
        }

        /// <inheritdoc />\
        // triggered by PATCH /articles/{id}
        public virtual async Task<TResource> UpdateAsync(TId id, TResource requestResource)
        {
            _traceWriter.LogMethodStart(new {id, requestResource});
            if (requestResource == null) throw new ArgumentNullException(nameof(requestResource));
            
            TResource databaseResource = await GetPrimaryResourceById(id, false);

            if (HasNonNullRelationshipAssignments(requestResource, out var assignments))
            {
                await AssertRelationshipValuesExistAsync(assignments);
            }
            
            _resourceChangeTracker.SetInitiallyStoredAttributeValues(databaseResource);
            _resourceChangeTracker.SetRequestedAttributeValues(requestResource);

            if (_hookExecutor != null)
            {
                requestResource = _hookExecutor.BeforeUpdate(AsList(requestResource), ResourcePipeline.Patch).Single();
            }

            await _repository.UpdateAsync(requestResource, databaseResource);

            if (_hookExecutor != null)
            {
                _hookExecutor.AfterUpdate(AsList(databaseResource), ResourcePipeline.Patch);
                _hookExecutor.OnReturn(AsList(databaseResource), ResourcePipeline.Patch);
            }

            _repository.FlushFromCache(databaseResource);
            TResource afterResource = await GetPrimaryResourceById(id, false);
            _resourceChangeTracker.SetFinallyStoredAttributeValues(afterResource);

            bool hasImplicitChanges = _resourceChangeTracker.HasImplicitChanges();
            return hasImplicitChanges ? afterResource : null;
        }

        /// <inheritdoc />
        // triggered by DELETE /articles/{id
        public virtual async Task DeleteAsync(TId id)
        {
            _traceWriter.LogMethodStart(new {id});

            if (_hookExecutor != null)
            {
                var resource = _resourceFactory.CreateInstance<TResource>();
                resource.Id = id;

                _hookExecutor.BeforeDelete(AsList(resource), ResourcePipeline.Delete);
            }

            var succeeded = await _repository.DeleteAsync(id);

            if (_hookExecutor != null)
            {
                var resource = _resourceFactory.CreateInstance<TResource>();
                resource.Id = id;

                _hookExecutor.AfterDelete(AsList(resource), ResourcePipeline.Delete, succeeded);
            }

            if (!succeeded)
            {
                AssertPrimaryResourceExists(null);
            }
        }
        
        /// <inheritdoc />
        // triggered by POST /articles/{id}/relationships/{relationshipName}
        public async Task AddRelationshipAsync(TId id, string relationshipName, IEnumerable<IIdentifiable> relationships)
        {
            /*
             * APPROACH:
             * - get all relationships through repository
             * - construct accurate relationshipsId list
             * - use repo.UpdateAsync method. POST vs PATCH part of the spec will be abstracted away from repo this way
             * - EF Core:
             *     one-to-many: will probably iterate through list and set FK to primaryResource.id.  C ~ relationshipsId.Count
             *         X optimal performance: we could do this without getting any data. Now it does.
             *     many-to-many: add new join table records. What if they already exist?
             *         X here we will always need to get the join table records first to make sure we are not inserting one that already exists, so no performance loss
             *
             * Conclusion
             * => for creation we only need to fetch data if relationships is many-to-many. so for many-to-many it doesnt matter if we create reuse repo.UpdateAsync,
             * or not. For to-many, we never need to fetch data, so we wont leverage this performance opportunity if we re-use repo.UpdateAsync
             *
             *
             * Rectification:
             * before adding a relationship we need to see if it acutally exists. If not we must return 404.
             * 
             *
             * So new conclusion: we always need to fetch the to be added or deleted relationship
             * we dont have to fetch the current state of the relationship.
             *     unless many-to-many: I think query wil fail if we create double entry in jointable when the pre-existing entry is not being tracked in dbcontext. 
             */
            
            _traceWriter.LogMethodStart(new {id, relationshipName, relationships});
            if (relationshipName == null) throw new ArgumentNullException(nameof(relationshipName));

            AssertRelationshipExists(relationshipName);
            AssertRelationshipIsToMany(relationshipName);
            
            var relationship = GetRelationshipAttribute(relationshipName);
            await AssertRelationshipValuesExistAsync((relationship, relationships));
            
            throw new NotImplementedException();
        }

        /// <inheritdoc />
        // triggered by GET /articles/{id}/relationships/{relationshipName}
        public virtual async Task<TResource> GetRelationshipAsync(TId id, string relationshipName)
        {
            _traceWriter.LogMethodStart(new {id, relationshipName});
            if (relationshipName == null) throw new ArgumentNullException(nameof(relationshipName));

            AssertRelationshipExists(relationshipName);

            _hookExecutor?.BeforeRead<TResource>(ResourcePipeline.GetRelationship, id.ToString());

            var secondaryLayer = _queryLayerComposer.Compose(_request.SecondaryResource);
            secondaryLayer.Projection = _queryLayerComposer.GetSecondaryProjectionForRelationshipEndpoint(_request.SecondaryResource);
            secondaryLayer.Include = null;

            var primaryLayer = _queryLayerComposer.WrapLayerForSecondaryEndpoint(secondaryLayer, _request.PrimaryResource, id, _request.Relationship);

            var primaryResources = await _repository.GetAsync(primaryLayer);

            var primaryResource = primaryResources.SingleOrDefault();
            AssertPrimaryResourceExists(primaryResource);

            if (_hookExecutor != null)
            {
                _hookExecutor.AfterRead(AsList(primaryResource), ResourcePipeline.GetRelationship);
                primaryResource = _hookExecutor.OnReturn(AsList(primaryResource), ResourcePipeline.GetRelationship).Single();
            }

            return primaryResource;
        }

        /// <inheritdoc />
        // triggered by PATCH /articles/{id}/relationships/{relationshipName}
        public virtual async Task SetRelationshipAsync(TId id, string relationshipName, object relationships)
        {
            _traceWriter.LogMethodStart(new {id, relationshipName, relationships});
            if (relationshipName == null) throw new ArgumentNullException(nameof(relationshipName));

            AssertRelationshipExists(relationshipName);
            
            var secondaryLayer = _queryLayerComposer.Compose(_request.SecondaryResource);
            secondaryLayer.Projection = _queryLayerComposer.GetSecondaryProjectionForRelationshipEndpoint(_request.SecondaryResource);
            secondaryLayer.Include = null;

            var primaryLayer = _queryLayerComposer.WrapLayerForSecondaryEndpoint(secondaryLayer, _request.PrimaryResource, id, _request.Relationship);
            primaryLayer.Projection = null;

            primaryLayer.Include = null;
            
            var primaryResources = await _repository.GetAsync(primaryLayer);

            var primaryResource = primaryResources.SingleOrDefault();
            AssertPrimaryResourceExists(primaryResource);
            
            await AssertRelationshipValuesExistAsync((_request.Relationship, relationships));
            
            if (_hookExecutor != null)
            {
                primaryResource = _hookExecutor.BeforeUpdate(AsList(primaryResource), ResourcePipeline.PatchRelationship).Single();
            }

            string[] relationshipIds = null;
            if (relationships != null)
            {
                relationshipIds = _request.Relationship is HasOneAttribute
                    ? new[] {TypeHelper.GetIdValue((IIdentifiable) relationships)}
                    : ((IEnumerable<IIdentifiable>) relationships).Select(TypeHelper.GetIdValue).ToArray();
            }

            await _repository.UpdateRelationshipAsync(primaryResource, _request.Relationship, relationshipIds ?? Array.Empty<string>());

            if (_hookExecutor != null && primaryResource != null)
            {
                _hookExecutor.AfterUpdate(AsList(primaryResource), ResourcePipeline.PatchRelationship);
            }
        }

        /// <inheritdoc />
        // triggered by DELETE /articles/{id}/relationships/{relationshipName}
        public async Task DeleteRelationshipAsync(TId id, string relationshipName, IEnumerable<IIdentifiable> relationships)
        {
            /*
             * APPROACH ONE:
             * - get all relationships through repository
             * - construct accurate relationshipsId list
             * - use repo.UpdateAsync method. POST vs PATCH part of the spec will be abstracted away from repo this way
             * - EF Core:
             *     one-to-many: will probably iterate through list and set FK to primaryResource.id.  C ~ amount of new ids
             *         X optimal performance: we could do this without getting any data. Now it does.
             *     many-to-many: iterates over list and creates DELETE query per removed id. C ~ amount of new ids
             *         X delete join table records. No need to fetch them first. Now it does.
             *
             * Conclusion
             *  => for delete we wont ever need to fetch data first. If we reuse repo.UpdateAsync,
             *  we wont leverage this performance opportunity
             */
            
            _traceWriter.LogMethodStart(new {id, relationshipName, relationships});
            if (relationshipName == null) throw new ArgumentNullException(nameof(relationshipName));

            AssertRelationshipExists(relationshipName);
            AssertRelationshipIsToMany(relationshipName);
            
            await AssertRelationshipValuesExistAsync((_request.Relationship, relationships));

            throw new NotImplementedException();
        }

        private bool HasNonNullRelationshipAssignments(TResource requestResource, out (RelationshipAttribute, object)[] assignments)
        {
            assignments = _targetedFields.Relationships
                .Select(attr => (attr, attr.GetValue(requestResource)))
                .Where(t =>
                {
                    if (t.Item1 is HasOneAttribute)
                    {
                        return t.Item2 != null;
                    }

                    return ((IEnumerable<IIdentifiable>) t.Item2).Any();
                }).ToArray();

            return assignments.Any();
        }

        private RelationshipAttribute GetRelationshipAttribute(string relationshipName)
        {
            return _provider.GetResourceContext<TResource>().Relationships.Single(attr => attr.PublicName == relationshipName);
        }

        private void AssertPrimaryResourceExists(TResource resource)
        {
            if (resource == null)
            {
                throw new ResourceNotFoundException(_request.PrimaryId, _request.PrimaryResource.PublicName);
            }
        }

        private void AssertRelationshipExists(string relationshipName)
        {
            var relationship = _request.Relationship;
            if (relationship == null)
            {
                throw new RelationshipNotFoundException(relationshipName, _request.PrimaryResource.PublicName);
            }
        }

        private void AssertRelationshipIsToMany(string relationshipName)
        {
            var relationship = _request.Relationship;
            if (!(relationship is HasManyAttribute))
            {
                throw new RequestMethodNotAllowedException(relationship.PublicName);
            }
        }

        private async Task AssertRelationshipValuesExistAsync(params (RelationshipAttribute relationship, object relationshipValue)[] assignments)
        {
            var nonExistingResources = new Dictionary<string, IList<string>>();
            foreach (var (relationship, relationshipValue) in assignments)
            {
                IEnumerable<string> identifiers; 
                if (relationshipValue is IIdentifiable identifiable)
                {
                    identifiers = new [] { TypeHelper.GetIdValue(identifiable) };
                }
                else
                {
                    identifiers = ((IEnumerable<IIdentifiable>) relationshipValue).Select(TypeHelper.GetIdValue).ToArray();
                }  
                
                var resources = await _resourceAccessor.GetResourcesByIdAsync(relationship.RightType, identifiers);
                var missing = identifiers.Where(id => resources.All(r => TypeHelper.GetIdValue(r) != id)).ToArray();
                if (missing.Any())
                {
                    nonExistingResources.Add(_provider.GetResourceContext(relationship.RightType).PublicName, missing.ToArray());
                }
            }

            if (nonExistingResources.Any())
            {
                throw new ResourceNotFoundException(nonExistingResources);
            }
        }

        private List<TResource> AsList(TResource resource)
        {
            return new List<TResource> { resource };
        }
    }

    /// <summary>
    /// Represents the foundational Resource Service layer in the JsonApiDotNetCore architecture that uses a Resource Repository for data access.
    /// </summary>
    /// <typeparam name="TResource">The resource type.</typeparam>
    public class JsonApiResourceService<TResource> : JsonApiResourceService<TResource, int>,
        IResourceService<TResource>
        where TResource : class, IIdentifiable<int>
    {
        public JsonApiResourceService(
            IResourceRepository<TResource> repository,
            IQueryLayerComposer queryLayerComposer,
            IPaginationContext paginationContext,
            IJsonApiOptions options,
            ILoggerFactory loggerFactory,
            IJsonApiRequest request,
            IResourceChangeTracker<TResource> resourceChangeTracker,
            IResourceFactory resourceFactory,
            IResourceAccessor resourceAccessor,
            ITargetedFields targetedFields,
            IResourceContextProvider provider,
            IResourceHookExecutor hookExecutor = null)
            : base(repository, queryLayerComposer, paginationContext, options, loggerFactory, request,
                resourceChangeTracker, resourceFactory, resourceAccessor, targetedFields, provider, hookExecutor)
        { }
    }
}
