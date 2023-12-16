// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Amazon;
using Amazon.CloudFormation;
using Amazon.CloudFormation.Model;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.AWS.CloudFormation;

/// <summary>
/// Resource representing an AWS CloudFormation stack.
/// </summary>
public interface ICloudFormationResource : IResource
{
    /// <summary>
    /// The AWS credential profile to use for resolving credentials to make AWS service API calls.
    /// </summary>
    string? Profile { get; set; }

    /// <summary>
    /// The location where the profile is registered. Used when overriding the default location of credential profiles in the ~/.aws directory.
    /// </summary>
    string? ProfileLocation { get; set; }

    /// <summary>
    /// The AWS region to deploy the CloudFormation Stack.
    /// </summary>
    RegionEndpoint? Region { get; set; }

    /// <summary>
    /// The configured Amazon CloudFormation service client used to make service calls. If this property set
    /// then other properties for constructing the service like Profile and Region are ignored.
    /// </summary>
    IAmazonCloudFormation? CloudFormationClient { get; set; }

    /// <summary>
    /// Path to the CloudFormation template.
    /// </summary>
    string TemplatePath { get; }

    /// <summary>
    /// The output parameters of the CloudFormation stack.
    /// </summary>
    List<Output>? Outputs { get; }
}
