using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace MooreHotels.WebAPI.Configuration;

public sealed class RemoveSwaggerDescriptionsFilter : IDocumentFilter
{
    public void Apply(
        OpenApiDocument document,
        DocumentFilterContext context)
    {
        document.Info.Description = null;

        if (document.Tags is not null)
        {
            foreach (var tag in document.Tags)
            {
                tag.Description = null;
            }
        }

        foreach (var path in document.Paths.Values)
        {
            foreach (var operation in path.Operations.Values)
            {
                operation.Summary = null;
                operation.Description = null;

                foreach (var parameter in operation.Parameters)
                {
                    parameter.Description = null;
                    RemoveSchemaDescriptions(parameter.Schema);
                }

                if (operation.RequestBody is not null)
                {
                    operation.RequestBody.Description = null;
                    foreach (var mediaType in operation.RequestBody.Content.Values)
                    {
                        RemoveSchemaDescriptions(mediaType.Schema);
                    }
                }

                foreach (var response in operation.Responses.Values)
                {
                    response.Description = string.Empty;
                    foreach (var header in response.Headers.Values)
                    {
                        header.Description = null;
                        RemoveSchemaDescriptions(header.Schema);
                    }

                    foreach (var mediaType in response.Content.Values)
                    {
                        RemoveSchemaDescriptions(mediaType.Schema);
                    }
                }
            }
        }

        foreach (var schema in document.Components.Schemas.Values)
        {
            RemoveSchemaDescriptions(schema);
        }

        foreach (var parameter in document.Components.Parameters.Values)
        {
            parameter.Description = null;
            RemoveSchemaDescriptions(parameter.Schema);
        }

        foreach (var requestBody in document.Components.RequestBodies.Values)
        {
            requestBody.Description = null;
        }

        foreach (var response in document.Components.Responses.Values)
        {
            response.Description = string.Empty;
        }

        foreach (var securityScheme in
                 document.Components.SecuritySchemes.Values)
        {
            securityScheme.Description = null;
        }
    }

    private static void RemoveSchemaDescriptions(
        OpenApiSchema? schema,
        ISet<OpenApiSchema>? visited = null)
    {
        if (schema is null) return;

        visited ??= new HashSet<OpenApiSchema>(
            ReferenceEqualityComparer.Instance);
        if (!visited.Add(schema)) return;

        schema.Description = null;
        RemoveSchemaDescriptions(schema.Items, visited);
        RemoveSchemaDescriptions(schema.AdditionalProperties, visited);
        foreach (var property in schema.Properties.Values)
        {
            RemoveSchemaDescriptions(property, visited);
        }

        foreach (var child in schema.AllOf
                     .Concat(schema.AnyOf)
                     .Concat(schema.OneOf))
        {
            RemoveSchemaDescriptions(child, visited);
        }
    }
}
