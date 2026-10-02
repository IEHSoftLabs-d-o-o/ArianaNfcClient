using NfcTagReader.Models;

namespace NfcTagReader.Services.Validation;

public interface ITagPayloadValidator
{
    TagValidationResult Validate(string? rawPayload);
}
