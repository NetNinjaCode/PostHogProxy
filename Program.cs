using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

var builder = WebApplication.CreateBuilder(args);

// Use in-memory cache
builder.Services.AddMemoryCache();

// Create an HttpClient that ignores SSL issues
builder.Services.AddHttpClient("NoSSLValidation")
    .ConfigurePrimaryHttpMessageHandler(() =>
    {
        return new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
        };
    });

// CORS: let requests come from Your Domain
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowDomain", policy =>
    {
        policy
            .SetIsOriginAllowed(origin =>
            {
                try
                {
                    var requestUri = new Uri(origin);
                    return requestUri.Host.EndsWith(".domain.local", StringComparison.OrdinalIgnoreCase)
                           || requestUri.Host == "domain.local";
                }
                catch
                {
                    return false;
                }
            })
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials();
    });
});

var app = builder.Build();

const string API_HOST = "https://us.i.posthog.com";
const string ASSET_HOST = "https://us-assets.i.posthog.com";

app.UseCors("AllowDomain");

// Single endpoint for all paths
app.Map("/{**path}", async (HttpContext context, IMemoryCache cache, IHttpClientFactory clientFactory) =>
{
    // The part after the slash, e.g. /static/something
    var path = context.Request.Path.ToString().TrimStart('/');
    var query = context.Request.QueryString.ToString();
    var fullPath = $"/{path}{query}";

    var client = clientFactory.CreateClient("NoSSLValidation");

    // If it's a static path, try to proxy from the ASSET_HOST
    if (path.StartsWith("static/"))
    {
        if (cache.TryGetValue(fullPath, out byte[] cachedResponse))
        {
            // We cached this file earlier
            // We don't know the original content type here unless we also cached it,
            // but for brevity, let's just serve as application/octet-stream
            // or you could store that in the cache as well.
            context.Response.ContentType = "application/octet-stream";
            await context.Response.Body.WriteAsync(cachedResponse, 0, cachedResponse.Length);
            return;
        }

        var assetUrl = $"{ASSET_HOST}{fullPath}";
        var assetResponse = await client.GetAsync(assetUrl);

        if (assetResponse.IsSuccessStatusCode)
        {
            var data = await assetResponse.Content.ReadAsByteArrayAsync();

            // Cache the bytes
            cache.Set(fullPath, data, TimeSpan.FromMinutes(60));

            // If the upstream sets a valid content type, use it, otherwise guess
            if (assetResponse.Content.Headers.ContentType != null)
            {
                // Use what upstream gave us
                context.Response.ContentType = assetResponse.Content.Headers.ContentType.ToString();
            }
            else
            {
                // If the path ends with .js, set application/javascript so the browser executes it
                if (fullPath.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.ContentType = "application/javascript";
                }
                else
                {
                    // Fallback
                    context.Response.ContentType = "application/octet-stream";
                }
            }

            // Remove chunked encoding from the response to avoid issues
            context.Response.Headers.Remove("Transfer-Encoding");

            // Write the data out
            await context.Response.Body.WriteAsync(data, 0, data.Length);
            return;
        }

        // If the upstream gave an error, pass that on
        context.Response.StatusCode = (int)assetResponse.StatusCode;
        return;
    }

    // Otherwise, we treat this as an API request to posthog: us.i.posthog.com
    var apiUrl = $"{API_HOST}{fullPath}";
    var apiRequest = new HttpRequestMessage(new HttpMethod(context.Request.Method), apiUrl);

    // We might read the request body here
    if (context.Request.ContentLength > 0 || context.Request.HasFormContentType)
    {
        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
        var requestBody = await reader.ReadToEndAsync();

        // Reset the position if possible
        if (context.Request.Body.CanSeek)
        {
            context.Request.Body.Position = 0;
        }

        apiRequest.Content = new StringContent(requestBody, Encoding.UTF8, context.Request.ContentType);
    }
    else if (!string.IsNullOrEmpty(context.Request.ContentType) &&
             context.Request.ContentType.Contains("json", StringComparison.OrdinalIgnoreCase))
    {
        // If it's JSON but empty, still set an empty JSON body
        apiRequest.Content = new StringContent("", Encoding.UTF8, "application/json");
    }

    // --- Remove or handle content encoding properly ---
    // If the original request had 'Content-Encoding' or 'Transfer-Encoding',
    // we often do not want to forward them after reading the stream ourselves.
    if (context.Request.Headers.ContainsKey("Content-Encoding"))
    {
        context.Request.Headers.Remove("Content-Encoding");
    }
    if (context.Request.Headers.ContainsKey("Transfer-Encoding"))
    {
        context.Request.Headers.Remove("Transfer-Encoding");
    }

    // Forward most headers, but skip content-length, content-type, and encodings we handled
    foreach (var header in context.Request.Headers)
    {
        if (header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) ||
            header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
            header.Key.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase) ||
            header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        // If the apiRequest doesn't already have it, add it
        if (!apiRequest.Headers.Contains(header.Key))
        {
            apiRequest.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }
    }

    // Remove a few sensitive headers
    apiRequest.Headers.Remove("Cookie");
    apiRequest.Headers.Remove("Authorization");
    apiRequest.Headers.Remove("Host");

    // Force the host to the PostHog host
    apiRequest.Headers.TryAddWithoutValidation("Host", "us.i.posthog.com");

    // Forward original client IP using X-Forwarded-For
    var clientIp = context.Connection.RemoteIpAddress?.ToString();
    if (!string.IsNullOrWhiteSpace(clientIp))
    {
        if (apiRequest.Headers.Contains("X-Forwarded-For"))
        {
            var existing = apiRequest.Headers.GetValues("X-Forwarded-For");
            apiRequest.Headers.Remove("X-Forwarded-For");
            apiRequest.Headers.TryAddWithoutValidation("X-Forwarded-For", string.Join(", ", existing) + ", " + clientIp);
        }
        else
        {
            apiRequest.Headers.TryAddWithoutValidation("X-Forwarded-For", clientIp);
        }
    }

    // Send request to PostHog
    var apiResponse = await client.SendAsync(apiRequest);

    // Pass status code back
    context.Response.StatusCode = (int)apiResponse.StatusCode;

    // Copy response headers, except transfer/content-encoding which can cause chunked errors
    foreach (var header in apiResponse.Headers)
    {
        if (header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
            header.Key.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }
        context.Response.Headers[header.Key] = header.Value.ToArray();
    }

    // Some servers put content headers separately
    foreach (var header in apiResponse.Content.Headers)
    {
        if (header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
            header.Key.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }
        context.Response.Headers[header.Key] = header.Value.ToArray();
    }

    // Finally, copy the response body. This should also handle chunked content properly.
    await apiResponse.Content.CopyToAsync(context.Response.Body);
});

app.Run();
