using System.Reflection;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Validators;
using PunchedApi.API.Controllers;
using PunchedApi.Application.Media;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Tests;

public sealed class MediaApiContractTests
{
    [Fact]
    public void ControllerExposesRequiredLifecycleRoutes()
    {
        var type = typeof(MediaController);
        Assert.NotNull(type.GetCustomAttribute<ApiControllerAttribute>());
        Assert.Equal("v1/media", type.GetCustomAttribute<Microsoft.AspNetCore.Mvc.RouteAttribute>()!.Template);
        var methods = type.GetMethods();
        Assert.Contains(methods, m => m.GetCustomAttributes().OfType<HttpPostAttribute>().Any(a => a.Template == "uploads"));
        Assert.Contains(methods, m => m.GetCustomAttributes().OfType<HttpPostAttribute>().Any(a => a.Template == "{id:guid}/complete"));
        Assert.Contains(methods, m => m.GetCustomAttributes().OfType<HttpPostAttribute>().Any(a => a.Template == "{id:guid}/upload-grant"));
        Assert.Contains(methods, m => m.GetCustomAttributes().OfType<HttpGetAttribute>().Any(a => a.Template == "{id:guid}"));
        Assert.Contains(methods, m => m.GetCustomAttributes().OfType<HttpPostAttribute>().Any(a => a.Template == "{id:guid}/retry"));
        Assert.Contains(methods, m => m.GetCustomAttributes().OfType<HttpDeleteAttribute>().Any(a => a.Template == "{id:guid}"));
        Assert.Contains(methods, m => m.GetCustomAttributes().OfType<HttpPutAttribute>().Any(a => a.Template == "business/logo"));
        Assert.Contains(methods, m => m.GetCustomAttributes().OfType<HttpDeleteAttribute>().Any(a => a.Template == "business/logo"));
        Assert.Contains(methods, m => m.GetCustomAttributes().OfType<HttpPutAttribute>().Any(a => a.Template == "business/cover"));
        Assert.Contains(methods, m => m.GetCustomAttributes().OfType<HttpPutAttribute>().Any(a => a.Template == "user/avatar"));
        Assert.Contains(methods, m => m.GetCustomAttributes().OfType<HttpPostAttribute>().Any(a => a.Template == "business/gallery"));
        Assert.Contains(methods, m => m.GetCustomAttributes().OfType<HttpPatchAttribute>().Any(a => a.Template == "business/gallery/order"));
        Assert.Contains(methods, m => m.GetCustomAttributes().OfType<HttpPutAttribute>().Any(a => a.Template == "service/{serviceId:guid}"));
        Assert.Contains(methods, m => m.GetCustomAttributes().OfType<HttpPutAttribute>().Any(a => a.Template == "loyalty-program/{programId:guid}"));
        Assert.Contains(methods, m => m.GetCustomAttributes().OfType<HttpPutAttribute>().Any(a => a.Template == "review/{reviewId:guid}"));
    }

    [Fact]
    public void RuntimePurposeLimitsAreDeterministic()
    {
        var limits = MediaLimitOptions.CreateDefaults();
        Assert.Equal(5_242_880, limits.Purposes[MediaPurposes.UserAvatar].MaxBytes);
        Assert.All(limits.Purposes.Values, limit => Assert.True(limit.MinWidth <= 1 && limit.MinHeight <= 1));
        Assert.Equal(10_000, limits.Purposes[MediaPurposes.ServiceImage].MaxWidth);
        Assert.Equal(6_291_456, limits.Purposes[MediaPurposes.ReviewImage].MaxBytes);
    }

    [Fact]
    public void UploadValidatorRejectsMissingAndInvalidFields()
    {
        var validator = new CreateMediaUploadRequestValidator();
        Assert.False(validator.Validate(new CreateMediaUploadRequest()).IsValid);
        Assert.False(validator.Validate(new CreateMediaUploadRequest { Purpose = MediaPurposes.BusinessLogo, FileName = "x.png", DeclaredMimeType = "image/png", SizeBytes = 0 }).IsValid);
        Assert.True(validator.Validate(new CreateMediaUploadRequest { Purpose = MediaPurposes.BusinessLogo, FileName = "x.png", DeclaredMimeType = "image/png", SizeBytes = 10 }).IsValid);
    }
}
