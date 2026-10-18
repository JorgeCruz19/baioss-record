using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Application.Localization;
using Baioss.Record.Application.Recording;
using Baioss.Record.Engine.FFmpeg;
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

    /// <summary>Ese vídeo en ese contenedor, con un audio que el contenedor lleva: así lo único que puede fallar es el vídeo.</summary>
    private static RecordingProfile With(VideoCodec codec, ContainerFormat container)
    {
        var p = Valid(); p.VideoCodec = codec; p.Container = container; p.AudioCodec = AudioThatFits(container);
        return p;
    }

    private static AudioCodec AudioThatFits(ContainerFormat container) => container switch
    {
        ContainerFormat.Mxf or ContainerFormat.Wav => AudioCodec.Pcm,
        ContainerFormat.ProgramStream => AudioCodec.Mp2,
        ContainerFormat.Mp3Audio => AudioCodec.Mp3,
        _ => AudioCodec.Aac,
    };

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

    // --- Contenedor × códec de audio ---

    private enum AudioOutcome { Records, Refused, Unreadable }

    /// <summary>
    /// Lo que hizo el motor real (FfmpegChannelEngine, el argv del producto) el 2026-10-18 con FFmpeg 8.0 y con el incluido al
    /// grabar 4 s de un tono con cada códec de audio en cada contenedor, con vídeo y en solo audio, y releer el archivo:
    /// Refused = el muxer no escribe la cabecera y no queda audio; Unreadable = se escribe sin error y no se decodifica (AAC en
    /// WAV). El resto se relee entero; PCM en MP4, MOV y TS, como AAC. FDK-AAC en AVI y WAV lo graba 8.0, pero el incluido lo
    /// rechaza. Además, MXF en solo audio no graba con ningún códec: necesita vídeo (<see cref="AudioOnlyMxf_NeedsAVideoTrack"/>).
    /// </summary>
    private static AudioOutcome MeasuredAudio(AudioCodec audio, ContainerFormat container) => (audio, container) switch
    {
        (AudioCodec.Aac or AudioCodec.FdkAac or AudioCodec.Opus or AudioCodec.Mp3 or AudioCodec.Mp2, ContainerFormat.Mxf) => AudioOutcome.Refused,
        (AudioCodec.Pcm or AudioCodec.Aac or AudioCodec.FdkAac or AudioCodec.Opus, ContainerFormat.ProgramStream) => AudioOutcome.Refused,
        (AudioCodec.Pcm or AudioCodec.Aac or AudioCodec.FdkAac or AudioCodec.Opus or AudioCodec.Mp2, ContainerFormat.Mp3Audio) => AudioOutcome.Refused,
        (AudioCodec.Opus, ContainerFormat.Avi or ContainerFormat.Mov or ContainerFormat.Wav) => AudioOutcome.Refused,
        (AudioCodec.FdkAac, ContainerFormat.Avi or ContainerFormat.Wav) => AudioOutcome.Refused,
        (AudioCodec.Aac, ContainerFormat.Wav) => AudioOutcome.Unreadable,
        _ => AudioOutcome.Records,
    };

    /// <summary>Cada códec de audio en cada contenedor: con vídeo en los siete de vídeo y en solo audio en los nueve.</summary>
    public static TheoryData<AudioCodec, ContainerFormat, bool> EveryAudioCodecInEveryContainer()
    {
        var data = new TheoryData<AudioCodec, ContainerFormat, bool>();
        foreach (var audio in Enum.GetValues<AudioCodec>())
        {
            foreach (var container in VideoContainers) data.Add(audio, container, false);
            foreach (var container in Enum.GetValues<ContainerFormat>()) data.Add(audio, container, true);
        }
        return data;
    }

    /// <summary>Ese audio en ese contenedor, con vídeo H.264 (lo llevan los siete contenedores de vídeo) o en solo audio.</summary>
    private static RecordingProfile WithAudio(AudioCodec audio, ContainerFormat container, bool audioOnly)
    {
        var p = Valid(); p.AudioCodec = audio; p.Container = container; p.AudioOnly = audioOnly;
        return p;
    }

    private const string MxfNeedsVideo = "El contenedor Mxf necesita una pista de vídeo: desmarca «Solo audio» o elige otro contenedor.";

    [Theory]
    [MemberData(nameof(EveryAudioCodecInEveryContainer))]
    public void AudioAndContainer_AreRejected_ExactlyWhereTheEngineCannotRecordThem(AudioCodec audio, ContainerFormat container, bool audioOnly)
    {
        var errors = RecordingProfileValidator.Validate(WithAudio(audio, container, audioOnly)).ToList();
        if (audioOnly && container is ContainerFormat.Mxf)
        {
            // Sin vídeo, MXF no graba con ningún audio: lo dice primero, y después lo del códec si también falla.
            Assert.Equal(MxfNeedsVideo, errors.FirstOrDefault());
            errors.RemoveAt(0);
        }

        switch (MeasuredAudio(audio, container))
        {
            case AudioOutcome.Records:
                Assert.Empty(errors);
                break;
            case AudioOutcome.Refused:
                var refused = Assert.Single(errors);
                Assert.StartsWith($"El audio {audio} no se puede grabar en {container}: ", refused); // el nombre del editor
                Assert.Contains("la grabación no arrancaría", refused);
                break;
            case AudioOutcome.Unreadable:
                var unreadable = Assert.Single(errors);
                Assert.StartsWith($"El audio {audio} no se puede grabar en {container}: ", unreadable);
                Assert.Contains("el audio sale ilegible", unreadable);
                break;
        }
    }

    [Fact]
    public void AacInMxf_SaysWhyAndWhichAudioToChoose_InSpanish()
    {
        var error = Assert.Single(RecordingProfileValidator.Validate(WithAudio(AudioCodec.Aac, ContainerFormat.Mxf, audioOnly: false)));
        Assert.Equal("El audio Aac no se puede grabar en Mxf: FFmpeg no admite esa combinación y la grabación no arrancaría. " +
                     "En Mxf, elige el audio Pcm.", error);
    }

    [Fact]
    public void OpusInProgramStream_SaysWhyAndWhichAudioToChoose_InEnglish()
    {
        Localizer.Language = AppLanguage.English;
        var error = Assert.Single(RecordingProfileValidator.Validate(WithAudio(AudioCodec.Opus, ContainerFormat.ProgramStream, audioOnly: false)));
        Assert.Equal("Opus audio can't be recorded in ProgramStream: FFmpeg doesn't support that combination, so the recording " +
                     "wouldn't start. In ProgramStream, choose Mp2 or Mp3 audio.", error);
    }

    [Fact]
    public void AacInWav_SaysTheAudioComesOutUnreadable()
    {
        var error = Assert.Single(RecordingProfileValidator.Validate(WithAudio(AudioCodec.Aac, ContainerFormat.Wav, audioOnly: true)));
        Assert.Equal("El audio Aac no se puede grabar en Wav: FFmpeg escribe el archivo sin dar error, pero el audio sale ilegible. " +
                     "En Wav, elige el audio Pcm.", error);
    }

    [Fact]
    public void AudioOnlyMxf_NeedsAVideoTrack()
    {
        // El muxer MXF exige que la primera pista sea de vídeo: en solo audio no graba ni con PCM. Con un audio que MXF tampoco
        // lleva, los dos motivos.
        Assert.Equal(MxfNeedsVideo, Assert.Single(RecordingProfileValidator.Validate(WithAudio(AudioCodec.Pcm, ContainerFormat.Mxf, audioOnly: true))));
        var both = RecordingProfileValidator.Validate(WithAudio(AudioCodec.Aac, ContainerFormat.Mxf, audioOnly: true));
        Assert.Equal(2, both.Count);
        Assert.Equal(MxfNeedsVideo, both[0]);
        Assert.StartsWith("El audio Aac no se puede grabar en Mxf: ", both[1]);
        // Con vídeo, el PCM sí va en MXF.
        Assert.Empty(RecordingProfileValidator.Validate(WithAudio(AudioCodec.Pcm, ContainerFormat.Mxf, audioOnly: false)));
    }

    [Theory]
    [MemberData(nameof(EveryAudioCodecInEveryContainer))]
    public void TheAudioTheMessageSuggests_DoesRecordInThatContainer(AudioCodec audio, ContainerFormat container, bool audioOnly)
    {
        Localizer.Language = AppLanguage.English;
        string? message = RecordingProfileValidator.Validate(WithAudio(audio, container, audioOnly))
            .SingleOrDefault(e => e.StartsWith($"{audio} audio can't be recorded", StringComparison.Ordinal));
        if (message is null) return; // este audio cabe en el contenedor: no hay nada que proponer

        // «… In ProgramStream, choose Mp2 or Mp3 audio.»: con cada propuesta el audio deja de ser un problema; solo puede quedar
        // que MXF necesita vídeo, que es otro motivo con su propio mensaje.
        int from = message.LastIndexOf("choose ", StringComparison.Ordinal) + "choose ".Length;
        string list = message[from..message.LastIndexOf(" audio.", StringComparison.Ordinal)];
        var suggested = list.Replace(" or ", ", ", StringComparison.Ordinal).Split(", ").Select(s => Enum.Parse<AudioCodec>(s)).ToList();
        Assert.NotEmpty(suggested);
        foreach (var alternative in suggested)
            Assert.All(RecordingProfileValidator.Validate(WithAudio(alternative, container, audioOnly)),
                e => Assert.StartsWith("The Mxf container needs a video track", e));
    }

    [Theory]
    [InlineData(ContainerFormat.Mp4)]
    [InlineData(ContainerFormat.Mov)]
    [InlineData(ContainerFormat.Ts)]
    public void PcmInMp4MovOrTs_Passes_BecauseTheEngineRecordsItAsAac(ContainerFormat container)
    {
        // PCM s24le no cabe en estos tres (el TS lo guardaría como datos privados), pero el motor lo graba como AAC. Si dejara de
        // promoverlo, esta prueba avisa: el validador tendría que rechazarlo.
        Assert.Equal("aac", FfmpegCodecMap.EffectiveAudioEncoder(AudioCodec.Pcm, container));
        Assert.Empty(RecordingProfileValidator.Validate(WithAudio(AudioCodec.Pcm, container, audioOnly: false)));
    }

    [Fact]
    public void PcmInProgramStream_IsRejected_BecauseTheEngineKeepsIt24Bit()
    {
        // El PS de FFmpeg solo lleva PCM de 16 bits (pcm_dvd, pcm_s16be) y el motor no promueve ahí el PCM. Si empezara a hacerlo,
        // esta prueba avisa: el validador estaría rechazando algo que ya graba.
        Assert.Equal("pcm_s24le", FfmpegCodecMap.EffectiveAudioEncoder(AudioCodec.Pcm, ContainerFormat.ProgramStream));
        Assert.NotEmpty(RecordingProfileValidator.Validate(WithAudio(AudioCodec.Pcm, ContainerFormat.ProgramStream, audioOnly: false)));
    }
}
