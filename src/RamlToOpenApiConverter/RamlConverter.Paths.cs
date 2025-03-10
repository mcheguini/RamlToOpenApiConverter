using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Extensions;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Readers;
using RamlToOpenApiConverter.Extensions;

namespace RamlToOpenApiConverter
{
    public partial class RamlConverter
    {
        private OpenApiPaths MapPaths(IDictionary<object, object> o)
        {
            var paths = new OpenApiPaths();

            foreach (var key in o.Keys.OfType<string>().Where(k => k.StartsWith("/")))
            {
                var pathItems = MapPathItems(key, new List<OpenApiParameter>(), o.GetAsDictionary(key));
                foreach (var pathItem in pathItems)
                {
                    paths.Add(pathItem.AdjustedPath, pathItem.Item);
                }
            }

            return paths;
        }

        private ICollection<(OpenApiPathItem Item, string AdjustedPath)> MapPathItems(string parent, IList<OpenApiParameter> parentParameters, IDictionary<object, object> values)
        {
            var items = new List<(OpenApiPathItem Item, string AdjustedPath)>();

            // Fetch all parameters from this path
            var parameters = MapParameters(values);

            // And add parameters from parent
            foreach (var parameter in parentParameters)
            {
                parameters.Add(parameter);
            }

            var operations = new Dictionary<OperationType, OpenApiOperation>();

            // Loop all keys which do not start with a '/'
            foreach (string key in values.Keys.OfType<string>().Where(k => !k.StartsWith("/")))
            {
                // And try to match operations
                if (TryMapOperationType(key, out OperationType operationType))
                {
                    var operationValues = values.GetAsDictionary(key);
                    var operation = MapOperation(operationValues);

                    // Add parameters from the path to this operation
                    foreach (var parameter in parameters)
                    {
                        operation.Parameters.Add(parameter);
                    }

                    operations.Add(operationType, operation);
                }
            }

            // Operations found on this level from the PathItem, add these to a new PathItem
            if (operations.Any())
            {
                var singleItem = new OpenApiPathItem
                {
                    Operations = operations
                };

                items.Add((singleItem, parent));
            }

            // Now check 1 level deeper (loop all keys which do start with a '/')
            foreach (string key in values.Keys.OfType<string>().Where(k => k.StartsWith("/")))
            {
                var d = values.GetAsDictionary(key);
                string newPath = $"{parent}{key}";
                var mapItems = MapPathItems(newPath, parameters, d);
                items.AddRange(mapItems);
            }

            return items;
        }

        private OpenApiOperation MapOperation(IDictionary<object, object> values)
        {
            return new OpenApiOperation
            {
                Description = values.Get("description"),
                Parameters = MapParameters(values),
                Responses = MapResponses(values.GetAsDictionary("responses")),
                RequestBody = MapRequest(values.GetAsDictionary("body")),
            };
        }

        private OpenApiResponses MapResponses(IDictionary<object, object> values)
        {
            var openApiResponses = new OpenApiResponses();

            if (values == null)
            {
                return null;
            }

            // SharpYampl uses int but YamlDotNet uses string
            foreach (string key in values.Keys.OfType<string>())
            {
                var response = values.GetAsDictionary(key);
                if (response != null)
                {
                    var body = response.GetAsDictionary("body");
                    string description = response.Get("description");
                    if (body != null)
                    {
                        var openApiResponse = new OpenApiResponse
                        {
                            Description = description,
                            Content = MapContents(body)
                        };
                        openApiResponses.Add(key, openApiResponse);
                    }
                    else
                    {
                        openApiResponses.Add(key, new OpenApiResponse
                        {
                            Description = description
                        });
                    }
                }
            }

            return openApiResponses.Count > 0 ? openApiResponses : null;
        }

        private OpenApiRequestBody MapRequest(IDictionary<object, object> values)
        {
            if (values == null)
            {
                return null;
            }

            var requestBody = new OpenApiRequestBody
            {
                Content = MapContents(values)
            };

            return requestBody;
        }

        private IDictionary<string, OpenApiMediaType> MapContents(IDictionary<object, object> values)
        {
            if (values == null)
            {
                return null;
            }

            var content = new Dictionary<string, OpenApiMediaType>();

            foreach (string key in new[] { "application/json", "application/xml" })
            {
                if (values.ContainsKey(key))
                {
                    var items = values.GetAsDictionary(key); // Body and Example and Type and Schema
                    string exampleAsJson = items?.Get("example");

                    string type = items?.Get("type");
                    string schemaValue = items?.Get("schema");

                    OpenApiSchema schema = null;
                    if (!string.IsNullOrEmpty(type))
                    {
                        schema = MapMediaTypeSchema(type);
                    }
                    else if (!string.IsNullOrEmpty(schemaValue))
                    {
                        schema = MapMediaTypeSchema(schemaValue);
                    }

                    var openApiMediaType = new OpenApiMediaType
                    {
                        Schema = schema,
                        Example = !string.IsNullOrEmpty(exampleAsJson) ? MapExample(exampleAsJson) : null,
                    };

                    content.Add(key, openApiMediaType);
                }
            }

            return content;
        }

        private IOpenApiAny MapExample(string exampleAsJson)
        {
            var stringAsStream = new MemoryStream(Encoding.UTF8.GetBytes(exampleAsJson));

            var reader = new OpenApiStreamReader();
            return reader.ReadFragment<IOpenApiAny>(stringAsStream, OpenApiSpecVersion.OpenApi3_0, out var _);
        }

        private OpenApiSchema MapMediaTypeSchema(string value)
        {
            if (value.StartsWith("{"))
            {
                var objectType = _deserializer.Deserialize<IDictionary<object, object>>(value);
                var properties = objectType.GetAsDictionary("properties");
                var required = objectType.GetAsCollection("required");
                return MapSchema(properties, required);
            }

            var referenceSchemas = value
                .Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(o => CreateDummyOpenApiReferenceSchema(o.Trim(), "object"))
                .ToList();

            if (referenceSchemas.Count == 1)
            {
                return referenceSchemas.Single();
            }

            return new OpenApiSchema
            {
                AnyOf = referenceSchemas
            };
        }

        private OpenApiSchema CreateDummyOpenApiReferenceSchema(string referenceId, string type = null)
        {
            var schema = new OpenApiSchema
            {
                Reference = new OpenApiReference { Id = referenceId, Type = ReferenceType.Schema }
            };

            if (!string.IsNullOrEmpty(type))
            {
                schema.Type = type;
            }

            return schema;
        }

        private bool TryMapOperationType(string value, out OperationType operationType)
        {
            foreach (OperationType @enum in Enum.GetValues(typeof(OperationType)))
            {
                if (@enum.GetDisplayName().Equals(value, StringComparison.OrdinalIgnoreCase))
                {
                    operationType = @enum;
                    return true;
                }
            }

            operationType = OperationType.Get;
            return false;
        }
    }
}