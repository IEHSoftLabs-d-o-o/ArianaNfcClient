using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ArianaNfcClient.Models;
using ArianaNfcClient.Services.ArianaLab;
using ArianaNfcClient.Services.Validation;

namespace ArianaNfcClient.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ITagPayloadValidator _validator;
    private readonly IArianaLabClient _arianaLabClient;
    private readonly ILogger<MainViewModel> _logger;
    private string? _processedUid;
    private bool _waitingForRemoval;

    public MainViewModel(
        ITagPayloadValidator validator,
        IArianaLabClient arianaLabClient,
        IOptions<ArianaLabOptions> arianaLabOptions,
        ILogger<MainViewModel> logger)
    {
        _validator = validator;
        _arianaLabClient = arianaLabClient;
        _logger = logger;
        var options = arianaLabOptions.Value;
        BaseUrl = string.IsNullOrWhiteSpace(options.BaseUrl) ? "–" : options.BaseUrl;
        Username = string.IsNullOrWhiteSpace(options.Username) ? "–" : options.Username;
        StatusKind = AppStatusKind.Waiting;
        Status = "Warten auf NFC-Tag…";
        ResultMessage = "Legen Sie ein NFC-Tag auf den Leser.";
    }

    public string BaseUrl { get; }

    public string Username { get; }

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
                _logger.LogWarning(
                    "Tag read failed. UID {Uid}. {Message}. {Details}",
                    evt.Uid,
                    evt.ErrorMessage,
                    evt.TechnicalDetails);
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
        var scannedJson = evt.Payload ?? string.Empty;
        _logger.LogInformation("Scanned JSON. UID {Uid}:{NewLine}{Json}", evt.Uid, Environment.NewLine, scannedJson);
        SetStatus(AppStatusKind.Validating, "JSON wird geprüft…", "Das Tag-JSON wird gegen das Auftragsschema geprüft.");

        var validation = _validator.Validate(evt.Payload);
        PayloadPreview = validation.JsonText ?? scannedJson;
        if (!validation.IsValid || validation.Payload is null)
        {
            _waitingForRemoval = true;
            _logger.LogWarning(
                "Tag JSON rejected. UID {Uid}. {Reason}. {Details}",
                evt.Uid,
                validation.Reason,
                validation.TechnicalDetails);
            SetStatus(
                AppStatusKind.Error,
                "Tag abgelehnt – bitte entfernen",
                validation.Reason ?? "Das JSON entspricht nicht dem Schema.",
                validation.TechnicalDetails);
            return;
        }

        var protocolId = validation.Payload.Protokoll?.Id;
        SetStatus(AppStatusKind.Creating, "Auftrag wird angelegt…", "Der Auftrag wird an ArianaLab übertragen.");
        var result = await _arianaLabClient.CreateAuftragAsync(
            validation.Payload,
            validation.JsonText ?? scannedJson,
            cancellationToken);

        _waitingForRemoval = true;
        if (result.AlreadyExists)
        {
            _logger.LogWarning(
                "Auftrag already exists. Id {AuftragId}. UID {Uid}. Protocol {ProtocolId}. {Message}",
                result.AuftragNummer,
                evt.Uid,
                protocolId,
                result.Message);
            SetStatus(
                AppStatusKind.AlreadyExists,
                "Auftrag bereits vorhanden – bitte Tag entfernen",
                result.Message,
                result.TechnicalDetails);
            return;
        }

        if (result.Succeeded)
        {
            _logger.LogInformation(
                "Auftrag created. Id {AuftragId}. UID {Uid}. Protocol {ProtocolId}",
                result.AuftragNummer,
                evt.Uid,
                protocolId);
        }
        else
        {
            _logger.LogError(
                "Auftrag creation failed. UID {Uid}. Protocol {ProtocolId}. {Message}. {Details}",
                evt.Uid,
                protocolId,
                result.Message,
                result.TechnicalDetails);
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
