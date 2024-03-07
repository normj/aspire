// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Publishing;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.AWS.CloudFormation;

/// <inheritdoc/>
internal sealed class CloudFormationTemplateResource(string name, string templatePath) : CloudFormationResource(name), ICloudFormationTemplateResource
{
    public IDictionary<string, string> CloudFormationParameters { get; } = new Dictionary<string, string>();

    /// <inheritdoc/>
    public string TemplatePath { get; } = templatePath;

    /// <inheritdoc/>
    public ICloudFormationTemplateResource AddParameter(string parameterName, string parameterValue)
    {
        CloudFormationParameters[parameterName] = parameterValue;
        return this;
    }

    internal void WriteToManifest(ManifestPublishingContext context)
    {
        context.Writer.WriteString("type", "aws.cloudformation.template.v0");
        context.Writer.TryWriteString("stack-name", context.GetManifestRelativePath(Name));
        context.Writer.TryWriteString("template-path", context.GetManifestRelativePath(TemplatePath));

        context.Writer.WritePropertyName("references");
        context.Writer.WriteStartArray();
        foreach (var cloudFormationResource in Annotations.OfType<CloudFormationReferenceAnnotation>())
        {
            context.Writer.WriteStartObject();
            context.Writer.WriteString("TargetResource", cloudFormationResource.TargetResource);
            context.Writer.WriteEndObject();
        }
        context.Writer.WriteEndArray();
    }
}
