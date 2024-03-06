// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Amazon.CloudFormation;
using Amazon.CloudFormation.Model;
using Amazon.Runtime;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.AWS.CloudFormation;
internal sealed class CloudFormationProvisioner(
    DistributedApplicationModel appModel,
    ResourceNotificationService notificationService,
    ResourceLoggerService loggerService)
{
    // Name of the Tag for the stack to store the SHA256 of the CloudFormation template
    const string SHA256_TAG = "AspireAppHost_SHA256";

    // CloudFormation statuses for when the stack is in transition all end with IN_PROGRESS
    const string IN_PROGRESS_SUFFIX = "IN_PROGRESS";

    // Polling interval for checking status of CloudFormation stack when creating or updating the stack.
    public TimeSpan StackPollingDelay { get; set; } = TimeSpan.FromSeconds(3);

    internal async Task ConfigureCloudFormation(CancellationToken cancellationToken = default)
    {
        await ProcessCloudFormationStackResourceAsync(cancellationToken).ConfigureAwait(false);
        await ProcessCloudFormationTemplateResourceAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ProcessCloudFormationStackResourceAsync(CancellationToken cancellationToken = default)
    {
        foreach (var cloudFormationResource in appModel.Resources.OfType<CloudFormationStackResource>())
        {
            var resourceLogger = loggerService.GetLogger(cloudFormationResource);

            await PublishCloudFormationUpdateStateAsync(cloudFormationResource, Constants.ResourceStateStarting).ConfigureAwait(false);

            try
            {
                using var cfClient = GetCloudFormationClient(cloudFormationResource);

                var request = new DescribeStacksRequest { StackName = cloudFormationResource.Name };
                var response = await cfClient.DescribeStacksAsync(request, cancellationToken).ConfigureAwait(false);

                // If the stack didn't exist then a StackNotFoundException would have been thrown.
                var stack = response.Stacks[0];

                // Capture the CloudFormation stack output parameters on to the Aspire CloudFormation resource. This
                // allows projects that have a reference to the stack have the output parameters applied to the
                // projects IConfiguration.
                cloudFormationResource.Outputs = stack!.Outputs;

                await PublishCloudFormationUpdateStateAsync(cloudFormationResource, Constants.ResourceStateRunning).ConfigureAwait(false);

                cloudFormationResource.ProvisioningTaskCompletionSource?.TrySetResult();
            }
            catch (Exception e)
            {
                if (e is AmazonCloudFormationException ce && string.Equals(ce.ErrorCode, "ValidationError"))
                {
                    resourceLogger.LogError("Stack {StackName} does not exists to add as a resource.", cloudFormationResource.Name);
                }
                else
                {
                    resourceLogger.LogError(e, "Error reading {StackName}.", cloudFormationResource.Name);
                }

                await PublishCloudFormationUpdateStateAsync(cloudFormationResource, Constants.ResourceStateFailedToStart).ConfigureAwait(false);
                cloudFormationResource.ProvisioningTaskCompletionSource?.TrySetException(e);
            }
        }
    }

    private async Task ProcessCloudFormationTemplateResourceAsync(CancellationToken cancellationToken = default)
    {
        foreach (var cloudFormationResource in appModel.Resources.OfType<CloudFormationTemplateResource>())
        {
            var resourceLogger = loggerService.GetLogger(cloudFormationResource);

            try
            {
                await PublishCloudFormationUpdateStateAsync(cloudFormationResource, Constants.ResourceStateStarting).ConfigureAwait(false);

                using var cfClient = GetCloudFormationClient(cloudFormationResource);

                var templateBody = File.ReadAllText(cloudFormationResource.TemplatePath);
                var templateSha256 = ComputeSHA256(templateBody);

                var templateParameters = new List<Parameter>();
                foreach (var kvp in cloudFormationResource.CloudFormationParameters)
                {
                    templateParameters.Add(new Parameter
                    {
                        ParameterKey = kvp.Key,
                        ParameterValue = kvp.Value
                    });
                }

                var stackChanged = false;
                var stack = await FindExistingStackAsync(cfClient, cloudFormationResource.Name).ConfigureAwait(false);
                if (stack == null || stack.StackStatus == StackStatus.DELETE_COMPLETE)
                {
                    var createStackRequest = new CreateStackRequest
                    {
                        StackName = cloudFormationResource.Name,
                        TemplateBody = templateBody,
                        Parameters = templateParameters,
                        Tags = { new Tag { Key = SHA256_TAG, Value = templateSha256 } }
                    };

                    resourceLogger.LogInformation("Create CloudFormation stack {StackName}", cloudFormationResource.Name);
                    try
                    {
                        stackChanged = true;
                        await cfClient.CreateStackAsync(createStackRequest, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        resourceLogger.LogError(ex, "Error creating CloudFormation stack {StackName}", cloudFormationResource.Name);

                        await PublishCloudFormationUpdateStateAsync(cloudFormationResource, Constants.ResourceStateFailedToStart).ConfigureAwait(false);

                        return;
                    }
                    stack = await WaitStackToCompleteAsync(resourceLogger, cfClient, cloudFormationResource.Name, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    if (stack.StackStatus.Value.EndsWith("IN_PROGRESS", StringComparison.CurrentCultureIgnoreCase))
                    {
                        resourceLogger.LogError("Stack {StackName} status's is currently in progress and can not be updated. ({StackStatus})", stack.StackName, stack.StackStatus);
                        throw new AWSProvisioningException($"Stack {stack.StackName} status's is currently in progress and can not be updated. ({stack.StackStatus})", null);
                    }

                    var tags = stack.Tags;

                    var shaTag = tags.FirstOrDefault(x => string.Equals(x.Key, SHA256_TAG, StringComparison.Ordinal));
                    if (shaTag != null && string.Equals(templateSha256, shaTag.Value, StringComparison.Ordinal) && !IsStackInErrorState(stack))
                    {
                        resourceLogger.LogInformation("CloudFormation Template for CloudFormation stack {StackName} has not changed", cloudFormationResource.Name);
                    }
                    else
                    {
                        // Update the CloudFormation tag with the latest SHA256.
                        if (shaTag != null)
                        {
                            shaTag.Value = templateSha256;
                        }
                        else
                        {
                            tags.Add(new Tag { Key = SHA256_TAG, Value = templateSha256 });
                        }

                        var updateStackRequest = new UpdateStackRequest
                        {
                            StackName = cloudFormationResource.Name,
                            TemplateBody = templateBody,
                            Parameters = templateParameters,
                            Tags = tags
                        };

                        resourceLogger.LogInformation("Updating CloudFormation stack {StackName}", cloudFormationResource.Name);
                        try
                        {
                            stackChanged = true;
                            await cfClient.UpdateStackAsync(updateStackRequest, cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            resourceLogger.LogError(ex, "Error updating CloudFormation stack {StackName}", cloudFormationResource.Name);
                            await PublishCloudFormationUpdateStateAsync(cloudFormationResource, Constants.ResourceStateFailedToStart).ConfigureAwait(false);
                            return;
                        }

                        stack = await WaitStackToCompleteAsync(resourceLogger, cfClient, cloudFormationResource.Name, cancellationToken).ConfigureAwait(false);
                    }
                }

                resourceLogger.LogDebug("CloudFormation stack has {Count} output parameters", stack.Outputs.Count);
                if (resourceLogger.IsEnabled(LogLevel.Debug))
                {
                    foreach (var output in stack.Outputs)
                    {
                        resourceLogger.LogDebug("Output Name: {Name}, Value {Value}", output.OutputKey, output.OutputValue);
                    }
                }

                var stateEnum = Constants.ResourceStateRunning;
                if (stackChanged && IsStackInErrorState(stack))
                {
                    stateEnum = Constants.ResourceStateFailedToStart;
                }

                await PublishCloudFormationUpdateStateAsync(cloudFormationResource, stateEnum, ConvertOutputToProperties(stack, cloudFormationResource.TemplatePath)).ConfigureAwait(false);

                // Capture the CloudFormation stack output parameters on to the Aspire CloudFormation resource. This
                // allows projects that have a reference to the stack have the output parameters applied to the
                // projects IConfiguration.
                cloudFormationResource.Outputs = stack.Outputs;

                if (stateEnum == Constants.ResourceStateFailedToStart)
                {
                    cloudFormationResource.ProvisioningTaskCompletionSource?.TrySetException(new AWSProvisioningException("Failed to apply CloudFormation template", null));
                }
                else
                {
                    cloudFormationResource.ProvisioningTaskCompletionSource?.TrySetResult();
                }
            }
            catch (Exception ex)
            {
                resourceLogger.LogError(ex, "Error provisioning {ResourceName} CloudFormation resource", cloudFormationResource.Name);
                await PublishCloudFormationUpdateStateAsync(cloudFormationResource, Constants.ResourceStateFailedToStart).ConfigureAwait(false);
                cloudFormationResource.ProvisioningTaskCompletionSource?.TrySetException(ex);
            }
        }
    }

    private static bool IsStackInErrorState(Stack stack)
    {
        if (stack.StackStatus.ToString(CultureInfo.InvariantCulture).EndsWith("FAILED", StringComparison.OrdinalIgnoreCase) || stack.StackStatus.ToString(CultureInfo.InvariantCulture).EndsWith("ROLLBACK_COMPLETE", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private async Task PublishCloudFormationUpdateStateAsync(CloudFormationResource resource, string status, ImmutableArray<(string, string)>? properties = null)
    {
        if (properties == null)
        {
            properties = ImmutableArray.Create<(string, string)>();
        }

        await notificationService.PublishUpdateAsync(resource, state => state with { State = status, Properties = state.Properties.AddRange(properties) }).ConfigureAwait(false);
        foreach (var reference in resource.References)
        {
            await notificationService.PublishUpdateAsync(reference, state => state with { State = status, Properties = state.Properties.AddRange(properties) }).ConfigureAwait(false);

            if (status == Constants.ResourceStateRunning)
            {
                loggerService.GetLogger(reference).LogInformation("Resource {Project} is referencing the output variables of CloudFormation Stack {Stack}", reference.TargetResourceName, reference.StackName);
            }
        }
    }

    private static ImmutableArray<(string, string)> ConvertOutputToProperties(Stack stack, string? templateFile = null)
    {
        var list = new List<(string, string)>();

        foreach (var output in stack.Outputs)
        {
            list.Add(("aws.cloudformation.output." + output.OutputKey, output.OutputValue));
        }

        list.Add((CustomResourceKnownProperties.Source, stack.StackId));

        if (!string.IsNullOrEmpty(templateFile))
        {
            list.Add(("aws.cloudformation.template", templateFile));
        }

        return ImmutableArray.Create(list.ToArray());
    }

    private static IAmazonCloudFormation GetCloudFormationClient(ICloudFormationResource resource)
    {
        if (resource.CloudFormationClient != null)
        {
            return resource.CloudFormationClient;
        }

        try
        {
            if (resource.AWSSDKConfig != null)
            {
                var config = resource.AWSSDKConfig.CreateServiceConfig<AmazonCloudFormationConfig>();

                var awsCredentials = FallbackCredentialsFactory.GetCredentials(config);
                return new AmazonCloudFormationClient(awsCredentials, config);
            }

            return new AmazonCloudFormationClient();
        }
        catch (Exception e)
        {
            throw new AWSProvisioningException("Failed to construct AWS CloudFormation service client to provision AWS resources.", e);
        }
    }

    /// <summary>
    /// Wait for the CloudFormation stack to get to a stable state after creating or updating the stack.
    /// </summary>
    /// <param name="resourceLogger"></param>
    /// <param name="cfClient"></param>
    /// <param name="stackName"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private async Task<Stack> WaitStackToCompleteAsync(ILogger resourceLogger, IAmazonCloudFormation cfClient, string stackName, CancellationToken cancellationToken)
    {
        const int TIMESTAMP_WIDTH = 20;
        const int LOGICAL_RESOURCE_WIDTH = 40;
        const int RESOURCE_STATUS = 40;
        var mostRecentEventId = string.Empty;

        var minTimeStampForEvents = DateTimeOffset.Now;
        resourceLogger.LogInformation("Waiting for CloudFormation stack {StackName} to be ready", stackName);

        Stack stack;
        do
        {
            await Task.Delay(StackPollingDelay, cancellationToken).ConfigureAwait(false);

            // If we are in the WaitStackToCompleteAsync then we already know the stack exists.
            stack = (await FindExistingStackAsync(cfClient, stackName).ConfigureAwait(false))!;

            var events = await GetLatestEventsAsync(cfClient, stackName, minTimeStampForEvents, mostRecentEventId, cancellationToken).ConfigureAwait(false);
            if (events.Count > 0)
            {
                mostRecentEventId = events[0].EventId;
            }

            for (var i = events.Count - 1; i >= 0; i--)
            {
                var line =
                    events[i].Timestamp.ToString("g", CultureInfo.InvariantCulture).PadRight(TIMESTAMP_WIDTH) + " " +
                    events[i].LogicalResourceId.PadRight(LOGICAL_RESOURCE_WIDTH) + " " +
                    events[i].ResourceStatus.ToString(CultureInfo.InvariantCulture).PadRight(RESOURCE_STATUS);

                if (!events[i].ResourceStatus.ToString(CultureInfo.InvariantCulture).EndsWith(IN_PROGRESS_SUFFIX) && !string.IsNullOrEmpty(events[i].ResourceStatusReason))
                {
                    line += " " + events[i].ResourceStatusReason;
                }

                if (minTimeStampForEvents < events[i].Timestamp)
                {
                    minTimeStampForEvents = events[i].Timestamp;
                }

                resourceLogger.LogInformation(line);
            }

        } while (!cancellationToken.IsCancellationRequested && stack.StackStatus.ToString(CultureInfo.InvariantCulture).EndsWith(IN_PROGRESS_SUFFIX));

        return stack;
    }

    private static async Task<List<StackEvent>> GetLatestEventsAsync(IAmazonCloudFormation cfClient, string stackName, DateTimeOffset minTimeStampForEvents, string mostRecentEventId, CancellationToken cancellationToken)
    {
        var noNewEvents = false;
        var events = new List<StackEvent>();
        DescribeStackEventsResponse? response = null;
        do
        {
            var request = new DescribeStackEventsRequest() { StackName = stackName };
            if (response != null)
            {
                request.NextToken = response.NextToken;
            }

            try
            {
                response = await cfClient.DescribeStackEventsAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                throw new AWSProvisioningException($"Error getting events for CloudFormation stack: {e.Message}", e);
            }
            foreach (var evnt in response.StackEvents)
            {
                if (string.Equals(evnt.EventId, mostRecentEventId) || evnt.Timestamp < minTimeStampForEvents)
                {
                    noNewEvents = true;
                    break;
                }

                events.Add(evnt);
            }

        } while (!noNewEvents && !string.IsNullOrEmpty(response.NextToken));

        return events;
    }

    private static string ComputeSHA256(string templateBody)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(templateBody));
        return Convert.ToHexString(bytes).ToLower(CultureInfo.InvariantCulture);
    }

    private static async Task<Stack?> FindExistingStackAsync(IAmazonCloudFormation cfClient, string stackName)
    {
        await foreach (var stack in cfClient.Paginators.DescribeStacks(new DescribeStacksRequest()).Stacks)
        {
            if (string.Equals(stackName, stack.StackName, StringComparison.Ordinal))
            {
                return stack;
            }
        }

        return null;
    }
}
