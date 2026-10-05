using ArianaNfcClient.Models;

namespace ArianaNfcClient.Services.Validation;

public interface ITagPayloadValidator
{
    TagValidationResult Validate(string? rawPayload);
}
