using Baioss.Record.Application.Localization;
using Baioss.Record.Application.Presets;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>Cómo se le cuenta al operador el preset del canal: badge del panel, API y cliente web.</summary>
[Collection("Localizer")] // el resumen va en el idioma de la aplicación (estado global)
public class RecordingProfileSummaryTests : IDisposable
{
    private readonly AppLanguage _original = Localizer.Language;
    public RecordingProfileSummaryTests() => Localizer.Language = AppLanguage.Spanish;
    public void Dispose() => Localizer.Language = _original;

    private static RecordingProfile Profile() => new()
    {
        Name = "MP4 (demo)", VideoCodec = VideoCodec.H264x264, VideoBitrate = Bitrate.FromMbps(8),
        AudioCodec = AudioCodec.Aac, Container = ContainerFormat.Mp4,
    };

    [Fact]
    public void DisplayName_IsThePresetThatWasApplied()
    {
        var p = Profile();
        p.PresetName = "XDCAM HD422 50";

        Assert.Equal("XDCAM HD422 50", RecordingProfileSummary.DisplayName(p));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DisplayName_WithoutAKnownPreset_ShowsTheTechnicalSummary_NotTheInternalProfileName(string? presetName)
    {
        // Perfil sembrado, o una instalación anterior a guardar el nombre: el canal puede llevar meses con un preset
        // ProRes aplicado mientras su perfil interno se sigue llamando «MP4 (demo)». El resumen nunca miente.
        var p = Profile();
        p.VideoCodec = VideoCodec.ProRes;
        p.VideoBitrate = Bitrate.FromMbps(0);
        p.EncoderProfile = EncoderProfile.ProResHq;
        p.Container = ContainerFormat.Mov;
        p.PresetName = presetName;

        Assert.Equal("ProRes · ProResHq · nativa · Mov", RecordingProfileSummary.DisplayName(p));
    }

    [Fact]
    public void Describe_Bitrate_NativeResolution()
        => Assert.Equal("H264x264 · 8 Mbps · nativa · Mp4", RecordingProfileSummary.Describe(Profile()));

    [Fact]
    public void Describe_ConstantQuality_AndScaledResolution()
    {
        var p = Profile();
        p.RateControl = RateControlMode.ConstantQuality;
        p.Quality = 18;
        p.TargetResolution = new Resolution(1920, 1080);

        Assert.Equal($"H264x264 · CRF 18 · {new Resolution(1920, 1080)} · Mp4", RecordingProfileSummary.Describe(p));
    }

    [Fact]
    public void Describe_IntraCodecWithoutBitrate_ShowsItsEncoderProfile_NotZeroKbps()
    {
        // ProRes/DNxHR no llevan bitrate (lo fija el perfil del códec): «0 kbps» no le diría nada al operador.
        var p = Profile();
        p.VideoCodec = VideoCodec.ProRes;
        p.VideoBitrate = Bitrate.FromMbps(0);
        p.EncoderProfile = EncoderProfile.ProResHq;
        p.Container = ContainerFormat.Mov;
        p.TargetResolution = new Resolution(1920, 1080);

        Assert.Equal($"ProRes · ProResHq · {new Resolution(1920, 1080)} · Mov", RecordingProfileSummary.Describe(p));

        p.EncoderProfile = EncoderProfile.Auto;   // ni bitrate ni perfil: el hueco se omite, sin dobles separadores
        Assert.Equal($"ProRes · {new Resolution(1920, 1080)} · Mov", RecordingProfileSummary.Describe(p));
    }

    [Fact]
    public void Describe_AudioOnly_FollowsTheApplicationLanguage()
    {
        var p = Profile();
        p.AudioOnly = true;
        p.AudioCodec = AudioCodec.Pcm;
        p.Container = ContainerFormat.Wav;

        Assert.Equal("Solo audio · Pcm · Wav", RecordingProfileSummary.Describe(p));
        Localizer.Language = AppLanguage.English;
        Assert.Equal("Audio only · Pcm · Wav", RecordingProfileSummary.Describe(p));
    }

    [Fact]
    public void ApplyingAPreset_LeavesItsNameOnTheProfile_EvenWhenTheChannelKeepsItsOwnIdAndName()
    {
        var preset = new EncodingPreset { Name = "H.264 1080p 8 Mbps" };
        var channelProfileId = Guid.NewGuid();

        var profile = preset.ToProfile(channelProfileId, "MP4 (demo)");

        Assert.Equal(channelProfileId, profile.Id);
        Assert.Equal("MP4 (demo)", profile.Name);                  // identidad del perfil del canal: intacta
        Assert.Equal("H.264 1080p 8 Mbps", profile.PresetName);    // pero se sabe qué preset es
        Assert.Equal("H.264 1080p 8 Mbps", RecordingProfileSummary.DisplayName(profile));
    }
}
