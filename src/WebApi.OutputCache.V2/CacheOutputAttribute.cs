﻿using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Formatting;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http;
using System.Web.Http.Controllers;
using System.Web.Http.Filters;
using WebApi.OutputCache.Core;
using WebApi.OutputCache.Core.Cache;
using WebApi.OutputCache.Core.Time;

namespace WebApi.OutputCache.V2
{
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
    public class CacheOutputAttribute : CacheOutputAttributeBase
    {
        // cache repository
        private IApiOutputCache _webApiCache;

        public override void EnsureCache(HttpConfiguration config, HttpRequestMessage req)
        {
            _webApiCache = config.CacheOutputConfiguration().GetCacheOutputProvider(req);
        }
        
        public override void OnActionExecuting(HttpActionContext actionContext)
        {
            if (actionContext == null) throw new ArgumentNullException("actionContext");

            if (!IsCachingAllowed(actionContext, AnonymousOnly)) return;

            var config = actionContext.Request.GetConfiguration();

            EnsureCacheTimeQuery();
            EnsureCache(config, actionContext.Request);

            var cacheKeyGenerator = config.CacheOutputConfiguration().GetCacheKeyGenerator(actionContext.Request, CacheKeyGenerator);

            var responseMediaType = GetExpectedMediaType(config, actionContext);
            actionContext.Request.Properties[CurrentRequestMediaType] = responseMediaType;
            var cachekey = cacheKeyGenerator.MakeCacheKey(actionContext, responseMediaType, ExcludeQueryStringFromCacheKey);

            if (!_webApiCache.Contains(cachekey)) return;

            if (actionContext.Request.Headers.IfNoneMatch != null)
            {
                var etag = _webApiCache.Get<string>(cachekey + Constants.EtagKey);
                if (etag != null)
                {
                    if (actionContext.Request.Headers.IfNoneMatch.Any(x => x.Tag ==  etag))
                    {
                        var time = CacheTimeQuery.Execute(DateTime.Now);
                        var quickResponse = actionContext.Request.CreateResponse(HttpStatusCode.NotModified);
                        ApplyCacheHeaders(quickResponse, time);
                        actionContext.Response = quickResponse;
                        return;
                    }
                }
            }

            var val = _webApiCache.Get<byte[]>(cachekey);
            if (val == null) return;

            var contenttype = _webApiCache.Get<MediaTypeHeaderValue>(cachekey + Constants.ContentTypeKey) ?? responseMediaType;

            actionContext.Response = actionContext.Request.CreateResponse();
            actionContext.Response.Content = new ByteArrayContent(val);

            actionContext.Response.Content.Headers.ContentType = contenttype;
            var responseEtag = _webApiCache.Get<string>(cachekey + Constants.EtagKey);
            if (responseEtag != null) SetEtag(actionContext.Response,  responseEtag);

            var cacheTime = CacheTimeQuery.Execute(DateTime.Now);
            ApplyCacheHeaders(actionContext.Response, cacheTime);
        }

        public override async Task OnActionExecutedAsync(HttpActionExecutedContext actionExecutedContext, CancellationToken cancellationToken)
        {
            if (actionExecutedContext.ActionContext.Response == null || !actionExecutedContext.ActionContext.Response.IsSuccessStatusCode) return;

            if (!IsCachingAllowed(actionExecutedContext.ActionContext, AnonymousOnly)) return;

            var cacheTime = CacheTimeQuery.Execute(DateTime.Now);
            if (cacheTime.AbsoluteExpiration > DateTime.Now)
            {
                var httpConfig = actionExecutedContext.Request.GetConfiguration();
                var config = httpConfig.CacheOutputConfiguration();
                var cacheKeyGenerator = config.GetCacheKeyGenerator(actionExecutedContext.Request, CacheKeyGenerator);

                var responseMediaType = actionExecutedContext.Request.Properties[CurrentRequestMediaType] as MediaTypeHeaderValue ?? GetExpectedMediaType(httpConfig, actionExecutedContext.ActionContext);
                var cachekey = cacheKeyGenerator.MakeCacheKey(actionExecutedContext.ActionContext, responseMediaType, ExcludeQueryStringFromCacheKey);

                if (!string.IsNullOrWhiteSpace(cachekey) && !(_webApiCache.Contains(cachekey)))
                {
                    SetEtag(actionExecutedContext.Response, CreateEtag(actionExecutedContext, cachekey, cacheTime));

                    var responseContent = actionExecutedContext.Response.Content;

                    if (responseContent != null)
                    {
                        var baseKey = config.MakeBaseCachekey(actionExecutedContext.ActionContext.ControllerContext.ControllerDescriptor.ControllerType.FullName, actionExecutedContext.ActionContext.ActionDescriptor.ActionName);
                        var contentType = responseContent.Headers.ContentType;
                        string etag = actionExecutedContext.Response.Headers.ETag.Tag;
                        //ConfigureAwait false to avoid deadlocks
                        var content = await responseContent.ReadAsByteArrayAsync().ConfigureAwait(false);

                        responseContent.Headers.Remove("Content-Length");

                        _webApiCache.Add(baseKey, string.Empty, cacheTime.AbsoluteExpiration);
                        _webApiCache.Add(cachekey, content, cacheTime.AbsoluteExpiration, baseKey);


                        _webApiCache.Add(cachekey + Constants.ContentTypeKey,
                            contentType,
                            cacheTime.AbsoluteExpiration, baseKey);


                        _webApiCache.Add(cachekey + Constants.EtagKey,
                            etag,
                            cacheTime.AbsoluteExpiration, baseKey);
                    }
                }
            }

            ApplyCacheHeaders(actionExecutedContext.ActionContext.Response, cacheTime);
        }
    }
} 
