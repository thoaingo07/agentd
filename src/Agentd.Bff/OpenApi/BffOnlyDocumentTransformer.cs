using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Agentd.Bff.OpenApi;

/// <summary>Keeps only the browser contract (<c>/api</c>, <c>/bff</c>) in the document; <c>/mcp</c>, health and hubs stay out.</summary>
internal sealed class BffOnlyDocumentTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        foreach (var path in document.Paths.Keys.Where(p => !p.StartsWith("/api/", StringComparison.Ordinal) && !p.StartsWith("/bff/", StringComparison.Ordinal)).ToList())
        {
            document.Paths.Remove(path);
        }

        // Generated per run (the server URL); the committed contract must not depend on where it was generated.
        document.Servers?.Clear();
        return Task.CompletedTask;
    }
}
