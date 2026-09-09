using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace MooreHotels.WebAPI.Filters;

/// <summary>
/// Runs every registered FluentValidation validator for controller DTOs.
/// Registering validators alone does not execute them in the MVC pipeline.
/// </summary>
public sealed class FluentValidationActionFilter : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(
        ActionExecutingContext context,
        ActionExecutionDelegate next)
    {
        var failures = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var argument in context.ActionArguments.Values)
        {
            if (argument is null)
            {
                continue;
            }

            var validatorType = typeof(IValidator<>).MakeGenericType(argument.GetType());
            if (context.HttpContext.RequestServices.GetService(validatorType) is not IValidator validator)
            {
                continue;
            }

            var validationContext = new ValidationContext<object>(argument);
            var result = await validator.ValidateAsync(
                validationContext,
                context.HttpContext.RequestAborted);

            foreach (var error in result.Errors.Where(error => error is not null))
            {
                if (!failures.TryGetValue(error.PropertyName, out var messages))
                {
                    messages = [];
                    failures[error.PropertyName] = messages;
                }

                if (!messages.Contains(error.ErrorMessage, StringComparer.Ordinal))
                {
                    messages.Add(error.ErrorMessage);
                }
            }
        }

        if (failures.Count == 0)
        {
            await next();
            return;
        }

        var errors = failures.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToArray(),
            StringComparer.Ordinal);
        context.Result = new BadRequestObjectResult(new ValidationProblemDetails(errors)
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "One or more validation errors occurred."
        });
    }
}
