﻿using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Runtime.Caching;
using SharpRepository.Repository.Helpers;
using SharpRepository.Repository.Queries;
using SharpRepository.Repository.Specifications;

// References that were helpful in developing the Write Through Caching and Generational Caching logic
//  http://www.regexprn.com/2011/06/web-application-caching-strategies.html
//  http://www.regexprn.com/2011/06/web-application-caching-strategies_05.html
//  http://37signals.com/svn/posts/3113-how-key-based-cache-expiration-works
//  http://assets.en.oreilly.com/1/event/27/Accelerate%20your%20Rails%20Site%20with%20Automatic%20Generation-based%20Action%20Caching%20Presentation%201.pdf

namespace SharpRepository.Repository.Caching
{
    public abstract class StandardCachingStrategyBase<T, TKey, TPartition> : ICachingStrategy<T, TKey> where T : class
    {
        private ICachingProvider _cachingProvider;

        public string CachePrefix { get; set; }
        public bool WriteThroughCachingEnabled { get; set; }
        public bool GenerationalCachingEnabled { get; set; }
        public Expression<Func<T, TPartition>> Partition { get; set; }

        private readonly string _typeFullName;

        internal StandardCachingStrategyBase()
            : this(new InMemoryCachingProvider())
        {
        }

        internal StandardCachingStrategyBase(ICachingProvider cachingProvider)
        {
            CachingProvider = cachingProvider ?? new InMemoryCachingProvider();
            CachePrefix = "#Repo";
            WriteThroughCachingEnabled = true;
            GenerationalCachingEnabled = true;
            Partition = null;

            _typeFullName = typeof(T).FullName ?? typeof(T).Name; // sometimes FullName returns null in certain derived type situations, so I added the check to use the Name property if FullName is null
        }

        public ICachingProvider CachingProvider
        {
            get { return _cachingProvider; }
            set { _cachingProvider = value ?? new InMemoryCachingProvider(); }
        }

        public bool TryGetResult<TResult>(TKey key, Expression<Func<T, TResult>> selector, out TResult result)
        {
            result = default(TResult);

            return WriteThroughCachingEnabled && IsInCache(GetWriteThroughCacheKey(key, selector), out result);
        }

        public void SaveGetResult<TResult>(TKey key, Expression<Func<T, TResult>> selector, TResult result)
        {
            if (WriteThroughCachingEnabled)
                SetCache(GetWriteThroughCacheKey(key, selector), result);
        }

        public bool TryGetAllResult<TResult>(IQueryOptions<T> queryOptions, Expression<Func<T, TResult>> selector, out IEnumerable<TResult> result)
        {
            result = null;

            return GenerationalCachingEnabled && IsInCache(GetAllCacheKey(queryOptions, selector), out result);
        }

        public void SaveGetAllResult<TResult>(IQueryOptions<T> queryOptions, Expression<Func<T, TResult>> selector, IEnumerable<TResult> result)
        {
            if (GenerationalCachingEnabled)
                SetCache(GetAllCacheKey(queryOptions, selector), result);
        }

        public bool TryFindAllResult<TResult>(ISpecification<T> criteria, IQueryOptions<T> queryOptions, Expression<Func<T, TResult>> selector, out IEnumerable<TResult> result)
        {
            result = null;

            return GenerationalCachingEnabled && IsInCache(FindAllCacheKey(criteria, queryOptions, selector), out result);
        }

        public void SaveFindAllResult<TResult>(ISpecification<T> criteria, IQueryOptions<T> queryOptions, Expression<Func<T, TResult>> selector, IEnumerable<TResult> result)
        {
            if (GenerationalCachingEnabled)
                SetCache(FindAllCacheKey(criteria, queryOptions, selector), result);
        }

        public bool TryFindResult<TResult>(ISpecification<T> criteria, IQueryOptions<T> queryOptions, Expression<Func<T, TResult>> selector, out TResult result)
        {
            result = default(TResult);

            return GenerationalCachingEnabled && IsInCache(FindCacheKey(criteria, queryOptions, selector), out result);
        }

        public void SaveFindResult<TResult>(ISpecification<T> criteria, IQueryOptions<T> queryOptions, Expression<Func<T, TResult>> selector, TResult result)
        {
            if (GenerationalCachingEnabled)
                SetCache(FindCacheKey(criteria, queryOptions, selector), result);
        }

        public void Add(TKey key, T result)
        {
            if (WriteThroughCachingEnabled)
                SetCache(GetWriteThroughCacheKey<T>(key, null), result);

            if (GenerationalCachingEnabled)
                CheckPartitionUpdate(result);

        }

        public void Update(TKey key, T result)
        {
            if (WriteThroughCachingEnabled)
                SetCache(GetWriteThroughCacheKey<T>(key, null), result);

            if (GenerationalCachingEnabled)
                CheckPartitionUpdate(result);
        }

        public void Delete(TKey key, T result)
        {
            if (WriteThroughCachingEnabled)
                ClearCache(GetWriteThroughCacheKey<T>(key, null));

            if (GenerationalCachingEnabled)
                CheckPartitionUpdate(result);
        }

        public void Save()
        {
            // data is being added/edited/deleted so we need to move to the next generation for this type
            IncrementGeneration();
        }

        // helpers

        /// <summary>
        /// Increments the partition generation if applicable based on the partitions value of the entity that was just added/updated/deleted
        /// </summary>
        /// <param name="result">The entity that was changed</param>
        private void CheckPartitionUpdate(T result)
        {
            // TODO: right noow this is called mutliple times in Batchmode even if 3 in a row are for the same partition
            //  this should batch up the calls to IncrementPartitionGeneration and only call once if there are 3 of the same partition values in the same batch
            TPartition partition;
            if (TryPartitionValue(result, out partition))
            {
                IncrementPartitionGeneration(partition);
            }
        }

        public bool TryPartitionValue(T entity, out TPartition value)
        {
            value = default(TPartition);

            if (Partition == null || entity == null)
                return false;

            var partitionExpression = Partition.Body as MemberExpression;

            if (partitionExpression == null)
                return false;

            var partitionName = partitionExpression.Member.Name;

            // use the partition name (which is a property) and reflection to get the value
            var type = typeof(T);
            var propInfo = type.GetProperty(partitionName, typeof(TPartition));

            if (propInfo == null)
                return false;

            value = (TPartition)propInfo.GetValue(entity, null);

            return true;
        }

        // TODO: make this private
        // shouldn't be public but wanted to test against it quickly right now
        public bool TryPartitionValue(ISpecification<T> criteria, out TPartition value)
        {
            value = default(TPartition);

            if (Partition == null || criteria == null || criteria.Predicate == null)
                return false;

            var partitionExpression = Partition.Body as MemberExpression;

            if (partitionExpression == null)
                return false;

            var matches = new List<TPartition>();
            var partitionName = partitionExpression.Member.Name;

            RecurseExpressionTree(partitionName, criteria.Predicate.Body, ref matches);

            if (matches.Count == 1)
            {
                value = matches[0];
                return true;
            }

            return false;
        }

        private static void RecurseExpressionTree(string memberName, Expression expression, ref List<TPartition> matches)
        {
            var binaryExpression = expression as BinaryExpression;
            var methodCallExpression = expression as MethodCallExpression;

            if (binaryExpression != null)
            {

                var left = binaryExpression.Left;
                var right = binaryExpression.Right;

                // only equal works, if > or != then it can include more than just this partition so no need to check
                if (binaryExpression.NodeType == ExpressionType.Equal)
                {
                    // check value is on the right
                    if (left is MemberExpression && ((MemberExpression)left).Member.Name == memberName)
                    {
                        if (right is ConstantExpression)
                        {
                            matches.Add((TPartition)((ConstantExpression)right).Value);
                            return;
                        }
                        if (right is MemberExpression)
                        {
                            // this is when the right side is a variable instead of a constant like:
                            //      var contactId = 1;
                            //      var predicate = c => c.ContactId == contactId;
                            // in this case we must compile the variable expression in order to evaluate it
                            var accessorExpression = Expression.Lambda<Func<TPartition>>(right);
                            matches.Add(accessorExpression.Compile()());
                            return;
                        }
                    }

                    // check value is on the left
                    if (right is MemberExpression && ((MemberExpression)right).Member.Name == memberName)
                    {
                        if (left is ConstantExpression)
                        {
                            matches.Add((TPartition)((ConstantExpression)left).Value);
                            return;
                        }
                        if (left is MemberExpression)
                        {
                            // this is when the right side is a variable instead of a constant like:
                            //      var contactId = 1;
                            //      var predicate = c => c.ContactId == contactId;
                            // in this case we must compile the variable expression in order to evaluate it
                            var accessorExpression = Expression.Lambda<Func<TPartition>>(left);
                            matches.Add(accessorExpression.Compile()());
                            return;
                        }
                    }
                }

                RecurseExpressionTree(memberName, left, ref matches);
                RecurseExpressionTree(memberName, right, ref matches);
            }
            else if (methodCallExpression != null)
            {
                if (methodCallExpression.NodeType != ExpressionType.Call || methodCallExpression.Method.Name != "Equals" || methodCallExpression.Arguments.Count != 1 || methodCallExpression.Arguments[0].Type != typeof(TPartition))
                    return;

                MemberExpression memberExpression;
                switch (methodCallExpression.Object.NodeType)
                {
                    //  c.ContactTypeId.Equals(1) ... or ... c.ContactTypeId.Equals(contactTypeId) ... or ... contactTypeId.Equals(c.ContactTypeId)
                    case ExpressionType.MemberAccess:
                        switch (methodCallExpression.Arguments[0].NodeType)
                        {
                            case ExpressionType.Constant:
                                matches.Add((TPartition)((ConstantExpression)methodCallExpression.Arguments[0]).Value);

                                break;
                            case ExpressionType.MemberAccess:
                                // the argument can either be a variable or the Entity.Property, so let's figure out which
                                memberExpression = methodCallExpression.Arguments[0] as MemberExpression;
                                if (memberExpression == null)
                                    return;

                                if (memberExpression.Member.Name == memberName)
                                {
                                    // this means the argument is the Entity.Property
                                    matches.Add(Expression.Lambda<Func<TPartition>>(methodCallExpression.Object).Compile()());
                                }
                                else if (((MemberExpression)methodCallExpression.Object).Member.Name == memberName)
                                {
                                    matches.Add(Expression.Lambda<Func<TPartition>>(methodCallExpression.Arguments[0]).Compile()());
                                }

                                break;
                        }

                        break;
                    //  1.Equals(c.ContactTypeId)
                    case ExpressionType.Constant:
                        memberExpression = methodCallExpression.Arguments[0] as MemberExpression;
                        if (memberExpression == null || memberExpression.Member.Name != memberName)
                            return;

                        var constantExpression = (ConstantExpression)methodCallExpression.Object;
                        if (constantExpression == null || constantExpression.Type != typeof(TPartition))
                            return;

                        matches.Add((TPartition)constantExpression.Value);

                        break;
                }
            }
        }

        protected bool IsInCache<TCacheItem>(string cacheKey, out TCacheItem result)
        {
            result = default(TCacheItem);

            try
            {
                if (CachingProvider.Get(cacheKey, out result))
                {
                    //Trace.WriteLine(String.Format("Got item from cache: {0} - {1}", cacheKey, typeof(TCacheItem).Name));
                    return true;
                }
            }
            catch (Exception)
            {
                // don't let caching errors cause problems for the Repository
            }

            return false;
        }

        protected void SetCache<TCacheItem>(string cacheKey, TCacheItem result)
        {
            try
            {
                CachingProvider.Set(cacheKey, result);
                //Trace.WriteLine(String.Format("Write item to cache: {0} - {1}", cacheKey, typeof(TCacheItem).Name));
            }
            catch (Exception)
            {
                // don't let caching errors mess with the repository
            }
        }

        protected void ClearCache(string cacheKey)
        {
            try
            {
                CachingProvider.Clear(cacheKey);
            }
            catch (Exception)
            {
                // don't let caching errors mess with the repository
            }
        }

        private string GetWriteThroughCacheKey<TResult>(TKey key, Expression<Func<T, TResult>> selector)
        {
            return String.Format("{0}/{1}/{2}::{3}", CachePrefix, _typeFullName, key, (selector != null ? selector.ToString() : "null"));
        }

        private string GetAllCacheKey<TResult>(IQueryOptions<T> queryOptions, Expression<Func<T, TResult>> selector)
        {
            return String.Format("{0}/{1}/{2}/{3}", CachePrefix, _typeFullName, GetGeneration(), Md5Helper.CalculateMd5("All::" + (queryOptions != null ? queryOptions.ToString() : "null") + "::" + (selector != null ? selector.ToString() : "null")));
        }

        private string FindAllCacheKey<TResult>(ISpecification<T> criteria, IQueryOptions<T> queryOptions, Expression<Func<T, TResult>> selector)
        {
            TPartition partition;
            if (TryPartitionValue(criteria, out partition))
            {
                return String.Format("{0}/{1}/p:{2}/{3}/{4}/{5}", CachePrefix, _typeFullName, partition, GetPartitionGeneration(partition), "FindAll", Md5Helper.CalculateMd5(criteria + "::" + (queryOptions != null ? queryOptions.ToString() : "null") + "::" + (selector != null ? selector.ToString() : "null")));
            }

            return String.Format("{0}/{1}/{2}/{3}/{4}", CachePrefix, _typeFullName, GetGeneration(), "FindAll", Md5Helper.CalculateMd5(criteria + "::" + (queryOptions != null ? queryOptions.ToString() : "null") + "::" + (selector != null ? selector.ToString() : "null")));
        }

        private string FindCacheKey<TResult>(ISpecification<T> criteria, IQueryOptions<T> queryOptions, Expression<Func<T, TResult>> selector)
        {
            TPartition partition;
            if (TryPartitionValue(criteria, out partition))
            {
                return String.Format("{0}/{1}/p:{2}/{3}/{4}/{5}", CachePrefix, _typeFullName, partition, GetPartitionGeneration(partition), "Find", Md5Helper.CalculateMd5(criteria + "::" + (queryOptions != null ? queryOptions.ToString() : "null") + "::" + (selector != null ? selector.ToString() : "null")));
            }

            return String.Format("{0}/{1}/{2}/{3}/{4}", CachePrefix, _typeFullName, GetGeneration(), "Find", Md5Helper.CalculateMd5(criteria + "::" + (queryOptions != null ? queryOptions.ToString() : "null") + "::" + (selector != null ? selector.ToString() : "null")));
        }

        private int GetGeneration()
        {
            if (!GenerationalCachingEnabled) return 1; // no need to use the caching provider

            int generation;
            return !CachingProvider.Get(GetGenerationKey(), out generation) ? 1 : generation;
        }

        private int IncrementGeneration()
        {
            var generation = !GenerationalCachingEnabled ? 1 : CachingProvider.Increment(GetGenerationKey(), 1, 1, CacheItemPriority.NotRemovable);
            
            return generation;
        }

        private string GetGenerationKey()
        {
            return String.Format("{0}/{1}/Generation", CachePrefix, _typeFullName);
        }

        private int GetPartitionGeneration(TPartition partition)
        {
            if (!GenerationalCachingEnabled) return 1; // no need to use the caching provider

            int generation;
            return !CachingProvider.Get(GetPartitionGenerationKey(partition), out generation) ? 1 : generation;
        }

        private int IncrementPartitionGeneration(TPartition partition)
        {
            return !GenerationalCachingEnabled ? 1 : CachingProvider.Increment(GetPartitionGenerationKey(partition), 1, 1, CacheItemPriority.NotRemovable);
        }

        protected string GetPartitionGenerationKey(TPartition partition)
        {
            return String.Format("{0}/{1}/p:{2}/Generation", CachePrefix, _typeFullName, partition);
        }
    }
}
