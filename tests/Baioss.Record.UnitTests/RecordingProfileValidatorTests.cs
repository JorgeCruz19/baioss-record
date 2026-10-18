using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Application.Localization;
using Baioss.Record.Application.Recording;
using Xunit;

namespace Baioss.Record.UnitTests;

[Collection("Localizer")] // los mensajes salen en el idioma vigente (estado global)
public class RecordingProfileValidatorTests : IDisposable
{
    private readonly AppLanguage _original = Localizer.Language;
    public RecordingProfileValidatorTests() => Localizer.Language = AppLanguage.Spanish;
    public void Dispose() => Localizer.Language = _original;

    private static RecordingProfile Valid() => new()
    {
        Name = "ok", VideoCodec = VideoCodec.H264x264, HwAccel = HwAccel.None,
        VideoBitrate = Bitrate.FromMbps(8), GopSize = 50, RateControl = RateControlMode.ConstantBitrate,
        AudioCodec = AudioCodec.Aac, AudioBitrate = Bitrate.FromKbps(256), AudioSampleRate = 48_000,
        Container = ContainerFormat.Mp4,
    };

    [Fact]
    public void Valid_ProfileHasNoErrors() => Assert.Empty(RecordingProfileValidator.Validate(Valid()));

    [Fact]
    public void Zero_VideoBitrate_IsRejected()
    {
        var p = Valid(); p.VideoBitrate = new Bitrate(0);
        Assert.NotEmpty(RecordingProfileValidator.Validate(p));
    }

    [Fact]
    public void Odd_Resolution_IsRejected()
    {
        var p = Valid(); p.TargetResolution = new Resolution(1281, 720);
        Assert.Contains(RecordingProfileValidator.Validate(p), e => e.Contains("PARES"));
    }

    [Fact]
    public void Messages_FollowTheApplicationLanguage()
    {
        // Lo ve el operador (editor de presets, badge del canal, aviso al pulsar Grabar): no puede salir en español con la
        // interfaz en inglés.
        var p = Valid(); p.TargetResolution = new Resolution(1281, 720);
        Localizer.Language = AppLanguage.English;
        Assert.Contains(RecordingProfileValidator.Validate(p), e => e.Contains("EVEN"));
    }

    [Fact]
    public void Zero_Gop_IsRejected()
    {
        var p = Valid(); p.GopSize = 0;
        Assert.NotEmpty(RecordingProfileValidator.Validate(p));
    }

    [Fact]
    public void Crf_OutOfRange_IsRejected()
    {
        var p = Valid(); p.RateControl = RateControlMode.ConstantQuality; p.Quality = 99;
        Assert.NotEmpty(RecordingProfileValidator.Validate(p));
    }

    [Fact]
    public void MaxBitrate_BelowVideoBitrate_IsRejected()
    {
        var p = Valid(); p.VideoBitrate = Bitrate.FromMbps(8); p.MaxBitrate = Bitrate.FromMbps(4);
        Assert.NotEmpty(RecordingProfileValidator.Validate(p));
    }

    [Fact]
    public void Zero_AudioBitrate_IsRejected_ForCompressedAudio()
    {
        var p = Valid(); p.AudioCodec = AudioCodec.Aac; p.AudioBitrate = new Bitrate(0);
        Assert.NotEmpty(RecordingProfileValidator.Validate(p));
    }

    [Fact]
    public void Intra_ZeroBitrate_IsAllowed()
    {
        // ProRes va por -profile, no por -b:v: un bitrate 0 NO es un error en un códec intra.
        var p = Valid();
        p.VideoCodec = VideoCodec.ProRes; p.VideoBitrate = new Bitrate(0); p.Container = ContainerFormat.Mov;
        Assert.Empty(RecordingProfileValidator.Validate(p));
    }

    [Fact]
    public void ConstantQuality_ZeroBitrate_IsAllowed()
    {
        // CRF/CQ no usa -b:v: bitrate 0 es legítimo.
        var p = Valid();
        p.RateControl = RateControlMode.ConstantQuality; p.Quality = 23; p.VideoBitrate = new Bitrate(0);
        Assert.Empty(RecordingProfileValidator.Validate(p));
    }

    // --- Contenedor × códec de vídeo ---

    private static readonly ContainerFormat[] VideoContainers =
    {
        ContainerFormat.Mp4, ContainerFormat.Mov, ContainerFormat.Mxf, ContainerFormat.Mkv,
        ContainerFormat.Ts, ContainerFormat.Avi, ContainerFormat.ProgramStream,
    };

    private enum Outcome { Records, Refused, Unreadable }

    /// <summary>
    /// Lo que hizo FFmpeg (8.0 y el incluido, iguales) el 2026-10-18 al grabar 2 s con cada códec en cada contenedor y releer
    /// el archivo: Refused = el muxer lo rechaza y la grabación no arranca; Unreadable = lo escribe sin error y se relee como
    /// rawvideo (HEVC en AVI) o como datos privados/desconocidos (AV1, ProRes, DNxHD/HR en TS y PS). El resto se relee entero.
    /// </summary>
    private static Outcome Measured(VideoCodec codec, ContainerFormat container) => (codec, container) switch
    {
        (VideoCodec.H265x265 or VideoCodec.HevcQsv or VideoCodec.HevcNvenc, ContainerFormat.Avi) => Outcome.Unreadable,
        (VideoCodec.H265x265 or VideoCodec.HevcQsv or VideoCodec.HevcNvenc, ContainerFormat.Mxf) => Outcome.Refused,
        (VideoCodec.Av1Nvenc, ContainerFormat.Mxf or ContainerFormat.Mov) => Outcome.Refused,
        (VideoCodec.Av1Nvenc, ContainerFormat.Ts or ContainerFormat.ProgramStream) => Outcome.Unreadable,
        (VideoCodec.ProRes or VideoCodec.DnxHd or VideoCodec.DnxHr, ContainerFormat.Mp4) => Outcome.Refused,
        (VideoCodec.ProRes or VideoCodec.DnxHd or VideoCodec.DnxHr, ContainerFormat.Ts or ContainerFormat.ProgramStream) => Outcome.Unreadable,
        _ => Outcome.Records,
    };

    public static TheoryData<VideoCodec, ContainerFormat> EveryVideoCodecInEveryVideoContainer()
    {
        var data = new TheoryData<VideoCodec, ContainerFormat>();
        foreach (var codec in Enum.GetValues<VideoCodec>())
            foreach (var container in VideoContainers)
                data.Add(codec, container);
        return data;
    }

    private static RecordingProfile With(VideoCodec codec, ContainerFormat container)
    {
        var p = Valid(); p.VideoCodec = codec; p.Container = container;
        return p;
    }

    [Theory]
    [MemberData(nameof(EveryVideoCodecInEveryVideoContainer))]
    public void ContainerAndCodec_AreRejected_ExactlyWhereFfmpegCannotRecordThem(VideoCodec codec, ContainerFormat container)
    {
        var errors = RecordingProfileValidator.Validate(With(codec, container));

        switch (Measured(codec, container))
        {
            case Outcome.Records:
                Assert.Empty(errors);
                break;
            case Outcome.Refused:
                var refused = Assert.Single(errors);
                Assert.Contains($"no se puede grabar en {container}", refused);
                Assert.Contains("la grabación no arrancaría", refused);
                Assert.Contains(codec.ToString(), refused); // el nombre que el operador eligió en el editor
                break;
            case Outcome.Unreadable:
                var unreadable = Assert.Single(errors);
                Assert.Contains($"no se puede grabar en {container}", unreadable);
                Assert.Contains("sale ilegible", unreadable);
                Assert.Contains(codec.ToString(), unreadable);
                break;
        }
    }

    [Fact]
    public void HevcInAvi_SaysWhyAndWhatToChoose_InSpanish()
    {
        var error = Assert.Single(RecordingProfileValidator.Validate(With(VideoCodec.HevcQsv, ContainerFormat.Avi)));
        Assert.Equal("HevcQsv (HEVC) no se puede grabar en Avi: FFmpeg escribe el archivo sin dar error, pero sale ilegible. " +
                     "Para HEVC, elige Mp4, Mov, Mkv o Ts.", error);
    }

    [Fact]
    public void HevcInMxf_SaysWhyAndWhatToChoose_InEnglish()
    {
        Localizer.Language = AppLanguage.English;
        var error = Assert.Single(RecordingProfileValidator.Validate(With(VideoCodec.H265x265, ContainerFormat.Mxf)));
        Assert.Equal("H265x265 (HEVC) can't be recorded in Mxf: FFmpeg doesn't support that combination, so the recording " +
                     "wouldn't start. For HEVC, choose Mp4, Mov, Mkv or Ts.", error);
    }

    [Fact]
    public void ACodecNamedLikeItsFamily_IsNotRepeated()
    {
        var error = Assert.Single(RecordingProfileValidator.Validate(With(VideoCodec.ProRes, ContainerFormat.Mp4)));
        Assert.StartsWith("ProRes no se puede grabar en Mp4:", error);
    }

    [Theory]
    [MemberData(nameof(EveryVideoCodecInEveryVideoContainer))]
    public void TheContainersTheMessageSuggests_DoRecordThatCodec(VideoCodec codec, ContainerFormat container)
    {
        Localizer.Language = AppLanguage.English;
        var errors = RecordingProfileValidator.Validate(With(codec, container));
        if (errors.Count == 0) return; // esta combinación graba: no hay nada que proponer

        // «… For HEVC, choose Mp4, Mov, Mkv or Ts.»: cada propuesta tiene que pasar la validación con el mismo códec.
        string message = Assert.Single(errors);
        string list = message[(message.LastIndexOf("choose ", StringComparison.Ordinal) + "choose ".Length)..].TrimEnd('.');
        var suggested = list.Replace(" or ", ", ", StringComparison.Ordinal).Split(", ").Select(s => Enum.Parse<ContainerFormat>(s)).ToList();
        Assert.True(suggested.Count >= 2, message);
        foreach (var alternative in suggested)
            Assert.Empty(RecordingProfileValidator.Validate(With(codec, alternative)));
    }

    [Theory]
    [InlineData(ContainerFormat.Wav)]
    [InlineData(ContainerFormat.Mp3Audio)]
    public void VideoInAnAudioOnlyContainer_IsRejected(ContainerFormat container)
    {
        // El muxer WAV rechaza la pista de vídeo («wav muxer does not support any stream of type video») y el MP3 falla al
        // escribirla: sin «Solo audio», un perfil con WAV/MP3 no graba.
        var error = Assert.Single(RecordingProfileValidator.Validate(With(VideoCodec.H264x264, container)));
        Assert.Equal($"El contenedor {container} solo lleva audio: marca «Solo audio» o elige un contenedor de vídeo.", error);
    }

    [Theory]
    [InlineData(ContainerFormat.Wav, AudioCodec.Pcm)]
    [InlineData(ContainerFormat.Mp3Audio, AudioCodec.Mp3)]
    [InlineData(ContainerFormat.Avi, AudioCodec.Pcm)] // el códec de vídeo que quedara en un perfil de solo audio no cuenta
    public void AudioOnlyProfiles_IgnoreTheVideoCodec(ContainerFormat container, AudioCodec audio)
    {
        var p = With(VideoCodec.HevcQsv, container);
        p.AudioOnly = true; p.AudioCodec = audio;
        Assert.Empty(RecordingProfileValidator.Validate(p));
    }
}
