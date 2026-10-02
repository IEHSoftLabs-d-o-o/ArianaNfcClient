using CommunityToolkit.Mvvm.ComponentModel;
using NfcTagReader.Models;
using NfcTagReader.Services.ArianaLab;
using NfcTagReader.Services.Validation;

namespace NfcTagReader.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ITagPayloadValidator _validator;
    private readonly IArianaLabClient _arianaLabClient;
    private string? _processedUid;
    private bool _waitingForRemoval;

    public MainViewModel(ITagPayloadValidator validator, IArianaLabClient arianaLabClient)
    {
        _validator = validator;
        _arianaLabClient = arianaLabClient;
        StatusKind = AppStatusKind.Waiting;
        Status = "Warten auf NFC-Tag…";
        ResultMessage = "Legen Sie ein NFC-Tag auf den Leser.";
    }

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private AppStatusKind _statusKind;

    [ObservableProperty]
    private string _resultMessage = string.Empty;

    [ObservableProperty]
    private string _technicalDetails = string.Empty;

    [ObservableProperty]
    private string _payloadPreview = string.Empty;

    [ObservableProperty]
    private string _readerName = "Kein Leser";

    [ObservableProperty]
    private string _lastUid = "–";

    public async Task HandleEventAsync(NfcReaderEvent evt, CancellationToken cancellationToken)
    {
        switch (evt.Kind)
        {
            case NfcReaderEventKind.ReaderAvailable:
                ReaderName = evt.ReaderName ?? ReaderName;
                if (!_waitingForRemoval)
                {
                    SetStatus(AppStatusKind.Waiting, "Warten auf NFC-Tag…", "Legen Sie ein NFC-Tag auf den Leser.");
                }

                break;

            case NfcReaderEventKind.ReaderLost:
                ReaderName = "Kein Leser";
                SetStatus(
                    AppStatusKind.NoReader,
                    "Kein NFC-Leser",
                    evt.ErrorMessage ?? "Der NFC-Leser ist nicht verbunden.",
                    evt.TechnicalDetails);
                break;

            case NfcReaderEventKind.TagPresent:
                LastUid = FormatUid(evt.Uid);
                if (_waitingForRemoval)
                {
                    return;
                }

                SetStatus(AppStatusKind.Reading, "Tag wird gelesen…", "NDEF-Daten werden vom NFC-Tag gelesen.");
                break;

            case NfcReaderEventKind.TagRemoved:
                _waitingForRemoval = false;
                _processedUid = null;
                LastUid = "–";
                PayloadPreview = string.Empty;
                SetStatus(AppStatusKind.Waiting, "Warten auf NFC-Tag…", "Legen Sie ein NFC-Tag auf den Leser.");
                TechnicalDetails = string.Empty;
                break;

            case NfcReaderEventKind.TagReadFailed:
                LastUid = FormatUid(evt.Uid);
                if (ShouldIgnore(evt.Uid))
                {
                    return;
                }

                _processedUid = evt.Uid;
                _waitingForRemoval = true;
                SetStatus(
                    AppStatusKind.Error,
                    "Tag abgelehnt – bitte entfernen",
                    evt.ErrorMessage ?? "Das NFC-Tag konnte nicht gelesen werden.",
                    evt.TechnicalDetails);
                break;

            case NfcReaderEventKind.TagRead:
                await HandleTagReadAsync(evt, cancellationToken);
                break;
        }
    }

    private async Task HandleTagReadAsync(NfcReaderEvent evt, CancellationToken cancellationToken)
    {
        LastUid = FormatUid(evt.Uid);
        if (ShouldIgnore(evt.Uid))
        {
            return;
        }

        _processedUid = evt.Uid;
        SetStatus(AppStatusKind.Validating, "JSON wird geprüft…", "Das Tag-JSON wird gegen das Auftragsschema geprüft.");

        var validation = _validator.Validate(evt.Payload);
        PayloadPreview = validation.JsonText ?? evt.Payload ?? string.Empty;
        if (!validation.IsValid || validation.Payload is null)
        {
            _waitingForRemoval = true;
            SetStatus(
                AppStatusKind.Error,
                "Tag abgelehnt – bitte entfernen",
                validation.Reason ?? "Das JSON entspricht nicht dem Schema.",
                validation.TechnicalDetails);
            return;
        }

        SetStatus(AppStatusKind.Creating, "Auftrag wird angelegt…", "Der Auftrag wird an ArianaLab übertragen.");
        var result = await _arianaLabClient.CreateAuftragAsync(
            validation.Payload,
            validation.JsonText ?? evt.Payload ?? string.Empty,
            cancellationToken);

        _waitingForRemoval = true;
        if (result.AlreadyExists)
        {
            SetStatus(
                AppStatusKind.AlreadyExists,
                "Auftrag bereits vorhanden – bitte Tag entfernen",
                result.Message,
                result.TechnicalDetails);
            return;
        }

        SetStatus(
            result.Succeeded ? AppStatusKind.Success : AppStatusKind.Error,
            result.Succeeded ? "Auftrag angelegt – bitte Tag entfernen" : "Fehler – bitte Tag entfernen",
            result.Message,
            result.TechnicalDetails);
    }

    private bool ShouldIgnore(string? uid) =>
        _waitingForRemoval ||
        (!string.IsNullOrWhiteSpace(uid) &&
         string.Equals(_processedUid, uid, StringComparison.OrdinalIgnoreCase));

    private void SetStatus(AppStatusKind kind, string status, string result, string? technical = null)
    {
        StatusKind = kind;
        Status = status;
        ResultMessage = result;
        TechnicalDetails = technical ?? string.Empty;
    }

    private static string FormatUid(string? uid)
    {
        if (string.IsNullOrWhiteSpace(uid))
        {
            return "–";
        }

        var hex = uid.Replace("-", string.Empty).Replace(" ", string.Empty);
        return string.Join(" ", Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2)));
    }
}
