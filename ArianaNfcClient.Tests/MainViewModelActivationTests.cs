using ArianaNfcClient.Models;
using ArianaNfcClient.Services.Validation;
using ArianaNfcClient.Tests.Support;
using ArianaNfcClient.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ArianaNfcClient.Tests;

public class MainViewModelActivationTests
{
    private const string Uid = "04AABBCCDD";

    private sealed record Harness(
        MainViewModel ViewModel,
        FakeArianaLabClient Client,
        List<(string Message, string? Url)> ActivationFailures);

    private static Harness CreateHarness(
        bool autoActivate = true,
        bool deleteOnFailure = false,
        Action<FakeArianaLabClient>? configure = null)
    {
        var client = new FakeArianaLabClient();
        configure?.Invoke(client);
        var options = new ArianaLabOptions
        {
            AutoActivateAuftrag = autoActivate,
            DeleteAuftragOnActivationFailure = deleteOnFailure
        };
        var viewModel = new MainViewModel(
            new TagPayloadValidator(),
            client,
            new FakeConnectionSettingsStore(),
            Options.Create(options),
            NullLogger<MainViewModel>.Instance);
        var failures = new List<(string Message, string? Url)>();
        viewModel.SetActivationFailedHandler((message, url) => failures.Add((message, url)));
        return new Harness(viewModel, client, failures);
    }

    private static NfcReaderEvent TagRead(string? payload = null, string uid = Uid) =>
        new(NfcReaderEventKind.TagRead, "Reader", uid, payload ?? TagJson.Create());

    private static NfcReaderEvent TagRemoved(string uid = Uid) =>
        new(NfcReaderEventKind.TagRemoved, "Reader", uid);

    [Fact]
    public void AutoActivateOn_CreatesAndActivatesOnce() => DispatcherRunner.Run(async () =>
    {
        var h = CreateHarness(autoActivate: true);

        await h.ViewModel.HandleEventAsync(TagRead(), CancellationToken.None);

        Assert.Equal(["Create", "Activate:A-1"], h.Client.Calls);
        Assert.Equal(AppStatusKind.Success, h.ViewModel.StatusKind);
        Assert.Equal("Probe angelegt – bitte Tag entfernen", h.ViewModel.Status);
        Assert.Empty(h.ActivationFailures);
    });

    [Fact]
    public void AutoActivateOff_OnlyCreates() => DispatcherRunner.Run(async () =>
    {
        var h = CreateHarness(autoActivate: false);

        await h.ViewModel.HandleEventAsync(TagRead(), CancellationToken.None);

        Assert.Equal(["Create"], h.Client.Calls);
        Assert.Equal(AppStatusKind.Success, h.ViewModel.StatusKind);
        Assert.Equal("Auftrag angelegt – bitte Tag entfernen", h.ViewModel.Status);
    });

    [Theory]
    [InlineData(false, "Auftrag bereits vorhanden – bitte Tag entfernen")]
    [InlineData(true, "Probe bereits vorhanden – bitte Tag entfernen")]
    public void ExistingAuftrag_IsNotActivatedAgain(bool isActivated, string expectedStatus) =>
        DispatcherRunner.Run(async () =>
        {
            var h = CreateHarness(configure: c => c.CreateResult = new AuftragCreateResult(
                false,
                "exists",
                null,
                "A-1",
                AlreadyExists: true,
                IsActivated: isActivated));

            await h.ViewModel.HandleEventAsync(TagRead(), CancellationToken.None);

            Assert.Equal(["Create"], h.Client.Calls);
            Assert.Equal(AppStatusKind.AlreadyExists, h.ViewModel.StatusKind);
            Assert.Equal(expectedStatus, h.ViewModel.Status);
        });

    [Fact]
    public void CreatedWithoutId_IsReportedAndNotActivated() => DispatcherRunner.Run(async () =>
    {
        var h = CreateHarness(configure: c => c.CreateResult = new AuftragCreateResult(true, "created", null));

        await h.ViewModel.HandleEventAsync(TagRead(), CancellationToken.None);

        Assert.Equal(["Create"], h.Client.Calls);
        Assert.Equal(AppStatusKind.Error, h.ViewModel.StatusKind);
        Assert.Equal("Fehler – bitte Tag entfernen", h.ViewModel.Status);
        Assert.Equal("Der Auftrag wurde angelegt, konnte aber nicht aktiviert werden.", h.ViewModel.ResultMessage);
    });

    [Fact]
    public void CreateFailure_DoesNotActivate() => DispatcherRunner.Run(async () =>
    {
        var h = CreateHarness(configure: c => c.CreateResult = new AuftragCreateResult(false, "rejected", "details"));

        await h.ViewModel.HandleEventAsync(TagRead(), CancellationToken.None);

        Assert.Equal(["Create"], h.Client.Calls);
        Assert.Equal(AppStatusKind.Error, h.ViewModel.StatusKind);
        Assert.Equal("Fehler – bitte Tag entfernen", h.ViewModel.Status);
        Assert.Equal("rejected", h.ViewModel.ResultMessage);
    });

    [Fact]
    public void ActivationFailure_WithDeleteOn_DeletesTheAuftragAndNotifies() => DispatcherRunner.Run(async () =>
    {
        var h = CreateHarness(deleteOnFailure: true, configure: c =>
            c.ActivateResult = new AuftragCreateResult(false, "Livecheck fehlgeschlagen", "details", "A-1"));

        await h.ViewModel.HandleEventAsync(TagRead(), CancellationToken.None);

        Assert.Equal(["Create", "Activate:A-1", "Delete:A-1"], h.Client.Calls);
        Assert.Equal(AppStatusKind.Error, h.ViewModel.StatusKind);
        var failure = Assert.Single(h.ActivationFailures);
        Assert.Equal("Livecheck fehlgeschlagen", failure.Message);
        Assert.Equal("https://lab.example/Opd/Probenanlage/Auftraege/A-1", failure.Url);
    });

    [Fact]
    public void ActivationFailure_WithDeleteOff_KeepsTheAuftragButNotifies() => DispatcherRunner.Run(async () =>
    {
        var h = CreateHarness(deleteOnFailure: false, configure: c =>
            c.ActivateResult = new AuftragCreateResult(false, "Livecheck fehlgeschlagen", "details", "A-1"));

        await h.ViewModel.HandleEventAsync(TagRead(), CancellationToken.None);

        Assert.Equal(["Create", "Activate:A-1"], h.Client.Calls);
        Assert.Equal(AppStatusKind.Error, h.ViewModel.StatusKind);
        Assert.Single(h.ActivationFailures);
    });

    [Fact]
    public void InvalidTag_IsRejectedWithoutCallingArianaLab() => DispatcherRunner.Run(async () =>
    {
        var h = CreateHarness();

        await h.ViewModel.HandleEventAsync(TagRead("not json"), CancellationToken.None);

        Assert.Empty(h.Client.Calls);
        Assert.Equal(AppStatusKind.Error, h.ViewModel.StatusKind);
        Assert.Equal("Tag abgelehnt – bitte entfernen", h.ViewModel.Status);
    });

    [Fact]
    public void SameTagIsIgnoredUntilItWasRemoved() => DispatcherRunner.Run(async () =>
    {
        using var cts = new CancellationTokenSource();
        var h = CreateHarness();

        await h.ViewModel.HandleEventAsync(TagRead(), cts.Token);
        await h.ViewModel.HandleEventAsync(TagRead(), cts.Token);
        Assert.Equal(1, h.Client.CreateCalls);

        await h.ViewModel.HandleEventAsync(TagRemoved(), cts.Token);
        await h.ViewModel.HandleEventAsync(TagRead(), cts.Token);
        Assert.Equal(2, h.Client.CreateCalls);

        await cts.CancelAsync();
    });
}
