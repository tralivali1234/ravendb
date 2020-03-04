﻿using System;
using System.Net;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Formatting;
using Raven.Client.Exceptions;
using Raven.Server.Config;
using Raven.Server.Routing;
using Raven.Server.ServerWide;
using Raven.Server.Utils;
using Raven.Server.Web.System;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using Voron.Util.Settings;
using Raven.Server.Documents.Indexes;

namespace Raven.Server.Web.Studio
{
    public class StudioTasksHandler : RequestHandler
    {
        [RavenAction("/studio-tasks/is-valid-name", "GET", AuthorizationStatus.ValidUser)]
        public Task IsValidName()
        {
            if (Enum.TryParse(GetQueryStringValueAndAssertIfSingleAndNotEmpty("type").Trim(), out ItemType elementType) == false)
            {
                throw new ArgumentException($"Type {elementType} is not supported");
            }
            
            var name = GetQueryStringValueAndAssertIfSingleAndNotEmpty("name").Trim();
            var path = GetStringQueryString("dataPath", false);
            
            using (ServerStore.ContextPool.AllocateOperationContext(out JsonOperationContext context))
            {
                bool isValid = true;
                string errorMessage = null;
                
                switch (elementType)
                {
                    case ItemType.Database:
                        isValid = ResourceNameValidator.IsValidResourceName(name, path, out errorMessage);
                        break;
                    case ItemType.Index:
                        isValid = IndexStore.IsValidIndexName(name, isStatic: true, out errorMessage);
                        break;
                }
                
                using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
                {
                    context.Write(writer, new DynamicJsonValue
                    {
                        [nameof(NameValidation.IsValid)] = isValid,
                        [nameof(NameValidation.ErrorMessage)] = errorMessage
                    });
                    
                    writer.Flush();
                }
            }
            
            return Task.CompletedTask;
        }
        
        // return the calculated full data directory for the database before it is created according to the name & path supplied
        [RavenAction("/studio-tasks/full-data-directory", "GET", AuthorizationStatus.ValidUser)]
        public Task FullDataDirectory()
        {
            var path = GetStringQueryString("path", required: false);
            var name = GetStringQueryString("name", required: false);
            
            var baseDataDirectory = ServerStore.Configuration.Core.DataDirectory.FullPath;
            
            // 1. Used as default when both Name & Path are Not defined
            var result = baseDataDirectory; 
          
            // 2. Path defined, Path overrides any given Name
            if (string.IsNullOrEmpty(path) == false)
            {
                result = PathUtil.ToFullPath(path, baseDataDirectory);
            }

            // 3. Name defined, No path 
            else if (string.IsNullOrEmpty(name) == false)
            {
                // 'Databases' prefix is added...
                result = RavenConfiguration.GetDataDirectoryPath(ServerStore.Configuration.Core, name, ResourceType.Database);
            }
            
            using (ServerStore.ContextPool.AllocateOperationContext(out JsonOperationContext context))
            using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
            {
                writer.WriteStartObject();
                writer.WritePropertyName("FullPath");
                writer.WriteString(result);
                writer.WriteEndObject();
            }

            return Task.CompletedTask;
        }
        
        [RavenAction("/studio-tasks/format", "POST", AuthorizationStatus.ValidUser)]
        public Task Format()
        {
            using (ServerStore.ContextPool.AllocateOperationContext(out JsonOperationContext context))
            {
                var json = context.ReadForMemory(RequestBodyStream(), "studio-tasks/format");
                if (json == null)
                    throw new BadRequestException("No JSON was posted.");

                if (json.TryGet("Expression", out string expressionAsString) == false)
                    throw new BadRequestException("'Expression' property was not found.");

                if (string.IsNullOrWhiteSpace(expressionAsString))
                    return NoContent();

                using (var workspace = new AdhocWorkspace())
                {
                    var expression = SyntaxFactory
                        .ParseExpression(expressionAsString)
                        .NormalizeWhitespace();

                    var result = Formatter.Format(expression, workspace);

                    if (result.ToString().IndexOf("Could not format:", StringComparison.Ordinal) > -1)
                        throw new BadRequestException();

                    var formatedExpression = new FormatedExpression
                    {
                        Expression = result.ToString()
                    };

                    using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
                    {
                        context.Write(writer, formatedExpression.ToJson());
                    }
                }
            }

            return Task.CompletedTask;
        }

        public class FormatedExpression : IDynamicJson
        {
            public string Expression { get; set; }

            public DynamicJsonValue ToJson()
            {
                return new DynamicJsonValue
                {
                    [nameof(Expression)] = Expression
                };
            }
        }
        
        public enum ItemType
        {
            Index,
            Database
        }
    }
}
